using System.Collections.Immutable;
using Gpss.Core;

namespace Gpss.Input;

/// <summary>Owns session lifetime, bounded delivery and diagnostics independently of UI.</summary>
public abstract class InputSourceBase : IInputSource, ILatestPositionSource, IInputBufferDiagnostics
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly BoundedObservationBuffer _buffer = new();
    private readonly object _positionGate = new();
    private InputPositionSnapshot? _latestPosition;
    private long _positionRevision, _lastPositionSequence = -1, _lastPositionTimestamp = -1;
    private CancellationTokenSource? _stopping;
    private Task? _capture;
    private InputSession? _session;
    private InputStatus _status = new(0, ConnectionState.Searching);
    private long _apiCalls, _observations, _dropped, _parseErrors, _skips, _lastApi, _lastObservation, _lossGeneration;
    protected IMonotonicClock Clock { get; }
    public abstract BackendKind Kind { get; }
    public InputStatus Status => Volatile.Read(ref _status);
    public InputPositionSnapshot? LatestPosition
    {
        get
        {
            var snapshot = Volatile.Read(ref _latestPosition);
            if (snapshot is null) return null;
            var status = Volatile.Read(ref _status);
            var session = Volatile.Read(ref _session);
            return status.State == ConnectionState.Connected && status.Epoch == snapshot.Epoch && session?.Epoch == snapshot.Epoch
                ? snapshot : null;
        }
    }
    public BufferDiagnostics BufferDiagnostics => GetBufferDiagnostics();
    public InputDiagnostics Diagnostics => new(
        Interlocked.Read(ref _apiCalls), Interlocked.Read(ref _observations),
        Interlocked.Read(ref _dropped), Interlocked.Read(ref _parseErrors), Interlocked.Read(ref _skips),
        ReadTime(ref _lastApi), ReadTime(ref _lastObservation));
    private static long? ReadTime(ref long value) => Interlocked.Read(ref value) is var t && t != 0 ? t : null;
    protected InputSourceBase(IMonotonicClock? clock = null) => Clock = clock ?? StopwatchClock.Instance;
    public abstract Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default);
    protected abstract Task CaptureAsync(InputSession session, CancellationToken cancellationToken);

    public async Task StartAsync(DeviceInfo device, AxisMapping mapping, AcquisitionSettings settings,
        long epoch, CancellationToken cancellationToken = default)
    {
        if (device.Backend != Kind) throw new ArgumentException("入力方式が一致しません", nameof(device));
        if (!Enum.IsDefined(settings.Mode)) throw new ArgumentOutOfRangeException(nameof(settings));
        // Legacy callers/settings may still contain Normal. Every new session uses LowLatency.
        settings = settings with { Mode = AcquisitionMode.LowLatency };
        if (!double.IsFinite(settings.XInputIntervalMs) || settings.XInputIntervalMs <= 0 || settings.XInputIntervalMs > 1000 ||
            !double.IsFinite(settings.BusyWaitMicroseconds) || settings.BusyWaitMicroseconds < 0 || settings.BusyWaitMicroseconds > 2000 ||
            !double.IsFinite(settings.LowLatencyYieldMicroseconds) || settings.LowLatencyYieldMicroseconds < 0 || settings.LowLatencyYieldMicroseconds > 100000)
            throw new ArgumentOutOfRangeException(nameof(settings));
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Fence off old-session status/publication before awaiting its native stop.
            ClearLatestPosition(resetRevision: true);
            Volatile.Write(ref _session, new(device, mapping, settings, epoch));
            await StopCoreAsync().ConfigureAwait(false);
            ClearLatestPosition(resetRevision: true); // Remove any last publication completed during cancellation.
            _buffer.Clear();
            _apiCalls = _observations = _dropped = _parseErrors = _skips = _lastApi = _lastObservation = _lossGeneration = 0;
            _stopping = new();
            SetStatus(epoch, ConnectionState.Searching, device, "入力を開始しています");
            var session = _session;
            var token = _stopping.Token;
            _capture = Task.Run(async () =>
            {
                try { await CaptureAsync(session, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { SetStatus(epoch, ConnectionState.Error, device, ex.Message); }
            }, CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }

    public Task SelectDeviceAsync(DeviceInfo device, AxisMapping mapping, long epoch, CancellationToken cancellationToken = default)
        => StartAsync(device, mapping, _session?.Settings ?? new(), epoch, cancellationToken);
    public Task SetModeAsync(AcquisitionSettings settings, long epoch, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new InvalidOperationException("機器を選択してください");
        return StartAsync(session.Device, session.Mapping, settings, epoch, cancellationToken);
    }
    public Task ResetAsync(long epoch, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new InvalidOperationException("機器を選択してください");
        return StartAsync(session.Device, session.Mapping, session.Settings, epoch, cancellationToken);
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Completion (including native I/O cancellation) remains owned until it is drained.
            await StopCoreAsync().ConfigureAwait(false);
            _buffer.Clear();
            SetStatus(Status.Epoch, ConnectionState.Stopped, _session?.Device, "停止しました");
        }
        finally { _lifecycle.Release(); }
    }
    private async Task StopCoreAsync()
    {
        if (_stopping is null) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_capture is not null) await _capture.ConfigureAwait(false);
        _stopping.Dispose(); _stopping = null; _capture = null;
    }
    public bool TryRead(out InputFrame? frame) => _buffer.TryRead(out frame);
    public BufferDiagnostics GetBufferDiagnostics() => _buffer.GetBufferDiagnostics();
    protected void SetStatus(long epoch, ConnectionState state, DeviceInfo? device, string message = "")
    {
        var session = Volatile.Read(ref _session);
        if (session is not null && session.Epoch != epoch) return;
        Volatile.Write(ref _status, new(epoch, state, device, message, ReadTime(ref _lastApi)));
        if (state != ConnectionState.Connected)
        {
            lock (_positionGate)
                Volatile.Write(ref _latestPosition, null);
        }
    }
    protected void RecordApiCall() => Interlocked.Increment(ref _apiCalls);
    protected void RecordApiSuccess() => Interlocked.Exchange(ref _lastApi, Clock.Timestamp);
    protected void RecordParseError(bool breakAllAxes = true) { Interlocked.Increment(ref _parseErrors); if (breakAllAxes) Interlocked.Increment(ref _lossGeneration); }
    protected void RecordPotentialStateSkip() => Interlocked.Increment(ref _skips);
    protected void MarkDiscontinuity() => Interlocked.Increment(ref _lossGeneration);
    protected bool PublishFrame(InputFrame frame)
    {
        Interlocked.Increment(ref _observations);
        Interlocked.Exchange(ref _lastObservation, frame.Timestamp);
        var loss = Interlocked.Read(ref _lossGeneration);
        var adjusted = frame with
        {
            DroppedCount = Interlocked.Read(ref _dropped),
            Readings = frame.Readings.Select(r => r with { ContinuityId = r.ContinuityId + loss }).ToImmutableArray()
        };
        PublishLatestPosition(adjusted);
        if (_buffer.TryWrite(adjusted)) return true;
        Interlocked.Increment(ref _dropped);
        Interlocked.Increment(ref _lossGeneration);
        return false;
    }

    private void PublishLatestPosition(InputFrame frame)
    {
        if (frame.Readings.IsDefaultOrEmpty) return;
        lock (_positionGate)
        {
            var session = Volatile.Read(ref _session);
            var status = Volatile.Read(ref _status);
            if (session is null || session.Epoch != frame.Epoch || status.Epoch != frame.Epoch || status.State != ConnectionState.Connected)
                return;

            if (frame.Sequence < 0 || frame.Timestamp < 0 || frame.Sequence <= _lastPositionSequence || frame.Timestamp < _lastPositionTimestamp)
            {
                if (frame.Sequence > _lastPositionSequence) _lastPositionSequence = frame.Sequence;
                return;
            }
            _lastPositionSequence = frame.Sequence;
            _lastPositionTimestamp = frame.Timestamp;
            var previous = _latestPosition;
            if (previous?.Epoch != frame.Epoch) previous = null;
            var lx = previous?.LX; var ly = previous?.LY; var rx = previous?.RX; var ry = previous?.RY;
            var changed = false;
            foreach (var reading in frame.Readings)
            {
                if (!double.IsFinite(reading.Value) || reading.Value < -1 || reading.Value > 1) continue;
                var sample = new AxisSample(frame.Epoch, frame.Sequence, frame.Timestamp,
                    reading.RawValue, reading.Value, reading.ContinuityId);
                switch (reading.Axis)
                {
                    case Axis.LX: lx = sample; changed = true; break;
                    case Axis.LY: ly = sample; changed = true; break;
                    case Axis.RX: rx = sample; changed = true; break;
                    case Axis.RY: ry = sample; changed = true; break;
                }
            }
            if (!changed) return;
            var snapshot = new InputPositionSnapshot(frame.Epoch, ++_positionRevision, lx, ly, rx, ry);
            Volatile.Write(ref _latestPosition, snapshot);
        }
    }

    private void ClearLatestPosition(bool resetRevision)
    {
        lock (_positionGate)
        {
            Volatile.Write(ref _latestPosition, null);
            if (resetRevision) { _positionRevision = 0; _lastPositionSequence = _lastPositionTimestamp = -1; }
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

public sealed class BoundedObservationBuffer(int capacity = 8192, int maximumBytes = 16 * 1024 * 1024)
{
    private readonly Queue<InputFrame> _frames = new();
    private readonly object _gate = new();
    private int _bytes;
    public int Count { get { lock (_gate) return _frames.Count; } }
    public BufferDiagnostics GetBufferDiagnostics()
    {
        lock (_gate)
            return new(_frames.Count, _bytes, _frames.TryPeek(out var oldest) ? oldest.Timestamp : null);
    }
    public bool TryWrite(InputFrame frame)
    {
        var size = frame.ByteSize;
        lock (_gate)
        {
            if (_frames.Count >= capacity || size > maximumBytes - _bytes) return false;
            _frames.Enqueue(frame); _bytes += size; return true;
        }
    }
    public bool TryRead(out InputFrame? frame)
    {
        lock (_gate)
        {
            if (!_frames.TryDequeue(out frame)) return false;
            _bytes -= frame.ByteSize; return true;
        }
    }
    public void Clear() { lock (_gate) { _frames.Clear(); _bytes = 0; } }
}
