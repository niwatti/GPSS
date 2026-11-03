using Gpss.Core;

namespace Gpss.Input;

public interface IInputSource : IAsyncDisposable
{
    BackendKind Kind { get; }
    InputStatus Status { get; }
    InputDiagnostics Diagnostics { get; }
    Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default);
    Task StartAsync(DeviceInfo device, AxisMapping mapping, AcquisitionSettings settings, long epoch,
        CancellationToken cancellationToken = default);
    Task SelectDeviceAsync(DeviceInfo device, AxisMapping mapping, long epoch,
        CancellationToken cancellationToken = default);
    Task SetModeAsync(AcquisitionSettings settings, long epoch, CancellationToken cancellationToken = default);
    Task ResetAsync(long epoch, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    bool TryRead(out InputFrame? frame);
}

/// <summary>Optional low-cost latest-position mailbox, independent of observation consumption.</summary>
public interface ILatestPositionSource
{
    InputPositionSnapshot? LatestPosition { get; }
}

/// <summary>Optional snapshot of the bounded observation queue.</summary>
public interface IInputBufferDiagnostics
{
    BufferDiagnostics BufferDiagnostics { get; }
}

public sealed record InputPositionSnapshot(
    long Epoch, long Revision,
    AxisSample? LX, AxisSample? LY, AxisSample? RX, AxisSample? RY);

public sealed record BufferDiagnostics(int Count, int Bytes, long? OldestTimestamp);

public sealed record InputSession(DeviceInfo Device, AxisMapping Mapping,
    AcquisitionSettings Settings, long Epoch);

public interface IInputDiscoveryDiagnostics
{
    IReadOnlyList<string> DiscoveryErrors { get; }
}
