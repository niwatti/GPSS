using System.Collections.Immutable;
using Gpss.Core;

namespace Gpss.Application;

public sealed record ApplicationSnapshot(
    InputStatus Status,
    AcquisitionSettings Settings,
    BackendKind Backend,
    AnalysisSnapshot? Analysis,
    ImmutableDictionary<Axis, AxisSample> Latest,
    InputDiagnostics Diagnostics,
    long Now,
    long Frequency,
    double ObservationRate,
    double ApiCallRate,
    double CpuPercent,
    string MethodId,
    double ElapsedSeconds,
    ImmutableDictionary<Axis, AxisWindowSnapshot>? History = null,
    long AnalysisRevision = 0,
    long HistoryRevision = 0,
    PipelinePerformance? Performance = null,
    StepHistogramSnapshot? StepHistogram = null,
    int MeasurementWindowSeconds = MeasurementWindow.DefaultSeconds);

/// <summary>Bounded pipeline diagnostics; durations include all four axes, not hardware latency.</summary>
public sealed record PipelinePerformance(
    double DrainDurationMs,
    double AnalysisDurationMs,
    int QueueCount,
    int QueueBytes,
    double QueueOldestAgeMs,
    long AnalysisRuns,
    long AnalysisCompletedTicks,
    long AnalysisSampleCount,
    long HistoryPointCount);
