using System.Collections.Immutable;
using System.Threading.Channels;
using Gpss.Core;
using Gpss.Input;
using Gpss.Input.Simulation;

namespace Gpss.Application.Tests;

public sealed class InputPositionTests
{
    private sealed class ProbeSource : InputSourceBase
    {
        private readonly Channel<InputFrame> _frames = Channel.CreateUnbounded<InputFrame>();
        public override BackendKind Kind => BackendKind.Simulation;

        public override Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeviceInfo>>([SimulationInputSource.DemoDevice]);

        protected override async Task CaptureAsync(InputSession session, CancellationToken cancellationToken)
        {
            SetStatus(session.Epoch, ConnectionState.Connected, session.Device);
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                PublishFrame(frame);
        }

        public bool Send(InputFrame frame) => _frames.Writer.TryWrite(frame);
        public void SetConnection(ConnectionState state)
            => SetStatus(Status.Epoch, state, Status.Device, state.ToString());
    }

    private static InputFrame Frame(long epoch, long sequence, long timestamp, params AxisReading[] readings)
        => new(BackendKind.Simulation, epoch, sequence, timestamp, readings.ToImmutableArray(), "fixture");

    private static async Task WaitFor(Func<bool> predicate)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < stopAt)
        {
            if (predicate()) return;
            await Task.Delay(1);
        }
        Assert.True(predicate(), "Expected input position state was not published in time.");
    }

    private static Task Start(ProbeSource source, long epoch)
        => source.StartAsync(SimulationInputSource.DemoDevice, AxisMapping.Empty, new AcquisitionSettings(), epoch);

    [Fact]
    public async Task LatestPositionAdvancesWithoutConsumingObservationQueue()
    {
        await using var source = new ProbeSource();
        await Start(source, 7);
        await WaitFor(() => source.Status.State == ConnectionState.Connected);
        Assert.True(source.Send(Frame(7, 1, 100,
            new AxisReading(Axis.LX, 10, .1, 2), new AxisReading(Axis.LY, 20, .2, 3))));
        Assert.True(source.Send(Frame(7, 2, 200, new AxisReading(Axis.LX, 11, .3, 2))));

        await WaitFor(() => source.LatestPosition?.Revision == 2);

        var latest = Assert.IsAssignableFrom<ILatestPositionSource>(source).LatestPosition!;
        Assert.Equal(7L, latest.Epoch);
        Assert.Equal(.3, latest.LX!.Value.Value);
        Assert.Equal(200L, latest.LX.Value.Timestamp);
        Assert.Equal(.2, latest.LY!.Value.Value);
        Assert.Equal(100L, latest.LY.Value.Timestamp);
        Assert.Null(latest.RX); Assert.Null(latest.RY);
        Assert.Equal(2, source.GetBufferDiagnostics().Count);
        Assert.Equal(2, source.BufferDiagnostics.Count);
        Assert.True(source.TryRead(out _));
        Assert.Equal(1, source.BufferDiagnostics.Count);
    }

    [Fact]
    public async Task PartialAndInvalidReadingsPreserveLastValidAxisSamples()
    {
        await using var source = new ProbeSource();
        await Start(source, 2);
        await WaitFor(() => source.Status.State == ConnectionState.Connected);
        source.Send(Frame(2, 1, 10,
            new AxisReading(Axis.LX, 10, -.1, 0), new AxisReading(Axis.LY, 20, .2, 0),
            new AxisReading(Axis.RX, 30, .3, 0), new AxisReading(Axis.RY, 40, .4, 0)));
        await WaitFor(() => source.LatestPosition?.Revision == 1);

        source.Send(Frame(2, 2, 20,
            new AxisReading(Axis.LX, 11, double.NaN, 0), new AxisReading(Axis.LY, 21, 1.01, 0),
            new AxisReading(Axis.RX, 31, -.5, 1), new AxisReading(Axis.RY, 41, double.PositiveInfinity, 0)));
        await WaitFor(() => source.GetBufferDiagnostics().Count == 2);
        var snapshot = source.LatestPosition!;
        Assert.Equal(2L, snapshot.Revision);
        Assert.Equal((10L, 10L, -.1), (snapshot.LX!.Value.RawValue, snapshot.LX.Value.Timestamp, snapshot.LX.Value.Value));
        Assert.Equal((20L, 10L, .2), (snapshot.LY!.Value.RawValue, snapshot.LY.Value.Timestamp, snapshot.LY.Value.Value));
        Assert.Equal((31L, 20L, -.5), (snapshot.RX!.Value.RawValue, snapshot.RX.Value.Timestamp, snapshot.RX.Value.Value));
        Assert.Equal((40L, 10L, .4), (snapshot.RY!.Value.RawValue, snapshot.RY.Value.Timestamp, snapshot.RY.Value.Value));

        source.Send(Frame(2, 3, 30,
            new AxisReading(Axis.LX, 12, double.NaN, 0), new AxisReading(Axis.LY, 22, 2, 0)));
        await WaitFor(() => source.GetBufferDiagnostics().Count == 3);
        Assert.Equal(2L, source.LatestPosition!.Revision);
        Assert.Equal(20L, source.LatestPosition.RX!.Value.Timestamp);
    }

    [Fact]
    public async Task EpochChangeAndNonConnectedStatusClearOldPositionAndRejectStaleFrames()
    {
        await using var source = new ProbeSource();
        await Start(source, 10);
        await WaitFor(() => source.Status.State == ConnectionState.Connected);
        source.Send(Frame(10, 1, 100, new AxisReading(Axis.RX, 5, .5, 0)));
        await WaitFor(() => source.LatestPosition is not null);
        Assert.Equal(10L, source.LatestPosition!.Epoch);

        await Start(source, 11);
        Assert.Null(source.LatestPosition);
        await WaitFor(() => source.Status.State == ConnectionState.Connected && source.Status.Epoch == 11);
        source.Send(Frame(10, 2, 200, new AxisReading(Axis.RX, 6, .6, 0)));
        await WaitFor(() => source.BufferDiagnostics.Count == 1);
        Assert.Null(source.LatestPosition);

        source.Send(Frame(11, 3, 300, new AxisReading(Axis.RX, 7, .7, 0)));
        await WaitFor(() => source.LatestPosition?.Epoch == 11);
        source.SetConnection(ConnectionState.Disconnected);
        Assert.Null(source.LatestPosition);
    }

    [Fact]
    public async Task OutOfOrderFramesCannotReplaceTheLatestValidPosition()
    {
        await using var source = new ProbeSource();
        await Start(source, 1);
        await WaitFor(() => source.Status.State == ConnectionState.Connected);
        source.Send(Frame(1, 1, 100, new AxisReading(Axis.LX, 1, .1, 0)));
        await WaitFor(() => source.LatestPosition?.Revision == 1);
        source.Send(Frame(1, 0, 200, new AxisReading(Axis.LX, 0, .9, 0)));
        source.Send(Frame(1, 2, 99, new AxisReading(Axis.LX, 2, .8, 0)));
        source.Send(Frame(1, 2, 200, new AxisReading(Axis.LX, 2, .7, 0)));
        await WaitFor(() => source.BufferDiagnostics.Count == 4);
        Assert.Equal(.1, source.LatestPosition!.LX!.Value.Value);
        source.Send(Frame(1, 3, 300, new AxisReading(Axis.LX, 3, .3, 0)));
        await WaitFor(() => source.LatestPosition?.Revision == 2);
        Assert.Equal(.3, source.LatestPosition!.LX!.Value.Value);
    }

    [Fact]
    public async Task LatestPositionRemainsCurrentWhenObservationQueueDropsFrames()
    {
        await using var source = new ProbeSource();
        await Start(source, 4);
        await WaitFor(() => source.Status.State == ConnectionState.Connected);
        const int total = 8_200;
        for (var i = 0; i < total; i++)
            source.Send(Frame(4, i, i, new AxisReading(Axis.LX, i, (i % 200) / 100d - 1, 0)));

        await WaitFor(() => source.LatestPosition?.Revision == total);
        var diagnostics = Assert.IsAssignableFrom<IInputBufferDiagnostics>(source).BufferDiagnostics;
        Assert.Equal(8_192, diagnostics.Count);
        Assert.Equal(8_192 * 192, diagnostics.Bytes);
        Assert.Equal(0L, diagnostics.OldestTimestamp);
        Assert.Equal((long)total - 1, source.LatestPosition!.LX!.Value.Timestamp);
        Assert.Equal((double)((total - 1) % 200) / 100 - 1, source.LatestPosition.LX.Value.Value);
    }
}
