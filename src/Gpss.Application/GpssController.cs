using System.Collections.Immutable;
using System.Diagnostics;
using Gpss.Core;
using Gpss.Input;

namespace Gpss.Application;

/// <summary>Serializes acquisition sessions; one background writer owns all analysis windows.</summary>
public sealed class GpssController : IAsyncDisposable
{
    private readonly Func<BackendKind, IInputSource> _factory;
    private readonly IMonotonicClock _clock;
    private readonly EstimatorRegistry _registry;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<Axis, LiveAxisWindow> _windows;
    private readonly Dictionary<Axis, AxisSample> _latest = new();
    private readonly Queue<(long Time, long Observations, long Calls)> _rates = new();
    private IStepEstimator _estimator;
    private ApplicationSnapshot _current;
    private IInputSource? _source;
    private DeviceInfo? _device;
    private AxisMapping _mapping = AxisMapping.Empty;
    private AcquisitionSettings _settings = new();
    private int _measurementWindowSeconds = MeasurementWindow.DefaultSeconds, _workerWindowSeconds;
    private Task? _worker;
    private long _epoch, _workerEpoch = -1, _analysisAt = long.MinValue;
    private long? _started;
    private int _forceAnalysis = 1;
    private BackendKind _backend = BackendKind.HidDirect;
    private AnalysisSnapshot? _analysis;
    private ImmutableDictionary<Axis, AxisWindowSnapshot> _history = ImmutableDictionary<Axis, AxisWindowSnapshot>.Empty;
    private StepHistogramSnapshot? _stepHistogram;
    private readonly StepHistogramCounts _combinedStepHistogram = new();
    private TimeSpan _cpuAt;
    private long _cpuTime;
    private double _cpuPercent;
    private bool _disposed, _wasConnected;
    private sealed record EpochWork(long Epoch, CancellationTokenSource Cancellation, CancellationToken Token, int WindowSeconds);
    private EpochWork _epochWork;
    private string[] _discoveryWarnings = [];
    private long _analysisRevision, _historyRevision, _analysisCompleted;
    private double _analysisDurationMs;
    private long _analysisSampleCount, _historyPointCount;
    private readonly object _displayGate = new();
    private InputPositionSnapshot? _displayPosition;
    private ImmutableDictionary<Axis, AxisSample> _displayLatest = ImmutableDictionary<Axis, AxisSample>.Empty;
    private ImmutableDictionary<Axis, AxisSample> _publishedLatest = ImmutableDictionary<Axis, AxisSample>.Empty;


    public GpssController(Func<BackendKind, IInputSource> factory,
        IMonotonicClock? clock = null, EstimatorRegistry? registry = null)
    {
        _factory = factory; _clock = clock ?? StopwatchClock.Instance;
        var initialCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        _epochWork = new(0, initialCancellation, initialCancellation.Token, MeasurementWindow.DefaultSeconds);
        _registry = registry ?? EstimatorRegistry.CreateDefault();
        _estimator = _registry.Create(_registry.Ids[0]);
        _windows = new(); // The worker owns creation, resizing and all writes.
        _current = new(new(0, ConnectionState.Searching, Message: "機器を選択してください"),
            _settings, _backend, null, ImmutableDictionary<Axis, AxisSample>.Empty, new(),
            _clock.Timestamp, _clock.Frequency, 0, 0, 0, _estimator.MethodId, 0);
    }

    public ApplicationSnapshot Current => Volatile.Read(ref _current);
    /// <summary>UI-only bounded read: latest observed position does not wait for analysis/queue draining.</summary>
    public ApplicationSnapshot GetDisplaySnapshot()
    {
        var snapshot = Current;
        var source = Volatile.Read(ref _source);
        var epoch = Interlocked.Read(ref _epoch);
        if (snapshot.Status.Epoch != epoch) return Current;
        if (snapshot.Status.State != ConnectionState.Connected || source is not ILatestPositionSource live)
            return snapshot with { Now = _clock.Timestamp };
        var sourceStatus = source.Status;
        if (sourceStatus.Epoch != epoch) return Current;
        if (sourceStatus.State != ConnectionState.Connected)
            return snapshot with { Status = sourceStatus, Latest = ImmutableDictionary<Axis, AxisSample>.Empty, Analysis = null, History = null, StepHistogram = null, Now = _clock.Timestamp };
        var position = live.LatestPosition;
        if (position is null || position.Epoch != epoch) return snapshot with { Now = _clock.Timestamp };
        lock (_displayGate)
        {
            if (!ReferenceEquals(position, _displayPosition))
            {
                var builder = ImmutableDictionary.CreateBuilder<Axis, AxisSample>();
                if (position.LX is {} lx) builder.Add(Axis.LX, lx);
                if (position.LY is {} ly) builder.Add(Axis.LY, ly);
                if (position.RX is {} rx) builder.Add(Axis.RX, rx);
                if (position.RY is {} ry) builder.Add(Axis.RY, ry);
                _displayLatest = builder.ToImmutable();
                _displayPosition = position;
            }
            if (epoch != Interlocked.Read(ref _epoch)) return Current;
            var displayNow = _clock.Timestamp;
            var performance = snapshot.Performance;
            if (performance is not null && source is IInputBufferDiagnostics buffered)
            {
                var queue = buffered.BufferDiagnostics;
                performance = performance with { QueueCount = queue.Count, QueueBytes = queue.Bytes,
                    QueueOldestAgeMs = queue.OldestTimestamp is long first ? Math.Max(0, (displayNow-first)*1000d/_clock.Frequency) : 0 };
            }
            return snapshot with { Latest = _displayLatest, Now = displayNow, Performance = performance };
        }
    }
    public AxisMapping CurrentMapping => Volatile.Read(ref _mapping);
    public IReadOnlyList<string> EstimatorIds => _registry.Ids;
    public IReadOnlyList<string> DiscoveryWarnings => Volatile.Read(ref _discoveryWarnings);
    public void Start() => _worker ??= Task.Run(() => RunAsync(_stopping.Token));

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(BackendKind backend)
    {
        await using var catalog = _factory(backend);
        var devices = await catalog.EnumerateAsync(_stopping.Token).ConfigureAwait(false);
        Volatile.Write(ref _discoveryWarnings, catalog is IInputDiscoveryDiagnostics info ? info.DiscoveryErrors.ToArray() : []);
        return devices;
    }

    public async Task SelectAsync(DeviceInfo device, AxisMapping? mapping = null, AcquisitionSettings? settings = null)
    {
        await _changes.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try { await SelectCoreAsync(device, mapping, settings).ConfigureAwait(false); }
        finally { _changes.Release(); }
    }

    public async Task SetMeasurementWindowAsync(int seconds)
    {
        MeasurementWindow.Validate(seconds);
        await _changes.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_measurementWindowSeconds == seconds) return;
            _measurementWindowSeconds = seconds;
            if (_device is { } device)
                await SelectCoreAsync(device, CurrentMapping, _settings).ConfigureAwait(false);
            else
            {
                var epoch = AdvanceEpoch();
                Volatile.Write(ref _current, Current with
                {
                    Status = new(epoch, ConnectionState.Searching, Message: "機器を選択してください"),
                    MeasurementWindowSeconds = seconds, Analysis = null, History = null, StepHistogram = null,
                    Latest = ImmutableDictionary<Axis, AxisSample>.Empty, Diagnostics = new(),
                    ElapsedSeconds = 0, ObservationRate = 0, ApiCallRate = 0
                });
            }
        }
        finally { _changes.Release(); }
    }

    private async Task SelectCoreAsync(DeviceInfo device, AxisMapping? mapping, AcquisitionSettings? settings)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var selectedMapping = mapping ?? SuggestedMapping.Create(device);
            var epoch = AdvanceEpoch();
            _backend = device.Backend;
            _device = device;
            Volatile.Write(ref _mapping, selectedMapping);
            _settings = (settings ?? _settings) with { Mode = AcquisitionMode.LowLatency };
            PublishChanging(epoch, device);
            if (_source is not null)
            {
                await _source.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _source, null);
            }
            if (selectedMapping.Bindings.Length != 4)
            {
                Volatile.Write(ref _current, Current with
                { Status = new(epoch, ConnectionState.Unsupported, device, "軸割当が必要です。軸の診断・割当を開いてください") });
                return;
            }
            SuggestedMapping.Validate(device, selectedMapping);
            var source = _factory(device.Backend);
            Volatile.Write(ref _source, source);
            await source.StartAsync(device, selectedMapping, _settings, epoch, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Volatile.Write(ref _current, Current with
            { Status = new(_epoch, ConnectionState.Error, device, ex.Message), Analysis = null, History = null, StepHistogram = null,
              Latest = ImmutableDictionary<Axis, AxisSample>.Empty });
        }
    }

    public async Task ClearSelectionAsync(BackendKind backend)
    {
        await _changes.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            var epoch = AdvanceEpoch();
            _backend = backend; _device = null;
            Volatile.Write(ref _mapping, AxisMapping.Empty);
            Volatile.Write(ref _current, Current with
            { Status = new(epoch, ConnectionState.Searching, Message: "機器を選択してください"),
              Backend = backend, Analysis = null, History = null, StepHistogram = null, Latest = ImmutableDictionary<Axis, AxisSample>.Empty,
              Diagnostics = new(), ObservationRate = 0, ApiCallRate = 0, ElapsedSeconds = 0 });
            var previous = Volatile.Read(ref _source);
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _source, null);
        }
        finally { _changes.Release(); }
    }

    private void PublishChanging(long epoch, DeviceInfo device)
    {
        Volatile.Write(ref _current, Current with
        { Status = new(epoch, ConnectionState.Searching, device, "入力を切り替えています"),
          Backend = device.Backend, Settings = _settings, Analysis = null, History = null, StepHistogram = null,
          MeasurementWindowSeconds = _measurementWindowSeconds,
          Latest = ImmutableDictionary<Axis, AxisSample>.Empty, Diagnostics = new(),
          ObservationRate = 0, ApiCallRate = 0, ElapsedSeconds = 0 });
    }

    public Task ApplyMappingAsync(AxisMapping mapping)
        => SelectAsync(_device ?? throw new InvalidOperationException("機器を選択してください"), mapping, _settings);
    public Task SetModeAsync(AcquisitionSettings settings)
        => SelectAsync(_device ?? throw new InvalidOperationException("機器を選択してください"), CurrentMapping, settings);
    public Task ResetAsync()
        => SelectAsync(_device ?? throw new InvalidOperationException("機器を選択してください"), CurrentMapping, _settings);
    public void SetEstimator(string methodId)
    {
        Volatile.Write(ref _estimator, _registry.Create(methodId));
        Interlocked.Exchange(ref _forceAnalysis, 1);
        while (true)
        {
            var current = Current;
            var next = current with { Analysis = null, MethodId = methodId };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, current), current)) break;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));
        try
        {
            do { ProcessOnce(); }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Volatile.Write(ref _current, Current with { Status = new(_epoch, ConnectionState.Error, _device,
                $"解析処理エラー: {ex.Message}"), Analysis = null, History = null, StepHistogram = null, Latest = ImmutableDictionary<Axis, AxisSample>.Empty });
        }
    }

    private long AdvanceEpoch()
    {
        var epoch = Interlocked.Increment(ref _epoch);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        var next = new EpochWork(epoch, cancellation, cancellation.Token, _measurementWindowSeconds);
        var previous = Interlocked.Exchange(ref _epochWork, next);
        previous.Cancellation.Cancel();
        previous.Cancellation.Dispose();
        return epoch;
    }

    private void ProcessOnce()
    {
        var work = Volatile.Read(ref _epochWork);
        var epoch = work.Epoch;
        if (epoch != Interlocked.Read(ref _epoch)) return;
        if (_workerEpoch != epoch)
        {
            _workerEpoch = epoch; _latest.Clear(); _rates.Clear();
            if (_windows.Count == 0 || _workerWindowSeconds != work.WindowSeconds)
            {
                _windows.Clear();
                foreach (var axis in Enum.GetValues<Axis>())
                    _windows.Add(axis, new LiveAxisWindow(axis, _clock, epoch,
                        MeasurementWindow.SampleCapacity(work.WindowSeconds), work.WindowSeconds));
                _workerWindowSeconds = work.WindowSeconds;
            }
            else foreach (var window in _windows.Values) window.Reset(epoch);
            _analysis = null; _history = ImmutableDictionary<Axis, AxisWindowSnapshot>.Empty; _analysisAt = long.MinValue; _started = null;
            _stepHistogram = null;
            _publishedLatest = ImmutableDictionary<Axis, AxisSample>.Empty;
            _wasConnected = false;
            _analysisRevision = _historyRevision = _analysisCompleted = 0;
            _analysisDurationMs = 0; _analysisSampleCount = _historyPointCount = 0;
        }
        var source = Volatile.Read(ref _source);
        var current = Current;
        var status = source?.Status is { } st && st.Epoch == epoch ? st : current.Status;
        var diagnostics = source?.Diagnostics ?? new();
        var now = _clock.Timestamp;
        var drainStarted = Stopwatch.GetTimestamp();
        var latestChanged = false;
        if (source is not null && status.State == ConnectionState.Connected)
        {
            _wasConnected = true;
            var receiveStart = Stopwatch.GetTimestamp();
            for (var i = 0; i < 8192 && source.TryRead(out var frame); ++i)
            {
                if (frame is null || frame.Epoch != epoch) continue;
                foreach (var reading in frame.Readings)
                {
                    var sample = new AxisSample(epoch, frame.Sequence, frame.Timestamp,
                        reading.RawValue, reading.Value, reading.ContinuityId);
                    if (_windows[reading.Axis].Append(sample))
                    {
                        _latest[reading.Axis] = sample;
                        latestChanged = true;
                        if (_started is null) _started = sample.Timestamp;
                    }
                }
                if (Stopwatch.GetElapsedTime(receiveStart).TotalMilliseconds > 8) break;
            }
        }
        else if (status.State is ConnectionState.Disconnected or ConnectionState.Error or ConnectionState.Unsupported or ConnectionState.Stopped)
        {
            if (_wasConnected)
            {
                latestChanged = _latest.Count != 0;
                _latest.Clear(); _analysis = null; _started = null;
                _history = ImmutableDictionary<Axis, AxisWindowSnapshot>.Empty;
                _stepHistogram = null;
                foreach (var window in _windows.Values) window.Reset(epoch);
                _wasConnected = false;
            }
        }
        if (latestChanged) _publishedLatest = _latest.ToImmutableDictionary();
        var drainDurationMs = Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds;
        now = _clock.Timestamp;
        var estimator = Volatile.Read(ref _estimator);
        var forceAnalysis = Interlocked.Exchange(ref _forceAnalysis, 0) != 0;
        if (status.State == ConnectionState.Connected && (_analysisAt == long.MinValue || (now - _analysisAt) / (double)_clock.Frequency >= .1 || forceAnalysis))
        {
            var analysisStarted = Stopwatch.GetTimestamp();
            var analyses = ImmutableDictionary.CreateBuilder<Axis, AxisAnalysis>();
            var histories = ImmutableDictionary.CreateBuilder<Axis, AxisWindowSnapshot>();
            _combinedStepHistogram.Clear();
            long sampleCount = 0, historyCount = 0;
            foreach (var axis in Enum.GetValues<Axis>())
            {
                // A reconfiguration cancels publication and avoids doing the remaining axes.
                if (epoch != Interlocked.Read(ref _epoch)) return;
                _stopping.Token.ThrowIfCancellationRequested();
                LiveAxisResult result;
                try { result = _windows[axis].Analyze(now, estimator, work.Token); }
                catch (OperationCanceledException) when (work.Token.IsCancellationRequested && !_stopping.IsCancellationRequested) { return; }
                histories[axis] = result.History;
                analyses[axis] = result.Analysis;
                _combinedStepHistogram.AddBins(result.StepHistogram);
                sampleCount += result.Analysis.Count;
                historyCount += result.History.Samples.Length;
            }
            _history = histories.ToImmutable();
            var axes = analyses.ToImmutable();
            double? Representative(Axis x, Axis y) => StepPrecision.MeanBits(axes[x].Step, axes[y].Step);
            _analysis = new(epoch, now, axes, Representative(Axis.LX, Axis.LY), Representative(Axis.RX, Axis.RY), estimator.MethodId);
            _stepHistogram = new(epoch, now, work.WindowSeconds, "取得差分のbit数（非ゼロ・全4軸）",
                _combinedStepHistogram.Snapshot(), _combinedStepHistogram.TotalCount);
            _analysisAt = now;
            _analysisDurationMs = Stopwatch.GetElapsedTime(analysisStarted).TotalMilliseconds;
            _analysisCompleted = _clock.Timestamp;
            _analysisSampleCount = sampleCount; _historyPointCount = historyCount;
            _analysisRevision++; _historyRevision++;
        }
        _rates.Enqueue((now, diagnostics.Observations, diagnostics.ApiCalls));
        while (_rates.Count > 1 && (_rates.Count > 512 || _rates.Peek().Time < now - _clock.Frequency)) _rates.Dequeue();
        var baseline = _rates.Peek();
        var seconds = (now - baseline.Time) / (double)_clock.Frequency;
        var observationRate = seconds > 0 ? Math.Max(0, diagnostics.Observations - baseline.Observations) / seconds : 0;
        var apiRate = seconds > 0 ? Math.Max(0, diagnostics.ApiCalls - baseline.Calls) / seconds : 0;
        UpdateCpu(now);
        // Reconfiguration may happen while analysis is running: never publish its obsolete generation.
        if (epoch != Interlocked.Read(ref _epoch) || !ReferenceEquals(estimator, Volatile.Read(ref _estimator))) return;
        var publishedNow = _clock.Timestamp;
        var next = new ApplicationSnapshot(status, _settings, _backend, _analysis,
            _publishedLatest, diagnostics, publishedNow, _clock.Frequency,
            observationRate, apiRate, _cpuPercent, estimator.MethodId,
            _started is null ? 0 : Math.Max(0, (publishedNow - _started.Value) / (double)_clock.Frequency), _history,
            _analysisRevision, _historyRevision, CreatePerformance(source, publishedNow, drainDurationMs), _stepHistogram,
            work.WindowSeconds);
        Interlocked.CompareExchange(ref _current, next, current);
    }

    private PipelinePerformance CreatePerformance(IInputSource? source, long now, double drainDurationMs)
    {
        var queue = (source as IInputBufferDiagnostics)?.BufferDiagnostics;
        var age = queue?.OldestTimestamp is long timestamp ? Math.Max(0, (now - timestamp) * 1000d / _clock.Frequency) : 0;
        return new(drainDurationMs, _analysisDurationMs, queue?.Count ?? 0, queue?.Bytes ?? 0, age,
            _analysisRevision, _analysisCompleted, _analysisSampleCount, _historyPointCount);
    }

    private void UpdateCpu(long now)
    {
        if (_cpuTime != 0 && (now - _cpuTime) / (double)_clock.Frequency < 1) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            if (_cpuTime != 0)
                _cpuPercent = Math.Max(0, (cpu - _cpuAt).TotalSeconds / ((now - _cpuTime) / (double)_clock.Frequency) / Environment.ProcessorCount * 100);
            _cpuAt = cpu; _cpuTime = now;
        }
        catch (PlatformNotSupportedException) { _cpuPercent = 0; }
    }

    public async Task StopAsync()
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _stopping.CancelAsync().ConfigureAwait(false);
            if (_source is not null) await _source.DisposeAsync().ConfigureAwait(false);
            if (_worker is not null) await _worker.ConfigureAwait(false);
            Volatile.Read(ref _epochWork).Cancellation.Dispose();
            Volatile.Write(ref _current, Current with
            { Status = new(_epoch, ConnectionState.Stopped, _device, "停止しました"), Analysis = null, History = null, StepHistogram = null,
                Latest = ImmutableDictionary<Axis, AxisSample>.Empty });
        }
        finally { _changes.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
