using System.Runtime.InteropServices;
using Gpss.Core;
using Gpss.Input.HidDirect;

namespace Gpss.HidDirect.Tests;

public sealed class HidDescriptorTests
{
    [Fact]
    public void WindowsSdkLayoutsAreExplicit()
    {
        Assert.Equal(64, Marshal.SizeOf<HidCapabilities>());
        Assert.Equal(72, Marshal.SizeOf<HidValueCapability>());
        Assert.Equal(12, Marshal.SizeOf<HidAttributes>());
        Assert.Equal(40, Marshal.OffsetOf<HidValueCapability>(nameof(HidValueCapability.LogicalMin)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<HidValueCapability>(nameof(HidValueCapability.UsageMin)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 28, Marshal.SizeOf<HidDeviceInterfaceData>());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 20, Marshal.SizeOf<HidOverlapped>());
    }
    [Fact]
    public void RangeAndNotRangeUseDistinctUsageKeys()
    {
        var cap = Caps(); cap.IsRange = 1; cap.UsageMin = 0x30; cap.UsageMax = 0x33; cap.ReportCount = 4;
        var axes = HidDescriptorCodec.Expand(cap);
        Assert.Equal(4, axes.Length);
        Assert.Equal(4, axes.Select(a => a.Key).Distinct().Count());
        Assert.All(axes, a => Assert.True(a.Supported));
        cap.IsRange = 0; cap.UsageMax = 0; cap.ReportCount = 1;
        Assert.Single(HidDescriptorCodec.Expand(cap));
    }
    [Fact]
    public void Unsigned32AndSigned32BoundariesAreInterpretedWithoutIntShiftOverflow()
    {
        var cap = Caps(); cap.BitSize = 32; cap.LogicalMax = -1;
        var unsigned = Assert.Single(HidDescriptorCodec.Expand(cap));
        Assert.Equal(uint.MaxValue, unsigned.LogicalMax);
        Assert.Equal(uint.MaxValue, HidDescriptorCodec.Interpret(uint.MaxValue, unsigned));
        cap.LogicalMin = int.MinValue; cap.LogicalMax = int.MaxValue;
        var signed = Assert.Single(HidDescriptorCodec.Expand(cap));
        Assert.Equal(int.MinValue, HidDescriptorCodec.Interpret(0x80000000, signed));
        Assert.Equal(-1, HidDescriptorCodec.Interpret(uint.MaxValue, signed));
        Assert.Equal(int.MaxValue, HidDescriptorCodec.Interpret(0x7fffffff, signed));
    }
    [Theory]
    [InlineData(8, 0xFF, -1)]
    [InlineData(8, 0x80, -128)]
    [InlineData(12, 0xFFF, -1)]
    [InlineData(16, 0xFFFF, -1)]
    public void SignedValuesSignExtendAtTheirOwnBitWidth(int bits, uint value, long expected)
    {
        var descriptor = new AxisDescriptor("key", "axis", -(1L << (bits - 1)), (1L << (bits - 1)) - 1, bits);
        Assert.Equal(expected, HidDescriptorCodec.Interpret(value, descriptor));
    }
    [Fact]
    public void NullAndOutOfRangeValuesAreRejectedRatherThanClamped()
    {
        var descriptor = new AxisDescriptor("key", "axis", 0, 255, 16, HasNull: true);
        Assert.False(HidDescriptorCodec.IsValid(256, descriptor));
        Assert.True(HidDescriptorCodec.IsValid(255, descriptor));
    }
    [Fact]
    public void UnsupportedArraysRelativeValuesAndInvalidRangesAreExplained()
    {
        var cap = Caps(); cap.ReportCount = 2;
        Assert.Contains("array", Assert.Single(HidDescriptorCodec.Expand(cap)).UnsupportedReason);
        cap.ReportCount = 1; cap.IsAbsolute = 0;
        Assert.Contains("相対", Assert.Single(HidDescriptorCodec.Expand(cap)).UnsupportedReason);
        cap.IsAbsolute = 1; cap.LogicalMax = 0;
        Assert.False(Assert.Single(HidDescriptorCodec.Expand(cap)).Supported);
    }
    [Fact]
    public void DecoderRegistryDoesNotGuessUnrecognizedDevices()
    {
        var registry = new HidDecoderRegistry();
        var device = new DeviceInfo("test", "unknown", BackendKind.HidDirect, []);
        Assert.Null(registry.Find(device));
    }
    [Fact]
    public void OffWindowsNativeCatalogFailsClearly()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => new WindowsHidPlatform().Enumerate());
    }
    private static HidValueCapability Caps() => new()
    { UsagePage = 1, UsageMin = 0x30, ReportCount = 1, BitSize = 8, LogicalMin = 0, LogicalMax = 255, IsAbsolute = 1 };
}
