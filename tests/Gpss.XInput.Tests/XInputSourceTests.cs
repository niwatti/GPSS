using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Gpss.Core;
using Gpss.Input.XInput;

namespace Gpss.XInput.Tests;

public sealed class XInputSourceTests
{
    private static readonly AxisMapping Mapping = new(Enum.GetValues<Axis>()
        .Select(a => new AxisBinding(a, a.ToString())).ToImmutableArray());

    [Fact]
    public void NativeLayoutsMatchWindowsSdk()
    {
        Assert.Equal(12, Marshal.SizeOf<XInputGamepad>());
        Assert.Equal(16, Marshal.SizeOf<XInputState>());
        Assert.Equal(4, Marshal.OffsetOf<XInputState>(nameof(XInputState.Gamepad)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<XInputGamepad>(nameof(XInputGamepad.LX)).ToInt32());
    }

    [Fact]
    public async Task EnumeratesOnlyConnectedSlotsAndDoesNotConflateIdentity()
    {
        await using var source = new XInputSource(new CatalogApi());
        var devices = await source.EnumerateAsync();
        Assert.Equal(new uint?[] { 1, 3 }, devices.Select(d => d.XInputSlot));
        Assert.All(devices, d => Assert.Equal(BackendKind.XInput, d.Backend));
    }

    [Fact]
    public async Task OnlyInitialAndChangedPacketsAreSamples()
    {
        var api = new ScriptedApi(
            State(5, -32768), State(5, -32768), State(6, 32767), State(6, 32767));
        await using var source = new XInputSource(api);
        await source.StartAsync(XInputSource.Device(0), Mapping, new(), 4);
        await Until(() => source.Diagnostics.ApiCalls >= 8);
        var frames = Drain(source);
        Assert.Equal(2, frames.Count);
        Assert.Equal(new uint?[] { 5, 6 }, frames.Select(f => f.NativePacketNumber));
        Assert.Equal(-1, frames[0].Readings.Single(r => r.Axis == Axis.LX).Value);
        Assert.Equal(1, frames[1].Readings.Single(r => r.Axis == Axis.LX).Value);
        Assert.All(frames, f => Assert.Equal(4, f.Epoch));
        Assert.NotNull(source.Diagnostics.LastApiSuccess);
        Assert.Equal(ConnectionState.Connected, source.Status.State);
    }

    [Fact]
    public async Task TriggerAuxiliaryValuesCoverAndNormalizeTheFullByteRange()
    {
        await using var source = new XInputSource(new ScriptedApi(
            State(20, 0, leftTrigger: 0, rightTrigger: 255),
            State(21, 0, leftTrigger: 255, rightTrigger: 0)));
        await source.StartAsync(XInputSource.Device(0), Mapping, new(), 8);
        var frames = await ReadFrames(source, 2);
        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 0, 255 }, frames.Select(f => checked((byte)f.Auxiliary.Single(a => a.Name == "lt").RawValue)).ToArray());
        Assert.Equal(new byte[] { 255, 0 }, frames.Select(f => checked((byte)f.Auxiliary.Single(a => a.Name == "rt").RawValue)).ToArray());
        Assert.Equal(new[] { -1d, 1d }, frames.Select(f => f.Auxiliary.Single(a => a.Name == "lt").Value).ToArray());
        Assert.Equal(new[] { 1d, -1d }, frames.Select(f => f.Auxiliary.Single(a => a.Name == "rt").Value).ToArray());
        Assert.All(frames.SelectMany(f => f.Auxiliary), a => { Assert.Equal(0L, a.LogicalMin); Assert.Equal(255L, a.LogicalMax); });
    }

    [Fact]
    public async Task ButtonOnlyUpdatesStillCarryStickStateAndSamePacketIsNotRepeated()
    {
        var a = State(10, 100);
        var b = State(11, 100); b.Gamepad.Buttons = 1;
        await using var source = new XInputSource(new ScriptedApi(a, a, b));
        await source.StartAsync(XInputSource.Device(0), Mapping, new(), 1);
        await Until(() => source.Diagnostics.Observations == 2);
        var frames = Drain(source);
        Assert.Equal(frames[0].Readings[0].RawValue, frames[1].Readings[0].RawValue);
        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public async Task WrapAndPacketJumpsKeepObservedSamplesContinuousWithoutCountingUsbDrops()
    {
        await using var source = new XInputSource(new ScriptedApi(State(uint.MaxValue, 1), State(0, 2), State(2, 3)));
        await source.StartAsync(XInputSource.Device(0), Mapping, new(), 2);
        var frames = await ReadFrames(source, 3);
        Assert.Equal(frames[0].Readings[0].ContinuityId, frames[1].Readings[0].ContinuityId);
        Assert.Equal(frames[1].Readings[0].ContinuityId, frames[2].Readings[0].ContinuityId);
        Assert.Equal(1, source.Diagnostics.PotentialStateSkips);
        Assert.Equal(0, source.Diagnostics.Dropped);
    }

    [Theory]
    [InlineData(AcquisitionMode.Normal)]
    [InlineData(AcquisitionMode.LowLatency)]
    public async Task EightUpdatePacketJumpsStillProduceBitsAndHistogramForAllFourAxes(AcquisitionMode mode)
    {
        var initial = new XInputState { PacketNumber = 0 };
        var second = new XInputState { PacketNumber = 8, Gamepad = new() { LX = 256, LY = -256, RX = 256, RY = -256 } };
        var third = new XInputState { PacketNumber = 16, Gamepad = new() { LX = 512, LY = -512, RX = 512, RY = -512 } };
        await using var source = new XInputSource(new ScriptedApi(initial, second, third));
        await source.StartAsync(XInputSource.Device(0), Mapping, new(mode), 7);
        var frames = await ReadFrames(source, 3);
        foreach (var axis in Enum.GetValues<Axis>())
        {
            var window = new LiveAxisWindow(axis, StopwatchClock.Instance, epoch: 7);
            foreach (var frame in frames)
            {
                var reading = frame.Readings.Single(r => r.Axis == axis);
                window.Append(new(frame.Epoch, frame.Sequence, frame.Timestamp, reading.RawValue, reading.Value, reading.ContinuityId));
            }
            var result = window.Analyze(frames[^1].Timestamp, new MeanBitStepEstimator());
            Assert.Equal(2, result.Analysis.Step.NonzeroCount);
            Assert.Equal("8.0bit", StepPrecision.FormatBits(result.Analysis.Step.Bits));
            Assert.Equal(new StepHistogramBin(8, 2), Assert.Single(result.StepHistogram));
        }
        Assert.Equal(2, source.Diagnostics.PotentialStateSkips);
        Assert.Equal(0, source.Diagnostics.Dropped);
    }

    [Fact]
    public async Task ResetStartsNewEpochWithInitialStateAgain()
    {
        await using var source = new XInputSource(new ScriptedApi(State(10, 2)));
        await source.StartAsync(XInputSource.Device(0), Mapping, new(), 1);
        await Until(() => source.Diagnostics.Observations == 1);
        await source.ResetAsync(2);
        await Until(() => source.Diagnostics.Observations == 1 && source.Status.Epoch == 2);
        var frame = Assert.Single(Drain(source));
        Assert.Equal(2, frame.Epoch);
        Assert.Equal(0, frame.Sequence);
    }

    [Fact]
    public async Task DisconnectAndApiFailureBecomeExplicitStates()
    {
        await using var disconnected = new XInputSource(new ErrorApi(1167));
        await disconnected.StartAsync(XInputSource.Device(0), Mapping, new(), 1);
        await Until(() => disconnected.Status.State == ConnectionState.Disconnected);
        Assert.Empty(Drain(disconnected));
        await using var failed = new XInputSource(new ErrorApi(5));
        await failed.StartAsync(XInputSource.Device(0), Mapping, new(), 1);
        await Until(() => failed.Status.State == ConnectionState.Error);
        Assert.Contains("5", failed.Status.Message);
    }

    [Fact]
    public async Task InvalidMappingsAreNotReadAsValidAxes()
    {
        await using var source = new XInputSource(new ScriptedApi(State(1, 0)));
        await source.StartAsync(XInputSource.Device(0), new([new(Axis.LX, "not_an_axis")]), new(), 1);
        await Until(() => source.Status.State == ConnectionState.Error);
        Assert.Empty(Drain(source));
    }

    [Fact]
    public async Task LowLatencyModeStillStopsAndCarriesModeMetadata()
    {
        await using var source = new XInputSource(new ScriptedApi(State(1, 0)));
        await source.StartAsync(XInputSource.Device(0), Mapping,
            new(AcquisitionMode.LowLatency, BusyWaitMicroseconds: 100, LowLatencyYieldMicroseconds: 0), 1);
        // The observation counter is incremented before queue publication.
        await Until(() => source.GetBufferDiagnostics().Count == 1);
        var frame = Assert.Single(Drain(source));
        Assert.Equal(AcquisitionMode.LowLatency, frame.Mode);
        await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ConnectionState.Stopped, source.Status.State);
    }

    [Fact]
    public async Task LegacyNormalSettingsAreForcedToLowLatencyWithoutDuplicatePackets()
    {
        await using var source = new XInputSource(new ScriptedApi(State(1, 0)));
        await source.StartAsync(XInputSource.Device(0), Mapping, new(AcquisitionMode.Normal, BusyWaitMicroseconds: 0), 1);
        var frame = Assert.Single(await ReadFrames(source, 1));
        Assert.Equal(AcquisitionMode.LowLatency, frame.Mode);
        // A bounded spin burst may contain several calls; CPU speed does not
        // determine an API-call-rate contract. Check observable session behavior.
        await Until(() => source.Diagnostics.ApiCalls >= 8);
        Assert.Equal(1, source.Diagnostics.Observations);
        await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ConnectionState.Stopped, source.Status.State);
    }

    [Fact]
    public async Task OffWindowsRealApiFailsClearly()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var source = new XInputSource();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => source.EnumerateAsync());
    }

    private static XInputState State(uint packet, short lx, byte leftTrigger = 0, byte rightTrigger = 0) => new()
    { PacketNumber = packet, Gamepad = new() { LX = lx, LY = 5, RX = -5, RY = 0, LeftTrigger = leftTrigger, RightTrigger = rightTrigger } };
    private static List<InputFrame> Drain(XInputSource source)
    {
        List<InputFrame> frames = [];
        while (source.TryRead(out var frame)) frames.Add(frame!);
        return frames;
    }
    private static async Task<List<InputFrame>> ReadFrames(XInputSource source, int count)
    {
        List<InputFrame> frames = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (frames.Count < count)
        {
            if (source.TryRead(out var frame)) frames.Add(frame!);
            else
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Received {frames.Count} of {count} frames");
                await Task.Delay(5);
            }
        }
        return frames;
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Input condition was not met");
            await Task.Delay(5);
        }
    }
    private sealed class ScriptedApi(params XInputState[] states) : IXInputApi
    {
        private int _index = -1;
        public uint GetState(uint slot, out XInputState state)
        { state = states[Math.Min(Interlocked.Increment(ref _index), states.Length - 1)]; return 0; }
    }
    private sealed class ErrorApi(uint error) : IXInputApi
    { public uint GetState(uint slot, out XInputState state) { state = default; return error; } }
    private sealed class CatalogApi : IXInputApi
    { public uint GetState(uint slot, out XInputState state) { state = default; return slot is 1 or 3 ? 0u : 1167u; } }
}
