using System.Collections.Immutable;
using Gpss.Core;

namespace Gpss.Input.HidDirect;

public sealed record HidReadResult(long Timestamp, ImmutableArray<byte> Report);
public sealed record HidDecodeResult(ImmutableArray<AxisReading> Readings, bool HadInvalidValues = false,
    ImmutableArray<Axis> InvalidAxes = default, ImmutableArray<AuxiliaryReading> Auxiliary = default);

public interface IHidConnection : IAsyncDisposable
{
    DeviceInfo Device { get; }
    string DecoderId { get; }
    Task<HidReadResult?> ReadAsync(AcquisitionSettings settings, CancellationToken cancellationToken);
    HidDecodeResult Decode(ReadOnlySpan<byte> report, AxisMapping mapping, long continuityId);
}

public interface IHidPlatform
{
    IReadOnlyList<DeviceInfo> Enumerate();
    IHidConnection Open(DeviceInfo device);
}

/// <summary>Allows verified device-specific decoders without byte-offset guessing.</summary>
public interface IHidReportDecoder
{
    string Id { get; }
    bool Supports(DeviceInfo device);
    HidDecodeResult Decode(DeviceInfo device, ReadOnlySpan<byte> report, AxisMapping mapping, long continuityId);
}

public sealed class HidDecoderRegistry
{
    private readonly List<IHidReportDecoder> _decoders = [];
    public void Register(IHidReportDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (_decoders.Any(d => d.Id == decoder.Id)) throw new ArgumentException("デコーダーIDが重複しています");
        _decoders.Add(decoder);
    }
    public IHidReportDecoder? Find(DeviceInfo device) => _decoders.FirstOrDefault(d => d.Supports(device));
}

public sealed class HidDeviceDisconnectedException(string message) : IOException(message);
