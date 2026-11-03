using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Gpss.Core;
using Microsoft.Win32.SafeHandles;

namespace Gpss.Input.HidDirect;

public sealed class WindowsHidPlatform : IHidPlatform
{
    private readonly IMonotonicClock _clock;
    private readonly HidDecoderRegistry _decoders;
    public IReadOnlyList<string> DiscoveryErrors { get; private set; } = [];
    public WindowsHidPlatform(IMonotonicClock? clock = null, HidDecoderRegistry? decoders = null)
    { _clock = clock ?? StopwatchClock.Instance; _decoders = decoders ?? new(); }

    public IReadOnlyList<DeviceInfo> Enumerate()
    {
        RequireWindows();
        HidNative.HidD_GetHidGuid(out var guid);
        var set = HidNative.SetupDiGetClassDevsW(ref guid, null, 0, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == -1) throw Win32("HID一覧の取得に失敗");
        List<DeviceInfo> devices = [];
        List<string> errors = [];
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new HidDeviceInterfaceData { Size = Marshal.SizeOf<HidDeviceInterfaceData>() };
                if (!HidNative.SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == HidNative.ErrorNoMoreItems) break;
                    throw new Win32Exception(error, "HID interface列挙エラー");
                }
                HidNative.SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out var required, 0);
                if (required < 6 || required > 1024 * 1024) { errors.Add("HID pathサイズが無効です"); continue; }
                var detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!HidNative.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, 0))
                        throw Win32("HID pathの取得に失敗");
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrEmpty(path)) continue;
                    using var handle = OpenHandle(path, reading: false);
                    using var metadata = HidMetadata.Load(handle, path);
                    if (metadata.Capabilities.UsagePage == 1 && metadata.Capabilities.Usage is 4 or 5)
                        devices.Add(metadata.Device);
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
                { errors.Add(ex.Message); }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { HidNative.SetupDiDestroyDeviceInfoList(set); }
        DiscoveryErrors = errors;
        return devices;
    }

    public IHidConnection Open(DeviceInfo device)
    {
        RequireWindows();
        if (device.Backend != BackendKind.HidDirect || string.IsNullOrEmpty(device.Path))
            throw new ArgumentException("HID device pathが必要です");
        return new WindowsHidConnection(device.Path, _clock, _decoders);
    }
    internal static SafeFileHandle OpenHandle(string path, bool reading)
    {
        var handle = HidNative.CreateFileW(path, reading ? 0x80000000u : 0, 3, 0, 3,
            reading ? 0x40000080u : 0x80, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, $"HIDを開けません ({error}): {path}");
    }
    internal static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HID直接読み取りはWindows専用です");
    }
    internal static Win32Exception Win32(string message) => new(Marshal.GetLastWin32Error(), message);
}

internal sealed class HidMetadata : IDisposable
{
    public nint Preparsed { get; private set; }
    public HidCapabilities Capabilities { get; }
    public DeviceInfo Device { get; }
    private HidMetadata(nint data, HidCapabilities caps, DeviceInfo device)
    { Preparsed = data; Capabilities = caps; Device = device; }

    public static HidMetadata Load(SafeFileHandle handle, string path)
    {
        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
            throw WindowsHidPlatform.Win32("HID descriptorを取得できません");
        try
        {
            var code = HidNative.HidP_GetCaps(preparsed, out var caps);
            if (code != HidNative.HidpSuccess) throw new IOException($"HidP_GetCapsエラー: 0x{code:X8}");
            var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
            _ = HidNative.HidD_GetAttributes(handle, ref attributes);
            var name = ReadProduct(handle) ?? "HID Game Controller";
            ImmutableArray<AxisDescriptor> descriptors = [];
            if (caps.NumberInputValueCaps > 0)
            {
                var length = caps.NumberInputValueCaps;
                var values = new HidValueCapability[length];
                code = HidNative.HidP_GetValueCaps(0, values, ref length, preparsed);
                if (code != HidNative.HidpSuccess) throw new IOException($"HidP_GetValueCapsエラー: 0x{code:X8}");
                descriptors = values.Take(length).SelectMany(c => HidDescriptorCodec.Expand(c))
                    .DistinctBy(a => a.Key).ToImmutableArray();
            }
            var device = new DeviceInfo($"hid:{path}", $"HID: {name}", BackendKind.HidDirect, descriptors,
                Path: path, VendorId: attributes.VendorId, ProductId: attributes.ProductId,
                Detail: $"VID {attributes.VendorId:X4} / PID {attributes.ProductId:X4} / TLC {caps.UsagePage:X4}:{caps.Usage:X4} / Report {caps.InputReportByteLength} bytes");
            return new(preparsed, caps, device);
        }
        catch { HidNative.HidD_FreePreparsedData(preparsed); throw; }
    }
    private static string? ReadProduct(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            for (int i = 0; i < 512; i++) Marshal.WriteByte(buffer, i, 0);
            return HidNative.HidD_GetProductString(handle, buffer, 512) ? Marshal.PtrToStringUni(buffer)?.Trim() : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose()
    {
        var pointer = Preparsed;
        Preparsed = 0;
        if (pointer != 0) HidNative.HidD_FreePreparsedData(pointer);
    }
}
