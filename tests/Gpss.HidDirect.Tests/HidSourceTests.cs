using System.Collections.Immutable;
using System.Threading.Channels;
using Gpss.Core;
using Gpss.Input.HidDirect;

namespace Gpss.HidDirect.Tests;

public sealed class HidSourceTests
{
    private static readonly DeviceInfo Device = new("hid:test", "Test HID", BackendKind.HidDirect,
        [new("lx", "X", 0, 255, 8), new("ly", "Y", 0, 255, 8),
         new("rx", "Rx", 0, 255, 8), new("ry", "Ry", 0, 255, 8), new("lt", "LT", 0, 255, 8)], Path: "test");
    private static readonly AxisMapping Mapping = new([new(Axis.LX, "lx"), new(Axis.LY, "ly"),
        new(Axis.RX, "rx"), new(Axis.RY, "ry")], [new("lt", "lt")]);

    [Fact]
    public async Task CatalogReturnsDescriptorsAndOnlyPresentAxesBecomeSamples()
    {
        var connection = new FakeConnection();
        var platform = new FakePlatform(() => connection);
        await using var source = new HidDirectInputSource(platform);
        Assert.Equal(Device, Assert.Single(await source.EnumerateAsync()));
        await source.StartAsync(Device, Mapping, new(), 5);
        connection.Push(1, 10); // Report1 contains LX only.
        connection.Push(2, 20); // Report2 contains LY only.
        var frames = await ReadFrames(source, 2);
        Assert.Equal(2, frames.Count);
        Assert.Equal(Axis.LX, Assert.Single(frames[0].Readings).Axis);
        Assert.Equal(Axis.LY, Assert.Single(frames[1].Readings).Axis);
        Assert.Equal(5, frames[0].Epoch);
        Assert.Equal(frames[0].Readings[0].ContinuityId, frames[1].Readings[0].ContinuityId);
    }

    [Fact]
    public async Task DecodedAuxiliaryValuesArePublishedOnTheirReportFrame()
    {
        var connection = new FakeConnection();
        await using var source = new HidDirectInputSource(new FakePlatform(() => connection));
        await source.StartAsync(Device, Mapping, new(), 6);
        connection.Push(3, 191); // The fake decoder emits a decoded LT value for this report.
        var frame = Assert.Single(await ReadFrames(source, 1));
        Assert.Empty(frame.Readings);
        var trigger = Assert.Single(frame.Auxiliary);
        Assert.Equal("lt", trigger.Name);
        Assert.Equal(191L, trigger.RawValue);
        Assert.Equal(2d * 191 / 255 - 1, trigger.Value);
        Assert.Equal(0L, trigger.LogicalMin);
        Assert.Equal(255L, trigger.LogicalMax);
    }

    [Fact]
    public async Task StartRejectsDescriptorChangedSinceEnumerationBeforeReading()
    {
        var changedDevice = Device with { Axes = Device.Axes.SetItem(0, Device.Axes[0] with { LogicalMax = 1023 }) };
        var connection = new FakeConnection(changedDevice);
        await using var source = new HidDirectInputSource(new FakePlatform(() => connection));

        await source.StartAsync(Device, Mapping, new(), 7);
        await Until(() => source.Status.State == ConnectionState.Unsupported && connection.Disposed);

        Assert.Contains("変更", source.Status.Message);
        Assert.False(connection.Pending);
        Assert.True(connection.Disposed);
        Assert.Empty(Drain(source));
    }

    [Fact]
    public async Task InvalidInputBreaksContinuityBeforeNextValidReading()
    {
        var connection = new FakeConnection();
        await using var source = new HidDirectInputSource(new FakePlatform(() => connection));
        await source.StartAsync(Device, Mapping, new(), 1);
        connection.Push(1, 10);
        connection.Push(255, 0); // Simulated invalid/Null parsing result.
        connection.Push(1, 20);
        var allFrames = await ReadFrames(source, 3);
        Assert.Equal(3, allFrames.Count);
        var frames = allFrames.Where(f => !f.Readings.IsEmpty).ToList();
        Assert.NotEqual(frames[0].Readings[0].ContinuityId, frames[1].Readings[0].ContinuityId);
        Assert.Equal(0, source.Diagnostics.Dropped); // Parse errors are separate from queue losses.
    }

    [Fact]
    public async Task InvalidAxisDoesNotCutAnotherValidAxis()
    {
        var connection = new FakeConnection();
        await using var source = new HidDirectInputSource(new FakePlatform(() => connection));
        await source.StartAsync(Device, Mapping, new(), 1);
        connection.Push(1, 10); connection.Push(2, 10);
        connection.Push(254, 20); // Valid LX plus invalid LY.
        connection.Push(2, 30); connection.Push(1, 30);
        var frames = await ReadFrames(source, 5);
        Assert.Equal(frames[0].Readings[0].ContinuityId, frames[2].Readings[0].ContinuityId);
        Assert.Equal(frames[0].Readings[0].ContinuityId, frames[4].Readings[0].ContinuityId);
        Assert.NotEqual(frames[1].Readings[0].ContinuityId, frames[3].Readings[0].ContinuityId);
        Assert.Equal(1, source.Diagnostics.ParseErrors);
    }

    [Fact]
    public async Task CancellationCompletesOutstandingReadBeforeDisposal()
    {
        var connection = new FakeConnection();
        await using var source = new HidDirectInputSource(new FakePlatform(() => connection));
        await source.StartAsync(Device, Mapping, new(), 1);
        await Until(() => connection.Pending);
        Assert.False(connection.Disposed);
        await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(connection.ReadExited);
        Assert.True(connection.Disposed);
        Assert.False(connection.DisposedWhilePending);
    }

    [Fact]
    public async Task AReadDisconnectUpdatesStatusAndDisposesConnection()
    {
        var connection = new FakeConnection();
        await using var source = new HidDirectInputSource(new FakePlatform(() => connection));
        await source.StartAsync(Device, Mapping, new(), 1);
        connection.Disconnect();
        await Until(() => source.Status.State == ConnectionState.Disconnected && connection.Disposed);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task MissingDescriptorIsExplicitlyUnsupportedWithoutInventedZeroFrames()
    {
        var connection = new FakeConnection();
        await using var source = new HidDirectInputSource(new FakePlatform(() => connection));
        await source.StartAsync(Device, new([new(Axis.LX, "unknown")]), new(), 1);
        await Until(() => source.Status.State == ConnectionState.Unsupported && connection.Disposed);
        Assert.Empty(Drain(source));
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task RestartReopensDescriptorAndOldSessionCannotLeak()
    {
        var connections = new Queue<FakeConnection>([new(), new()]);
        var first = connections.Peek();
        var platform = new FakePlatform(() => connections.Dequeue());
        await using var source = new HidDirectInputSource(platform);
        await source.StartAsync(Device, Mapping, new(), 1);
        first.Push(1, 1);
        await Until(() => source.Diagnostics.Observations == 1);
        var second = connections.Peek();
        await source.ResetAsync(2);
        second.Push(1, 2);
        await Until(() => source.Status.Epoch == 2);
        var frame = Assert.Single(await ReadFrames(source, 1));
        Assert.Equal(2, frame.Epoch);
        Assert.Equal(2, frame.Readings[0].RawValue);
        Assert.True(first.Disposed);
        Assert.Equal(2, platform.OpenCount);
    }

    [Fact]
    public async Task BoundedQueueReportsLossAndNextSuccessfulFrameBreaksEdge()
    {
        var connection = new FakeConnection();
        await using var source = new HidDirectInputSource(new FakePlatform(() => connection));
        await source.StartAsync(Device, Mapping, new(), 1);
        for (int i = 0; i < 8195; i++) connection.Push(1, (byte)(i % 255));
        await Until(() => source.Diagnostics.Observations == 8195);
        Assert.Equal(3, source.Diagnostics.Dropped);
        var full = await ReadFrames(source, 8192);
        Assert.Equal(8192, full.Count);
        connection.Push(1, 23);
        var after = Assert.Single(await ReadFrames(source, 1));
        Assert.Equal(3, after.DroppedCount);
        Assert.NotEqual(full[^1].Readings[0].ContinuityId, after.Readings[0].ContinuityId);
    }

    private static List<InputFrame> Drain(HidDirectInputSource source)
    {
        List<InputFrame> frames = [];
        while (source.TryRead(out var frame)) frames.Add(frame!);
        return frames;
    }
    private static async Task<List<InputFrame>> ReadFrames(HidDirectInputSource source, int count)
    {
        List<InputFrame> frames = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
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
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Input condition was not met");
            await Task.Delay(5);
        }
    }
    private sealed class FakePlatform(Func<FakeConnection> connection) : IHidPlatform
    {
        public int OpenCount;
        public IReadOnlyList<DeviceInfo> Enumerate() => [Device];
        public IHidConnection Open(DeviceInfo device) { OpenCount++; return connection(); }
    }
    private sealed class FakeConnection : IHidConnection
    {
        private readonly Channel<ImmutableArray<byte>> _reports = Channel.CreateUnbounded<ImmutableArray<byte>>();
        private long _time;
        public DeviceInfo Device { get; }
        public string DecoderId => "test-hid";
        public volatile bool Pending, ReadExited, Disposed, DisposedWhilePending;
        public FakeConnection(DeviceInfo? device = null) => Device = device ?? HidSourceTests.Device;
        public void Push(byte report, byte value) => _reports.Writer.TryWrite([report, value]);
        public void Disconnect() => _reports.Writer.TryComplete(new HidDeviceDisconnectedException("切断"));
        public async Task<HidReadResult?> ReadAsync(AcquisitionSettings settings, CancellationToken cancellationToken)
        {
            Pending = true; ReadExited = false;
            try
            {
                var report = await _reports.Reader.ReadAsync(cancellationToken);
                return new(Interlocked.Increment(ref _time), report);
            }
            catch (ChannelClosedException ex) when (ex.InnerException is HidDeviceDisconnectedException disconnected)
            { throw disconnected; }
            finally { Pending = false; ReadExited = true; }
        }
        public HidDecodeResult Decode(ReadOnlySpan<byte> report, AxisMapping mapping, long continuityId)
        {
            if (report[0] == 255) return new([], true);
            if (report[0] == 254) return new([new(Axis.LX, report[1], 2.0 * report[1] / 255 - 1, continuityId)], true, [Axis.LY]);
            if (report[0] == 3 && mapping.AuxiliaryBindings.Any(b => b.Name == "lt" && b.DescriptorKey == "lt"))
                return new([], Auxiliary: [new("lt", report[1], 2.0 * report[1] / 255 - 1, 0, 255)]);
            var axis = report[0] == 1 ? Axis.LX : Axis.LY;
            return new([new(axis, report[1], 2.0 * report[1] / 255 - 1, continuityId)]);
        }
        public ValueTask DisposeAsync()
        { DisposedWhilePending = Pending; Disposed = true; return ValueTask.CompletedTask; }
    }
}
