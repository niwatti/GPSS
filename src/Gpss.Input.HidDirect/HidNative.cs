using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gpss.Input.HidDirect;

[StructLayout(LayoutKind.Sequential)]
public struct HidCapabilities
{
    public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
    public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
}

// HIDP_VALUE_CAPS has BOOLEAN fields (one byte), not Win32 BOOL (four bytes).
[StructLayout(LayoutKind.Explicit, Size = 72)]
public struct HidValueCapability
{
    [FieldOffset(0)] public ushort UsagePage;
    [FieldOffset(2)] public byte ReportId;
    [FieldOffset(3)] public byte IsAlias;
    [FieldOffset(4)] public ushort BitField;
    [FieldOffset(6)] public ushort LinkCollection;
    [FieldOffset(8)] public ushort LinkUsage;
    [FieldOffset(10)] public ushort LinkUsagePage;
    [FieldOffset(12)] public byte IsRange;
    [FieldOffset(13)] public byte IsStringRange;
    [FieldOffset(14)] public byte IsDesignatorRange;
    [FieldOffset(15)] public byte IsAbsolute;
    [FieldOffset(16)] public byte HasNull;
    [FieldOffset(18)] public ushort BitSize;
    [FieldOffset(20)] public ushort ReportCount;
    [FieldOffset(32)] public uint UnitsExp;
    [FieldOffset(36)] public uint Units;
    [FieldOffset(40)] public int LogicalMin;
    [FieldOffset(44)] public int LogicalMax;
    [FieldOffset(48)] public int PhysicalMin;
    [FieldOffset(52)] public int PhysicalMax;
    [FieldOffset(56)] public ushort UsageMin;
    [FieldOffset(58)] public ushort UsageMax;
    // Union NotRange.Usage is at the same location as Range.UsageMin.
}

[StructLayout(LayoutKind.Sequential)]
public struct HidAttributes
{
    public int Size;
    public ushort VendorId, ProductId, VersionNumber;
}
[StructLayout(LayoutKind.Sequential)]
public struct HidDeviceInterfaceData
{
    public int Size;
    public Guid InterfaceClassGuid;
    public uint Flags;
    public nint Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public struct HidOverlapped
{
    public nuint Internal, InternalHigh;
    public uint Offset, OffsetHigh;
    public nint Event;
}

internal static class HidNative
{
    internal const int ErrorNoMoreItems = 259, ErrorIoPending = 997, ErrorIoIncomplete = 996,
        ErrorOperationAborted = 995, ErrorNotFound = 1168;
    internal const uint WaitObject0 = 0, WaitTimeout = 258;
    internal const int HidpSuccess = 0x00110000;

    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SetupDiGetClassDevsW(ref Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, ref Guid guid, uint index, ref HidDeviceInterfaceData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref HidDeviceInterfaceData data,
        nint detail, uint size, out uint required, nint deviceData);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        nint security, uint disposition, uint flags, nint template);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, nint buffer, uint length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(nint data, out HidCapabilities caps);
    [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int reportType,
        [Out] HidValueCapability[] caps, ref ushort length, nint data);
    [DllImport("hid.dll")] internal static extern int HidP_GetUsageValue(int reportType,
        ushort usagePage, ushort linkCollection, ushort usage, out uint value,
        nint preparsed, byte[] report, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeWaitHandle CreateEventW(nint security,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ResetEvent(SafeWaitHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadFile(SafeFileHandle handle,
        nint buffer, uint count, nint bytesRead, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetOverlappedResult(SafeFileHandle handle,
        nint overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CancelIoEx(SafeFileHandle handle, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
