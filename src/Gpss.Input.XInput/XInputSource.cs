using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Gpss.Core;

namespace Gpss.Input.XInput;

[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger, RightTrigger;
    public short LX, LY, RX, RY;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

public interface IXInputApi
{
    uint GetState(uint slot, out XInputState state);
}

public sealed class WindowsXInputApi : IXInputApi
{
    public uint GetState(uint slot, out XInputState state)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("XInputはWindows専用です");
        return XInputGetState(slot, out state);
    }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}

/// <summary>Records state changes, not USB input-report traffic.</summary>
public sealed class XInputSource : InputSourceBase
{
    public const uint DeviceNotConnected = 1167;
    private readonly IXInputApi _api;
    public override BackendKind Kind => BackendKind.XInput;
    public XInputSource(IMonotonicClock? clock = null) : this(new WindowsXInputApi(), clock) { }
    public XInputSource(IXInputApi api, IMonotonicClock? clock = null) : base(clock) => _api = api;

    public static ImmutableArray<AxisDescriptor> Descriptors { get; } = Enum.GetValues<Axis>()
        .Select(a => new AxisDescriptor(a.ToString(), a.ToString(), short.MinValue, short.MaxValue)).ToImmutableArray();
    public static DeviceInfo Device(uint slot) => new($"xinput:{slot}", $"XInput: slot {slot}",
        BackendKind.XInput, Descriptors, XInputSlot: slot,
        Detail: "状態変化の検出（USBレポート受信頻度ではありません）");

    public override Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DeviceInfo>>(() =>
        {
            List<DeviceInfo> devices = [];
            for (uint slot = 0; slot < 4; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = _api.GetState(slot, out _);
                if (result == 0) devices.Add(Device(slot));
                else if (result != DeviceNotConnected) throw new IOException($"XInput列挙エラー: {result}");
            }
            return devices;
        }, cancellationToken);

    protected override async Task CaptureAsync(InputSession session, CancellationToken cancellationToken)
    {
        var slot = session.Device.XInputSlot ?? throw new ArgumentException("XInput slotが必要です");
        if (slot > 3) throw new ArgumentOutOfRangeException(nameof(slot));
        if (session.Mapping.Bindings.IsEmpty)
        {
            SetStatus(session.Epoch, ConnectionState.Unsupported, session.Device, "4軸の割当を確認してください");
            return;
        }
        if (session.Mapping.Bindings.Select(b => b.Axis).Distinct().Count() != session.Mapping.Bindings.Length ||
            session.Mapping.Bindings.Any(b => !Enum.TryParse<Axis>(b.DescriptorKey, out _)))
            throw new ArgumentException("XInputの軸割当が不正です");
        uint? previous = null;
        long sequence = 0;
        var burstStart = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            RecordApiCall();
            var code = _api.GetState(slot, out var state);
            var timestamp = Clock.Timestamp;
            if (code == DeviceNotConnected)
            {
                SetStatus(session.Epoch, ConnectionState.Disconnected, session.Device, "XInput機器が切断されました。再接続後に選択してください");
                return;
            }
            if (code != 0) throw new IOException($"XInputGetStateエラー: {code}");
            RecordApiSuccess();
            SetStatus(session.Epoch, ConnectionState.Connected, session.Device, "XInput 状態更新を計測中");
            if (previous is null || previous != state.PacketNumber)
            {
                if (previous is uint old && unchecked(state.PacketNumber - old) != 1)
                {
                    // XInput exposes the latest state, not a stream of USB reports.
                    // A packet jump is diagnostic information, not an application loss.
                    // Keep adjacent observed values eligible for step estimation.
                    RecordPotentialStateSkip();
                }
                var readings = session.Mapping.Bindings.Select(binding =>
                {
                    var raw = GetRawAxis(state.Gamepad, Enum.Parse<Axis>(binding.DescriptorKey));
                    return new AxisReading(binding.Axis, raw,
                        AxisNormalization.Normalize(raw, short.MinValue, short.MaxValue, binding.Inverted), 0);
                }).ToImmutableArray();
                PublishFrame(new(Kind, session.Epoch, sequence++, timestamp, readings,
                    "xinput_state_v1", session.Settings.Mode, NativePacketNumber: state.PacketNumber, Auxiliary: [
                        new("lt", state.Gamepad.LeftTrigger, AxisNormalization.Normalize(state.Gamepad.LeftTrigger, 0, 255), 0, 255),
                        new("rt", state.Gamepad.RightTrigger, AxisNormalization.Normalize(state.Gamepad.RightTrigger, 0, 255), 0, 255)]));
                previous = state.PacketNumber;
            }
            // Low-latency acquisition is the only supported live mode.
            // Bound each spin burst and check cancellation on every API iteration.
            var budget = Math.Max(1, session.Settings.BusyWaitMicroseconds);
            if (Stopwatch.GetElapsedTime(burstStart).TotalMicroseconds >= budget)
            {
                if (session.Settings.LowLatencyYieldMicroseconds > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(session.Settings.LowLatencyYieldMicroseconds / 1000))), cancellationToken).ConfigureAwait(false);
                else await Task.Yield();
                burstStart = Stopwatch.GetTimestamp();
            }
            else Thread.SpinWait(1);
        }
    }
    public static short GetRawAxis(XInputGamepad state, Axis axis) => axis switch
    {
        Axis.LX => state.LX, Axis.LY => state.LY, Axis.RX => state.RX, Axis.RY => state.RY,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };
}
