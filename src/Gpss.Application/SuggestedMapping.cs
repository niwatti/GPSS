using System.Collections.Immutable;
using Gpss.Core;

namespace Gpss.Application;

public static class SuggestedMapping
{
    public static AxisMapping Create(DeviceInfo device)
    {
        // Some HID pads expose right stick as Z/Rz and triggers as Rx/Ry.
        // When both pairs exist, usage names alone cannot identify the right stick.
        if (device.Backend == BackendKind.HidDirect)
        {
            bool Has(ushort usage) => device.Axes.Any(d => d.Supported && d.IsAbsolute && d.UsagePage == 1 && d.Usage == usage);
            if (Has(0x32) && Has(0x35) && Has(0x33) && Has(0x34)) return AxisMapping.Empty;
        }
        var bindings = ImmutableArray.CreateBuilder<AxisBinding>();
        foreach (var axis in Enum.GetValues<Axis>())
        {
            var usage = axis switch { Axis.LX => 0x30, Axis.LY => 0x31, Axis.RX => 0x33, _ => 0x34 };
            var matches = device.Axes.Where(d => d.Supported && d.IsAbsolute &&
                (device.Backend != BackendKind.HidDirect
                    ? d.Key.Equals(axis.ToString(), StringComparison.OrdinalIgnoreCase)
                    : d.UsagePage == 1 && d.Usage == usage)).ToArray();
            if (matches.Length != 1) return AxisMapping.Empty;
            bindings.Add(new(axis, matches[0].Key, device.Backend == BackendKind.XInput && axis is Axis.LY or Axis.RY));
        }
        return new(bindings.ToImmutable());
    }

    public static void Validate(DeviceInfo device, AxisMapping mapping)
    {
        if (mapping.Bindings.Length != 4 || mapping.Bindings.Select(b => b.Axis).Distinct().Count() != 4 ||
            mapping.Bindings.Select(b => b.DescriptorKey).Distinct().Count() != 4)
            throw new ArgumentException("左右のX/Yに異なる4軸を割り当ててください");
        if (!mapping.AuxiliaryBindings.IsDefaultOrEmpty)
        {
            var auxiliary = mapping.AuxiliaryBindings;
            if (auxiliary.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != auxiliary.Length ||
                auxiliary.Select(b => b.DescriptorKey).Distinct(StringComparer.Ordinal).Count() != auxiliary.Length ||
                auxiliary.Any(b => b.Name is not ("lt" or "rt") || mapping.Bindings.Any(a => a.DescriptorKey == b.DescriptorKey) ||
                    !device.Axes.Any(d => d.Key == b.DescriptorKey && d.Supported && d.IsAbsolute && d.LogicalMax > d.LogicalMin)))
                throw new ArgumentException("LT/RTの軸割当が不正です");
        }
        foreach (var binding in mapping.Bindings)
            if (!device.Axes.Any(d => d.Key == binding.DescriptorKey && d.Supported && d.IsAbsolute && d.LogicalMax > d.LogicalMin))
                throw new ArgumentException($"未対応の軸です: {binding.DescriptorKey}");
    }
}
