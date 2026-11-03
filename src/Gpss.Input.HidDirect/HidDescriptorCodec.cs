using System.Collections.Immutable;
using Gpss.Core;

namespace Gpss.Input.HidDirect;

public static class HidDescriptorCodec
{
    public static ImmutableArray<AxisDescriptor> Expand(HidValueCapability caps)
    {
        var first = caps.UsageMin;
        var last = caps.IsRange != 0 ? caps.UsageMax : first;
        if (last < first) return [];
        var minimum = (long)caps.LogicalMin;
        var maximum = minimum < 0 ? caps.LogicalMax : (long)unchecked((uint)caps.LogicalMax);
        var reason = caps.IsAbsolute == 0 ? "相対軸は未対応" :
            caps.BitSize is < 1 or > 32 ? "1〜32bit以外の軸は未対応" :
            caps.ReportCount > 1 && caps.IsRange == 0 ? "value arrayは未対応" :
            maximum <= minimum ? "論理範囲が無効" : null;
        // Value arrays have repeated instances of the same usage; a Range can instead describe scalar usages.
        var builder = ImmutableArray.CreateBuilder<AxisDescriptor>();
        for (int usage = first; usage <= last; usage++)
        {
            var key = $"{caps.UsagePage:X4}:{usage:X4}:{caps.LinkCollection}:{caps.ReportId}";
            var name = caps.UsagePage == 1 ? usage switch
            {
                0x30 => "X", 0x31 => "Y", 0x32 => "Z", 0x33 => "Rx", 0x34 => "Ry", 0x35 => "Rz",
                0x36 => "Slider", _ => $"Usage {usage:X4}"
            } : $"Usage {caps.UsagePage:X4}/{usage:X4}";
            builder.Add(new(key, $"{name} / Report {caps.ReportId} / Link {caps.LinkCollection}",
                minimum, maximum, caps.BitSize, caps.UsagePage, (ushort)usage, caps.LinkCollection,
                caps.ReportId, caps.IsAbsolute != 0, caps.HasNull != 0, reason is null, reason));
        }
        return builder.ToImmutable();
    }

    public static long Interpret(uint value, AxisDescriptor descriptor)
    {
        if (descriptor.BitSize is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(descriptor));
        var mask = (1UL << descriptor.BitSize) - 1;
        var raw = (ulong)value & mask;
        if (descriptor.LogicalMin < 0 && (raw & (1UL << (descriptor.BitSize - 1))) != 0)
            return (long)raw - (1L << descriptor.BitSize);
        return (long)raw;
    }

    public static bool IsValid(long value, AxisDescriptor descriptor)
        => descriptor.Supported && value >= descriptor.LogicalMin && value <= descriptor.LogicalMax;
}
