using System.Collections.Immutable;
using System.Diagnostics;

namespace Gpss.Core;

public enum Axis { LX, LY, RX, RY }
public enum BackendKind { HidDirect, XInput, Simulation }
public enum AcquisitionMode { Normal, LowLatency }
public enum ConnectionState { Searching, Connected, Disconnected, Unsupported, Error, Stopped }
public enum EstimateStatus { NoData, Reference, Valid, Invalid }

public interface IMonotonicClock
{
    long Timestamp { get; }
    long Frequency { get; }
}

public sealed class StopwatchClock : IMonotonicClock
{
    public static StopwatchClock Instance { get; } = new();
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
}

public sealed record AxisDescriptor(
    string Key, string Name, long LogicalMin, long LogicalMax,
    int BitSize = 16, ushort? UsagePage = null, ushort? Usage = null,
    ushort? LinkCollection = null, byte? ReportId = null,
    bool IsAbsolute = true, bool HasNull = false, bool Supported = true,
    string? UnsupportedReason = null);

public sealed record DeviceInfo(
    string Id, string Name, BackendKind Backend, ImmutableArray<AxisDescriptor> Axes,
    string? Path = null, uint? XInputSlot = null, ushort? VendorId = null,
    ushort? ProductId = null, string? Detail = null)
{
    public override string ToString() => Name;
}

public sealed record AxisBinding(Axis Axis, string DescriptorKey, bool Inverted = false);
public sealed record AuxiliaryBinding(string Name, string DescriptorKey, bool Inverted = false);
public sealed record AxisMapping(ImmutableArray<AxisBinding> Bindings, ImmutableArray<AuxiliaryBinding> AuxiliaryBindings = default)
{
    public static AxisMapping Empty { get; } = new([]);
}

public sealed record AcquisitionSettings(
    AcquisitionMode Mode = AcquisitionMode.LowLatency,
    double XInputIntervalMs = 1.0,
    double BusyWaitMicroseconds = 2000,
    double LowLatencyYieldMicroseconds = 100);

public readonly record struct AuxiliaryReading(string Name, long RawValue, double Value, long LogicalMin, long LogicalMax);

public readonly record struct AxisReading(Axis Axis, long RawValue, double Value, long ContinuityId);

public sealed record InputFrame(
    BackendKind Backend, long Epoch, long Sequence, long Timestamp,
    ImmutableArray<AxisReading> Readings, string DecoderId,
    AcquisitionMode Mode = AcquisitionMode.LowLatency, long DroppedCount = 0,
    uint? NativePacketNumber = null, ImmutableArray<byte> RawReport = default,
    ImmutableArray<AuxiliaryReading> Auxiliary = default)
{
    public int ByteSize => checked((RawReport.IsDefault ? 0 : RawReport.Length) + Readings.Length * 64 + (Auxiliary.IsDefault ? 0 : Auxiliary.Length * 64) + 128);
}

public sealed record InputStatus(
    long Epoch, ConnectionState State, DeviceInfo? Device = null, string Message = "",
    long? LastApiSuccess = null);

public sealed record InputDiagnostics(
    long ApiCalls = 0, long Observations = 0, long Dropped = 0,
    long ParseErrors = 0, long PotentialStateSkips = 0,
    long? LastApiSuccess = null, long? LastObservation = null);

public readonly record struct AxisSample(
    long Epoch, long Sequence, long Timestamp, long RawValue, double Value, long ContinuityId);

public sealed record AxisWindowSnapshot(
    long Epoch, Axis Axis, long Now, long Frequency,
    ImmutableArray<AxisSample> Samples, ImmutableArray<double> Differences,
    double RetainedSeconds, long DroppedCount, bool Truncated, bool ProjectionTruncated = false,
    double WindowSeconds = MeasurementWindow.DefaultSeconds);

public sealed record AxisJitter(int Count, double? Mean, double? PopulationStdDev, double? MaxDeviation);

public sealed record StepEstimate(
    string MethodId, EstimateStatus Status = EstimateStatus.NoData,
    double? Step = null, double? Levels = null, double? Bits = null,
    int NonzeroCount = 0, int ModalCount = 0, double ModalRatio = 0,
    bool Tie = false, int ExcludedCount = 0, string Message = "");

public sealed record AxisAnalysis(
    Axis Axis, AxisSample? Latest, AxisJitter Jitter, StepEstimate Step,
    int Count, double RetainedSeconds, bool Truncated, long DroppedCount,
    double SampleRate);

public sealed record AnalysisSnapshot(
    long Epoch, long Timestamp, ImmutableDictionary<Axis, AxisAnalysis> Axes,
    double? LeftBits, double? RightBits, string MethodId);
