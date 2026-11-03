using System.Collections.Concurrent;
using System.Collections.Immutable;
using Gpss.Application;
using Gpss.Core;
using Gpss.Input;
using Gpss.Input.Simulation;

namespace Gpss.Application.Tests;

public sealed class ControllerTests
{
    private sealed class Clock : IMonotonicClock
    {
        public long Now = 1_000_000_000;
        public long Timestamp => Interlocked.Read(ref Now);
        public long Frequency => 1_000_000_000;
    }
    private sealed class ManualSource(BackendKind kind, IMonotonicClock clock) : IInputSource
    {
        private readonly ConcurrentQueue<InputFrame> _queue = new();
        public BackendKind Kind => kind;
        public InputStatus Status { get; private set; } = new(0, ConnectionState.Searching);
        public InputDiagnostics Diagnostics { get; private set; } = new();
        public bool Stopped { get; private set; }
        public Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeviceInfo>>([Device(kind)]);
        public Task StartAsync(DeviceInfo device, AxisMapping mapping, AcquisitionSettings settings, long epoch, CancellationToken cancellationToken = default)
        { Status = new(epoch, ConnectionState.Connected, device); return Task.CompletedTask; }
        public Task SelectDeviceAsync(DeviceInfo device, AxisMapping mapping, long epoch, CancellationToken cancellationToken = default)
            => StartAsync(device, mapping, new(), epoch, cancellationToken);
        public Task SetModeAsync(AcquisitionSettings settings, long epoch, CancellationToken cancellationToken = default)
            => StartAsync(Status.Device!, AxisMapping.Empty, settings, epoch, cancellationToken);
        public Task ResetAsync(long epoch, CancellationToken cancellationToken = default)
            => StartAsync(Status.Device!, AxisMapping.Empty, new(), epoch, cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default)
        { Stopped = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Stopped = true; return ValueTask.CompletedTask; }
        public bool TryRead(out InputFrame? frame) => _queue.TryDequeue(out frame);
        public void Send(long sequence, long timestamp, double x, double y, bool partial = false, long? epoch = null)
        {
            ImmutableArray<AxisReading> readings = partial ? [new(Axis.LX, sequence, x, 0)] :
                [new(Axis.LX, sequence, x, 0), new(Axis.LY, sequence, y, 0),
                 new(Axis.RX, sequence, x / 4, 0), new(Axis.RY, sequence, y / 4, 0)];
            _queue.Enqueue(new(kind, epoch ?? Status.Epoch, sequence, timestamp, readings, "test"));
            Diagnostics = Diagnostics with { ApiCalls = Diagnostics.ApiCalls + 1,
                Observations = Diagnostics.Observations + 1, LastApiSuccess = clock.Timestamp };
        }
        public void Disconnect() => Status = Status with { State = ConnectionState.Disconnected };
    }
    private static DeviceInfo Device(BackendKind kind = BackendKind.Simulation) => new("test", "Test", kind,
        [new("LX", "Left X", -32768, 32767, UsagePage: 1, Usage: 0x30),
         new("LY", "Left Y", -32768, 32767, UsagePage: 1, Usage: 0x31),
         new("RX", "Right X", -32768, 32767, UsagePage: 1, Usage: 0x33),
         new("RY", "Right Y", -32768, 32767, UsagePage: 1, Usage: 0x34)]);

    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "Controller did not reach the expected state");
    }

    [Fact]
    public async Task FourAxesAndWeightedMeanPrecisionAreCalculatedOffUi()
    {
        var clock = new Clock(); ManualSource? source = null;
        await using var controller = new GpssController(k => source = new(k, clock), clock);
        await controller.SelectAsync(Device(), settings: new(AcquisitionMode.Normal));
        Assert.Equal(AcquisitionMode.LowLatency, controller.Current.Settings.Mode);
        for (var i = 0; i < 40; i++) source!.Send(i, clock.Now + i * 1_000_000, i % 2 / 128.0, i % 2 / 256.0);
        clock.Now += 40_000_000;
        controller.Start();
        await Wait(() => controller.Current.Analysis?.Axes[Axis.LX].Count == 40);
        Assert.Equal(8.5, controller.Current.Analysis!.LeftBits);
        Assert.Equal(10.5, controller.Current.Analysis.RightBits);
        Assert.Equal(9, controller.Current.Analysis.Axes[Axis.LY].Step.Bits);
        Assert.Equal(40, controller.Current.Analysis.Axes[Axis.RX].Count);
    }

    [Fact]
    public async Task PartialAxisAndEmptyWindowDoNotInventOtherAxesOrRemoveLastPosition()
    {
        var clock = new Clock(); ManualSource? source = null;
        await using var controller = new GpssController(k => source = new(k, clock), clock);
        await controller.SelectAsync(Device());
        source!.Send(0, clock.Now, .1, 0, partial: true);
        controller.Start();
        await Wait(() => controller.Current.Latest.Count == 1);
        clock.Now += 31 * clock.Frequency;
        await Wait(() => controller.Current.Analysis?.Axes[Axis.LX].Count == 0);
        Assert.Single(controller.Current.Latest);
        Assert.Null(controller.Current.Analysis!.LeftBits);
        Assert.Equal(ConnectionState.Connected, controller.Current.Status.State);
    }

    [Fact]
    public async Task ResetAndBackendChangeDiscardOldEpochs()
    {
        var clock = new Clock(); var sources = new List<ManualSource>();
        await using var controller = new GpssController(k => { var s = new ManualSource(k, clock); sources.Add(s); return s; }, clock);
        controller.Start();
        await controller.SelectAsync(Device());
        sources[^1].Send(0, clock.Now, 0, 0);
        await Wait(() => controller.Current.Latest.Count == 4);
        var epoch = controller.Current.Status.Epoch;
        await controller.ResetAsync();
        Assert.True(sources[0].Stopped);
        Assert.Empty(controller.Current.Latest);
        sources[^1].Send(1, clock.Now, .5, .5, epoch: epoch);
        clock.Now += clock.Frequency;
        await Task.Delay(30);
        Assert.Empty(controller.Current.Latest);
        sources[^1].Send(2, clock.Now, .25, .25);
        await Wait(() => controller.Current.Latest.Count == 4);
        Assert.All(controller.Current.Latest.Values, s => Assert.True(s.Epoch > epoch));
        await controller.ClearSelectionAsync(BackendKind.XInput);
        await Task.Delay(30);
        Assert.Empty(controller.Current.Latest);
        Assert.Equal(BackendKind.XInput, controller.Current.Backend);
        Assert.True(sources[^1].Stopped);
    }

    [Fact]
    public async Task DisconnectClearsAllPositions()
    {
        var clock = new Clock(); ManualSource? source = null;
        await using var controller = new GpssController(k => source = new(k, clock), clock);
        await controller.SelectAsync(Device());
        source!.Send(0, clock.Now, 0, 0);
        controller.Start();
        await Wait(() => controller.Current.Latest.Count == 4);
        source.Disconnect();
        await Wait(() => controller.Current.Status.State == ConnectionState.Disconnected);
        Assert.Empty(controller.Current.Latest);
        Assert.Null(controller.Current.Analysis?.LeftBits);
    }

    [Fact]
    public async Task RegistryReplacementUsesTheSameInputHistory()
    {
        var registry = EstimatorRegistry.CreateDefault();
        registry.Register("test", () => new TestEstimator());
        var clock = new Clock(); ManualSource? source = null;
        await using var controller = new GpssController(k => source = new(k, clock), clock, registry);
        await controller.SelectAsync(Device()); source!.Send(0, clock.Now, 0, 0);
        controller.Start();
        await Wait(() => controller.Current.Latest.Count == 4);
        controller.SetEstimator("test");
        await Wait(() => controller.Current.Analysis?.MethodId == "test");
        Assert.Equal(2, controller.Current.Analysis!.LeftBits);
        Assert.False(source.Stopped);
    }
    private sealed class TestEstimator : IStepEstimator
    {
        public string MethodId => "test";
        public StepEstimate Estimate(ReadOnlySpan<double> differences) => new("test", EstimateStatus.Valid, .5, 4, 2, NonzeroCount: 1);
    }

    [Fact]
    public void SuggestedMappingRejectsAmbiguityAndInvertsXInputVerticalAxes()
    {
        Assert.Equal(4, SuggestedMapping.Create(Device()).Bindings.Length);
        var mapping = SuggestedMapping.Create(Device(BackendKind.XInput));
        Assert.True(mapping.Bindings.Single(b => b.Axis == Axis.LY).Inverted);
        Assert.False(mapping.Bindings.Single(b => b.Axis == Axis.LX).Inverted);
        var duplicate = Device(BackendKind.HidDirect);
        duplicate = duplicate with { Axes = duplicate.Axes.Add(duplicate.Axes[0] with { Key = "duplicate" }) };
        Assert.Empty(SuggestedMapping.Create(duplicate).Bindings);
        var sixAxis = Device(BackendKind.HidDirect);
        sixAxis = sixAxis with { Axes = sixAxis.Axes.Add(new AxisDescriptor("Z", "Z", 0, 255, 8, 1, 0x32))
            .Add(new AxisDescriptor("Rz", "Rz", 0, 255, 8, 1, 0x35)) };
        Assert.Empty(SuggestedMapping.Create(sixAxis).Bindings);
        Assert.Throws<ArgumentException>(() => SuggestedMapping.Validate(Device(), AxisMapping.Empty));
    }

    [Fact]
    public async Task RealSimulationPipelineProducesEightAndTenBitAndStops()
    {
        await using var controller = new GpssController(_ => new SimulationInputSource());
        controller.Start();
        await controller.SelectAsync(SimulationInputSource.DemoDevice);
        await Wait(() => controller.Current.Analysis?.LeftBits is not null && controller.Current.Analysis?.RightBits is not null);
        Assert.Equal("8.0bit", StepPrecision.FormatBits(controller.Current.Analysis!.LeftBits));
        Assert.Equal("10.0bit", StepPrecision.FormatBits(controller.Current.Analysis.RightBits));
        Assert.True(controller.Current.Diagnostics.ApiCalls > 0);
        Assert.Equal(4, controller.Current.Latest.Count);
        await controller.StopAsync();
        Assert.Equal(ConnectionState.Stopped, controller.Current.Status.State);
    }

    [Fact]
    public void ObservationQueueEnforcesCountAndByteLimits()
    {
        var frame = new InputFrame(BackendKind.Simulation, 0, 0, 0, [], "test");
        var queue = new BoundedObservationBuffer(2, 256);
        Assert.True(queue.TryWrite(frame)); Assert.True(queue.TryWrite(frame));
        Assert.False(queue.TryWrite(frame)); Assert.Equal(2, queue.Count);
        Assert.True(queue.TryRead(out _)); Assert.True(queue.TryWrite(frame));
        queue.Clear(); Assert.Equal(0, queue.Count);
        Assert.False(new BoundedObservationBuffer(10, 127).TryWrite(frame));
    }
}
