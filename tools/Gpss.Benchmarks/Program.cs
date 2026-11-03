using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Gpss.Application;
using Gpss.Core;
using Gpss.Input;
using Gpss.Input.XInput;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help) { PrintHelp(); return 0; }
            if (options.Pipeline) await RunPipelineAsync(options.Seconds, options.WindowSeconds);
            else await RunVirtualAsync(options);
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Benchmark failed: {ex.Message}");
            return 2;
        }
    }

    private static async Task RunVirtualAsync(Options options)
    {
        WarmUp(options.Reference);
        foreach (var hz in new[] { 1_000, 8_000, 100 })
        {
            await RunVirtualScenarioAsync(hz, options.Seconds, options.Pattern, useReference: false, options.WindowSeconds);
            if (options.Reference) await RunVirtualScenarioAsync(hz, options.Seconds, options.Pattern, useReference: true, options.WindowSeconds);
        }
    }

    private static void WarmUp(bool includeReference)
    {
        var clock = new BenchClock(1_000_000_000);
        var estimator = new MeanBitStepEstimator();
        var live = new LiveAxisWindow(Axis.LX, clock, epoch: 1, capacity: 4096);
        AxisWindow? reference = includeReference ? new(Axis.LX, clock, epoch: 1, capacity: 4096) : null;
        for (var i = 0; i < 512; i++)
        {
            clock.Now = i * 1_000;
            var sample = new AxisSample(1, i, clock.Now, i % 256, (i % 256) / 127.5 - 1, 0);
            live.Append(sample); reference?.Append(sample);
        }
        _ = live.Analyze(clock.Now, estimator);
        if (reference is not null)
        {
            var snapshot = reference.Snapshot(clock.Now);
            _ = JitterCalculator.Calculate(snapshot.Samples.Select(s => s.Value));
            _ = estimator.Estimate(snapshot.Differences.AsSpan());
            _ = HistoryProjection.Reduce(snapshot);
        }
    }

    private static async Task RunVirtualScenarioAsync(int hz, int seconds, string pattern, bool useReference, int windowSeconds)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        const long frequency = 1_000_000_000;
        var watch = Stopwatch.StartNew();
        var allocatedStart = GC.GetTotalAllocatedBytes(false);
        var gen0Start = GC.CollectionCount(0); var gen1Start = GC.CollectionCount(1); var gen2Start = GC.CollectionCount(2);
        var pauseStart = GC.GetTotalPauseDuration();
        var clock = new BenchClock(frequency);
        var estimator = new MeanBitStepEstimator();
        var liveWindows = useReference ? null : Enum.GetValues<Axis>().Select(a => new LiveAxisWindow(a, clock, epoch: 1, capacity: MeasurementWindow.SampleCapacity(windowSeconds), durationSeconds: windowSeconds)).ToArray();
        var referenceWindows = useReference ? Enum.GetValues<Axis>().Select(a => new AxisWindow(a, clock, epoch: 1, capacity: MeasurementWindow.SampleCapacity(windowSeconds), durationSeconds: windowSeconds)).ToArray() : null;
        var pointsPerTick = hz / 10;
        var shouldAppend = new bool[pointsPerTick];
        var sequences = new long[pointsPerTick];
        var timestamps = new long[pointsPerTick];
        var rawValues = new int[pointsPerTick * 4];
        var normalizedValues = new double[pointsPerTick * 4];
        var patternSeed = pattern switch { "quantized" => 0x5155414E, "random16" => 0x52414E44, "fragmented" => 0x46524147, _ => 0x4D495845 };
        var random = new Random(0x47505353 ^ hz ^ patternSeed);
        var appendedSamples = 0L;
        var candidateSamples = (long)seconds * hz;
        var eventSequence = 0L;
        var appendTimes = new BoundedSamples();
        var analyzeTimes = new BoundedSamples();
        long? heap40Seconds = null, allocated40Seconds = null;
        var maxCount = 0; var maxDrawPoints = 0; var acquisitionTruncated = false; var projectionTruncated = false;
        var maxHistoryCoverageLossSeconds = 0d;
        var maximumCountByAxis = new int[4]; var maximumDrawPointsByAxis = new int[4];
        var finalCountByAxis = new int[4]; var finalAcquisitionTruncatedByAxis = new bool[4];
        var finalProjectionTruncatedByAxis = new bool[4];
        LiveAxisResult[]? finalLive = useReference ? null : new LiveAxisResult[4];

        for (var tick = 0; tick < seconds * 10; tick++)
        {
            var firstPoll = (long)tick * pointsPerTick;
            for (var j = 0; j < pointsPerTick; j++)
            {
                var poll = firstPoll + j;
                var phase = (poll % hz) / (double)hz;
                var emit = pattern != "mixed" || phase < .75;
                shouldAppend[j] = emit;
                timestamps[j] = PollTimestamp(poll + 1, hz, frequency);
                if (emit) sequences[j] = eventSequence++;
                for (var axis = 0; axis < 4; axis++)
                {
                    var slot = j * 4 + axis;
                    int raw;
                    if (pattern == "random16" || (pattern == "mixed" && phase >= .5))
                        raw = random.Next(short.MinValue, short.MaxValue + 1);
                    else
                    {
                        var quantized = (poll + axis * 37) % 256;
                        raw = (int)quantized;
                    }
                    rawValues[slot] = raw;
                    normalizedValues[slot] = pattern == "random16" || (pattern == "mixed" && phase >= .5)
                        ? 2.0 * (raw - (double)short.MinValue) / ushort.MaxValue - 1.0
                        : 2.0 * raw / 255.0 - 1.0;
                }
            }

            var appendStart = Stopwatch.GetTimestamp();
            for (var j = 0; j < pointsPerTick; j++)
            {
                if (!shouldAppend[j]) continue;
                appendedSamples++;
                for (var axis = 0; axis < 4; axis++)
                {
                    var slot = j * 4 + axis;
                    var sample = new AxisSample(1, sequences[j], timestamps[j], rawValues[slot], normalizedValues[slot], pattern == "fragmented" ? sequences[j] : 0);
                    if (useReference) referenceWindows![axis].Append(sample);
                    else liveWindows![axis].Append(sample);
                }
            }
            appendTimes.Add(Stopwatch.GetElapsedTime(appendStart).TotalMilliseconds);
            clock.Now = ((long)tick + 1) * frequency / 10;

            var analyzeStart = Stopwatch.GetTimestamp();
            if (useReference)
            {
                for (var axis = 0; axis < 4; axis++)
                {
                    var snapshot = referenceWindows![axis].Snapshot(clock.Now);
                    _ = JitterCalculator.Calculate(snapshot.Samples.Select(s => s.Value));
                    _ = estimator.Estimate(snapshot.Differences.AsSpan());
                    var history = HistoryProjection.Reduce(snapshot);
                    maxHistoryCoverageLossSeconds = Math.Max(maxHistoryCoverageLossSeconds, snapshot.RetainedSeconds - history.RetainedSeconds);
                    maxCount = Math.Max(maxCount, snapshot.Samples.Length);
                    maxDrawPoints = Math.Max(maxDrawPoints, history.Samples.Length);
                    acquisitionTruncated |= snapshot.Truncated;
                    projectionTruncated |= history.ProjectionTruncated;
                    maximumCountByAxis[axis] = Math.Max(maximumCountByAxis[axis], snapshot.Samples.Length);
                    maximumDrawPointsByAxis[axis] = Math.Max(maximumDrawPointsByAxis[axis], history.Samples.Length);
                    finalCountByAxis[axis] = snapshot.Samples.Length;
                    finalAcquisitionTruncatedByAxis[axis] = snapshot.Truncated;
                    finalProjectionTruncatedByAxis[axis] = history.ProjectionTruncated;
                }
            }
            else
            {
                for (var axis = 0; axis < 4; axis++)
                {
                    var result = liveWindows![axis].Analyze(clock.Now, estimator);
                    var histogramCount = result.StepHistogram.Sum(b => b.Count);
                    if (histogramCount < 0 || histogramCount > Math.Max(0, result.Analysis.Count - 1))
                        throw new InvalidOperationException("Histogram exceeds the retained adjacent sample pairs.");
                    if (pattern == "fragmented" && histogramCount != 0)
                        throw new InvalidOperationException("Histogram includes an edge across a continuity boundary.");
                    if (pattern == "quantized" && histogramCount != Math.Max(0, result.Analysis.Count - 1))
                        throw new InvalidOperationException("Histogram lost a quantized nonzero step.");
                    maxHistoryCoverageLossSeconds = Math.Max(maxHistoryCoverageLossSeconds, result.Analysis.RetainedSeconds - result.History.RetainedSeconds);
                    finalLive![axis] = result;
                    maxCount = Math.Max(maxCount, result.Analysis.Count);
                    maxDrawPoints = Math.Max(maxDrawPoints, result.History.Samples.Length);
                    acquisitionTruncated |= result.Analysis.Truncated;
                    projectionTruncated |= result.History.ProjectionTruncated;
                    maximumCountByAxis[axis] = Math.Max(maximumCountByAxis[axis], result.Analysis.Count);
                    maximumDrawPointsByAxis[axis] = Math.Max(maximumDrawPointsByAxis[axis], result.History.Samples.Length);
                    finalCountByAxis[axis] = result.Analysis.Count;
                    finalAcquisitionTruncatedByAxis[axis] = result.Analysis.Truncated;
                    finalProjectionTruncatedByAxis[axis] = result.History.ProjectionTruncated;
                }
            }
            analyzeTimes.Add(Stopwatch.GetElapsedTime(analyzeStart).TotalMilliseconds);

            if (tick == 399)
            {
                allocated40Seconds = GC.GetTotalAllocatedBytes(false);
                heap40Seconds = GC.GetTotalMemory(true);
            }
            if ((tick + 1) % 300 == 0)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    kind = "virtual_progress", seconds = (tick + 1) / 10, hz, pattern, windowSeconds,
                    engine = useReference ? "reference" : "live",
                    allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocatedStart,
                    heapBytes = GC.GetTotalMemory(false), gen0 = GC.CollectionCount(0) - gen0Start,
                    gen1 = GC.CollectionCount(1) - gen1Start, gen2 = GC.CollectionCount(2) - gen2Start,
                    appendP95Ms = appendTimes.Percentile(.95), analyzeP95Ms = analyzeTimes.Percentile(.95)
                }, JsonOptions));
                await Task.Yield();
            }
        }

        var allocationEnd = GC.GetTotalAllocatedBytes(false);
        var heapEnd = GC.GetTotalMemory(true);
        var allocationAfter40PerSimSecond = allocated40Seconds.HasValue && seconds > 40
            ? (allocationEnd - allocated40Seconds.Value) / (double)(seconds - 40) : (double?)null;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "virtual_summary", simulatedSeconds = seconds, hz, pattern, windowSeconds,
            engine = useReference ? "reference" : "live",
            candidateSamplesPerAxis = candidateSamples,
            appendedSamplesPerAxis = appendedSamples,
            unchangedPollsPerAxis = candidateSamples - appendedSamples,
            appendP95Ms = appendTimes.Percentile(.95), appendP99Ms = appendTimes.Percentile(.99), appendMaxMs = appendTimes.Max,
            appendPercentileSamples = appendTimes.RetainedCount,
            analyzeP95Ms = analyzeTimes.Percentile(.95), analyzeP99Ms = analyzeTimes.Percentile(.99), analyzeMaxMs = analyzeTimes.Max,
            analyzePercentileSamples = analyzeTimes.RetainedCount,
            maxWindowCountPerAxis = maxCount, maxDrawPointsPerAxis = maxDrawPoints,
            axisOrder = Enum.GetNames<Axis>(),
            maximumCountByAxis, maximumDrawPointsByAxis, finalCountByAxis,
            finalAcquisitionTruncatedByAxis, finalProjectionTruncatedByAxis,
            acquisitionTruncated, projectionTruncated,
            maxHistoryCoverageLossSeconds,
            cumulativeAllocatedBytes = allocationEnd - allocatedStart,
            steadyAllocatedBytesPerSimSecond = allocationAfter40PerSimSecond,
            heapAt40SecondsBytes = heap40Seconds, heapAtEndBytes = heapEnd,
            gen0Collections = GC.CollectionCount(0) - gen0Start, gen1Collections = GC.CollectionCount(1) - gen1Start,
            gen2Collections = GC.CollectionCount(2) - gen2Start,
            gcPauseMs = (GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds,
            wallSeconds = watch.Elapsed.TotalSeconds,
            finalWindowCounts = finalLive?.Select(r => r.Analysis.Count).ToArray(),
            finalRetainedSeconds = finalLive?.Select(r => r.Analysis.RetainedSeconds).ToArray(),
            finalHistoryRetainedSeconds = finalLive?.Select(r => r.History.RetainedSeconds).ToArray(),
            allocatedRawSampleSlotsByAxis = liveWindows?.Select(w => w.AllocatedSampleCapacity).ToArray(),
            finalMeanBits = finalLive?.Select(r => r.Analysis.Step.Bits).ToArray(),
            finalHistogramCounts = finalLive?.Select(r => r.StepHistogram.Sum(b => b.Count)).ToArray(),
            finalHistogramBins = finalLive?.Select(r => r.StepHistogram).ToArray()
        }, JsonOptions));
        GC.KeepAlive(liveWindows); GC.KeepAlive(referenceWindows);
    }

    private static async Task RunPipelineAsync(int seconds, int windowSeconds)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        var api = new ChangingXInputApi();
        IInputSource Factory(BackendKind backend) => backend == BackendKind.XInput
            ? new XInputSource(api) : throw new NotSupportedException($"Pipeline mock only supports XInput; requested {backend}");
        await using var controller = new GpssController(Factory);
        var allocatedStart = GC.GetTotalAllocatedBytes(false);
        var pauseStart = GC.GetTotalPauseDuration();
        var gen0Start = GC.CollectionCount(0); var gen1Start = GC.CollectionCount(1); var gen2Start = GC.CollectionCount(2);
        var process = Process.GetCurrentProcess();
        var cpuAt = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var intervalAt = Stopwatch.GetTimestamp();
        var intervalAllocationAt = allocatedStart;
        var intervalPauseAt = pauseStart;
        var intervalGen0At = gen0Start; var intervalGen1At = gen1Start; var intervalGen2At = gen2Start;
        long intervalCallsAt = 0, intervalObservationsAt = 0;
        var overallAnalysis = new BoundedSamples(); var intervalAnalysis = new BoundedSamples();
        var overallDrain = new BoundedSamples(); var intervalDrain = new BoundedSamples();
        long lastAnalysisRevision = -1;
        PipelinePerformance? lastPerformance = null;
        var nextReportAt = 5.0;

        try
        {
            controller.Start();
            await controller.SetMeasurementWindowAsync(windowSeconds);
            var devices = await controller.GetDevicesAsync(BackendKind.XInput);
            if (devices.Count == 0) throw new InvalidOperationException("Mock XInput device enumeration failed.");
            var device = devices[0];
            api.Reset(); // Match the recorded baseline's Random(608) stream after catalog probes.
            await controller.SelectAsync(device, SuggestedMapping.Create(device), new AcquisitionSettings(AcquisitionMode.LowLatency));
            var initialDisplay = controller.GetDisplaySnapshot();
            intervalCallsAt = initialDisplay.Diagnostics.ApiCalls;
            intervalObservationsAt = initialDisplay.Diagnostics.Observations;
            process.Refresh(); cpuAt = process.TotalProcessorTime;
            intervalAt = Stopwatch.GetTimestamp();
            intervalAllocationAt = GC.GetTotalAllocatedBytes(false);
            intervalPauseAt = GC.GetTotalPauseDuration();
            intervalGen0At = GC.CollectionCount(0); intervalGen1At = GC.CollectionCount(1); intervalGen2At = GC.CollectionCount(2);
            wall.Restart();

            while (wall.Elapsed.TotalSeconds < seconds)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var display = controller.GetDisplaySnapshot();
                var performance = display.Performance;
                if (performance is not null)
                {
                    if (display.AnalysisRevision != 0 && display.AnalysisRevision != lastAnalysisRevision)
                    {
                        overallAnalysis.Add(performance.AnalysisDurationMs);
                        intervalAnalysis.Add(performance.AnalysisDurationMs);
                        lastAnalysisRevision = display.AnalysisRevision;
                    }
                    if (lastPerformance is null || lastPerformance != performance)
                    {
                        overallDrain.Add(performance.DrainDurationMs);
                        intervalDrain.Add(performance.DrainDurationMs);
                        lastPerformance = performance;
                    }
                }

                if (wall.Elapsed.TotalSeconds >= nextReportAt)
                {
                    var nowTicks = Stopwatch.GetTimestamp();
                    var elapsedSeconds = Stopwatch.GetElapsedTime(intervalAt, nowTicks).TotalSeconds;
                    var allocatedNow = GC.GetTotalAllocatedBytes(false);
                    var calls = display.Diagnostics.ApiCalls;
                    var observations = display.Diagnostics.Observations;
                    var callsHz = elapsedSeconds > 0 ? (calls - intervalCallsAt) / elapsedSeconds : 0;
                    var updatesHz = elapsedSeconds > 0 ? (observations - intervalObservationsAt) / elapsedSeconds : 0;
                    process.Refresh();
                    var cpuNow = process.TotalProcessorTime;
                    var cpuPercent = elapsedSeconds > 0
                        ? Math.Max(0, (cpuNow - cpuAt).TotalSeconds / elapsedSeconds / Environment.ProcessorCount * 100)
                        : 0;
                    var ageMs = performance is { AnalysisCompletedTicks: > 0 }
                        ? Math.Max(0, (Stopwatch.GetTimestamp() - performance.AnalysisCompletedTicks) * 1000d / Stopwatch.Frequency)
                        : (double?)null;
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        kind = "pipeline_interval", elapsedSeconds = wall.Elapsed.TotalSeconds,
                        source = "mock_xinput", acquisitionMode = "LowLatency", windowSeconds, busyWaitMicroseconds = 2000, lowLatencyYieldMicroseconds = 100,
                        apiCallsPerSecond = callsHz, stateUpdatesPerSecond = updatesHz,
                        processCpuPercent = cpuPercent,
                        allocatedBytesPerSecond = elapsedSeconds > 0 ? (allocatedNow - intervalAllocationAt) / elapsedSeconds : 0,
                        cumulativeAllocatedBytes = allocatedNow - allocatedStart,
                        gcPauseMs = (GC.GetTotalPauseDuration() - intervalPauseAt).TotalMilliseconds,
                        gen0Collections = GC.CollectionCount(0) - intervalGen0At,
                        gen1Collections = GC.CollectionCount(1) - intervalGen1At,
                        gen2Collections = GC.CollectionCount(2) - intervalGen2At,
                        queueCount = performance?.QueueCount ?? 0, queueBytes = performance?.QueueBytes ?? 0,
                        queueOldestAgeMs = performance?.QueueOldestAgeMs ?? 0,
                        analysisAgeMs = ageMs, analysisRuns = performance?.AnalysisRuns ?? 0,
                        drainDurationMs = performance?.DrainDurationMs ?? 0,
                        analysisDurationMs = performance?.AnalysisDurationMs ?? 0,
                        analysisCompletedTicks = performance?.AnalysisCompletedTicks ?? 0,
                        analysisSamples = performance?.AnalysisSampleCount ?? 0,
                        historyPoints = performance?.HistoryPointCount ?? 0,
                        analysisP95Ms = intervalAnalysis.Percentile(.95), analysisP99Ms = intervalAnalysis.Percentile(.99),
                        analysisPercentileSamples = intervalAnalysis.RetainedCount,
                        analysisMaxMs = intervalAnalysis.Max, drainP95Ms = intervalDrain.Percentile(.95),
                        drainP99Ms = intervalDrain.Percentile(.99), drainMaxMs = intervalDrain.Max,
                        drainPercentileSamples = intervalDrain.RetainedCount,
                        analysisRevision = display.AnalysisRevision,
                        histogramCount = display.StepHistogram?.TotalCount ?? 0,
                        histogramBinCount = display.StepHistogram?.Bins.Length ?? 0
                    }, JsonOptions));

                    intervalAnalysis.Clear(); intervalDrain.Clear();
                    intervalAt = nowTicks; intervalAllocationAt = allocatedNow; intervalPauseAt = GC.GetTotalPauseDuration();
                    intervalGen0At = GC.CollectionCount(0); intervalGen1At = GC.CollectionCount(1); intervalGen2At = GC.CollectionCount(2);
                    intervalCallsAt = calls; intervalObservationsAt = observations; cpuAt = cpuNow; nextReportAt += 5;
                }
                await Task.Delay(17, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            Console.CancelKeyPress -= handler;
            await controller.StopAsync();
            var final = controller.GetDisplaySnapshot();
            var finalPerformance = final.Performance;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "pipeline_summary", source = "mock_xinput", acquisitionMode = "LowLatency", windowSeconds, busyWaitMicroseconds = 2000, lowLatencyYieldMicroseconds = 100,
                requestedSeconds = seconds, wallSeconds = wall.Elapsed.TotalSeconds,
                apiCalls = final.Diagnostics.ApiCalls, stateUpdates = final.Diagnostics.Observations,
                apiCallsPerSecond = wall.Elapsed.TotalSeconds > 0 ? final.Diagnostics.ApiCalls / wall.Elapsed.TotalSeconds : 0,
                stateUpdatesPerSecond = wall.Elapsed.TotalSeconds > 0 ? final.Diagnostics.Observations / wall.Elapsed.TotalSeconds : 0,
                analysisP95Ms = overallAnalysis.Percentile(.95), analysisP99Ms = overallAnalysis.Percentile(.99), analysisMaxMs = overallAnalysis.Max,
                drainP95Ms = overallDrain.Percentile(.95), drainP99Ms = overallDrain.Percentile(.99), drainMaxMs = overallDrain.Max,
                uniqueAnalysisRevisions = overallAnalysis.TotalCount, analysisPercentileSamples = overallAnalysis.RetainedCount,
                drainPercentileSamples = overallDrain.RetainedCount,
                cumulativeAllocatedBytes = GC.GetTotalAllocatedBytes(false) - allocatedStart,
                heapAtEndBytes = GC.GetTotalMemory(false), gcPauseMs = (GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds,
                gen0Collections = GC.CollectionCount(0) - gen0Start,
                gen1Collections = GC.CollectionCount(1) - gen1Start,
                gen2Collections = GC.CollectionCount(2) - gen2Start,
                finalDrainDurationMs = finalPerformance?.DrainDurationMs ?? 0,
                finalAnalysisDurationMs = finalPerformance?.AnalysisDurationMs ?? 0,
                finalAnalysisCompletedTicks = finalPerformance?.AnalysisCompletedTicks ?? 0,
                finalQueueCount = finalPerformance?.QueueCount ?? 0,
                finalQueueBytes = finalPerformance?.QueueBytes ?? 0,
                finalQueueOldestAgeMs = finalPerformance?.QueueOldestAgeMs ?? 0,
                finalAnalysisSampleCount = finalPerformance?.AnalysisSampleCount ?? 0,
                finalHistoryPointCount = finalPerformance?.HistoryPointCount ?? 0
            }, JsonOptions));
            process.Dispose();
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
GPSS performance benchmark
Usage: Gpss.Benchmarks [seconds] [--pattern quantized|random16|mixed|fragmented] [--reference] [--window-seconds 10|30|60|300]
       Gpss.Benchmarks --pipeline --seconds N

Default virtual mode runs 1kHz, 8kHz and 100Hz scenarios for 300 simulated seconds.
`mixed` cycles through quantized updates, random16 updates and unchanged polls.
`fragmented` starts a new continuity run at every observation to stress history coverage.
`--reference` also measures the legacy AxisWindow/Snapshot path.
Pipeline mode runs a changing mock XInput source with the production controller at
LowLatency/2000µs burst/100µs yield, polls its display snapshot every 17ms, and reports measured API calls and
state updates separately. It does not claim 1kHz hardware operation.
Output is JSON Lines on stdout.
""");
    }

    private static long PollTimestamp(long ordinal, int hz, long frequency)
    {
        var seconds = ordinal / hz;
        var remainder = ordinal % hz;
        return checked(seconds * frequency + remainder * frequency / hz);
    }

    private sealed class BenchClock(long frequency) : IMonotonicClock
    {
        public long Now;
        public long Timestamp => Now;
        public long Frequency => frequency;
    }

    private sealed class BoundedSamples(int capacity = 10_000)
    {
        private readonly double[] _values = new double[capacity];
        private int _count, _next;
        public long TotalCount { get; private set; }
        public int RetainedCount => _count;
        public double Max { get; private set; }
        public void Add(double value)
        {
            if (!double.IsFinite(value)) return;
            if (TotalCount == 0 || value > Max) Max = value;
            _values[_next] = value; _next = (_next + 1) % _values.Length;
            _count = Math.Min(_count + 1, _values.Length); TotalCount++;
        }
        public double Percentile(double percentile)
        {
            if (_count == 0) return 0;
            var copy = new double[_count];
            Array.Copy(_values, copy, _count); Array.Sort(copy);
            var index = (int)Math.Floor((_count - 1) * percentile);
            return copy[index];
        }
        public void Clear() { _count = 0; _next = 0; Max = 0; TotalCount = 0; }
    }

    private sealed class ChangingXInputApi : IXInputApi
    {
        private int _packet;
        private Random _random = new(608);
        public void Reset() { _packet = 0; _random = new Random(608); }
        public uint GetState(uint slot, out XInputState state)
        {
            if (slot != 0) { state = default; return XInputSource.DeviceNotConnected; }
            var packet = Interlocked.Increment(ref _packet);
            state = new XInputState
            {
                PacketNumber = unchecked((uint)packet),
                Gamepad = new XInputGamepad
                {
                    LX = (short)_random.Next(short.MinValue, short.MaxValue + 1),
                    LY = (short)_random.Next(short.MinValue, short.MaxValue + 1),
                    RX = (short)_random.Next(short.MinValue, short.MaxValue + 1),
                    RY = (short)_random.Next(short.MinValue, short.MaxValue + 1),
                    LeftTrigger = 0, RightTrigger = 0
                }
            };
            return 0;
        }
    }

    private sealed record Options(int Seconds, string Pattern, bool Reference, bool Pipeline, bool Help, int WindowSeconds)
    {
        public static Options Parse(string[] args)
        {
            var seconds = 300; var pattern = "quantized"; var reference = false; var pipeline = false; var help = false;
            var positionalSeen = false; var windowSeconds = MeasurementWindow.DefaultSeconds;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--help": case "-h": help = true; break;
                    case "--reference": reference = true; break;
                    case "--pipeline": pipeline = true; break;
                    case "--seconds":
                        if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                            throw new ArgumentException("--seconds requires an integer");
                        break;
                    case "--window-seconds":
                        if (++i >= args.Length || !int.TryParse(args[i], out windowSeconds))
                            throw new ArgumentException("--window-seconds requires 10, 30, 60 or 300");
                        MeasurementWindow.Validate(windowSeconds);
                        break;
                    case "--pattern":
                        if (++i >= args.Length) throw new ArgumentException("--pattern requires quantized, random16, mixed or fragmented");
                        pattern = args[i].ToLowerInvariant();
                        break;
                    default:
                        if (!arg.StartsWith('-') && !positionalSeen && int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacySeconds))
                        { seconds = legacySeconds; positionalSeen = true; break; }
                        throw new ArgumentException($"Unknown argument: {arg}");
                }
            }
            if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds), "seconds must be positive");
            if (pattern is not ("quantized" or "random16" or "mixed" or "fragmented")) throw new ArgumentException("--pattern must be quantized, random16, mixed or fragmented");
            if (pipeline && reference) throw new ArgumentException("--reference applies only to virtual mode");
            if ((long)seconds * 10 > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(seconds));
            return new(seconds, pattern, reference, pipeline, help, windowSeconds);
        }
    }
}
