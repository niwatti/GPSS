using System.Collections.Immutable;
using Gpss.Application;
using Gpss.Core;
using Gpss.Input;
using Gpss.Input.Simulation;

namespace Gpss.Application.Tests;

public sealed class HistogramPipelineTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public async Task SelectedPeriodAppliesBeforeDeviceSelectionAndChangingItClearsEveryResult(int seconds)
    {
        var clock = new Clock(); Source? source = null;
        await using var controller = new GpssController(_ => source = new Source(clock), clock);
        Assert.Equal(30, controller.Current.MeasurementWindowSeconds);
        await controller.SetMeasurementWindowAsync(seconds);
        Assert.Equal(seconds, controller.Current.MeasurementWindowSeconds);
        controller.Start(); await controller.SelectAsync(SimulationInputSource.DemoDevice);
        await Until(() => source?.Status.State == ConnectionState.Connected);
        var origin = clock.Timestamp;
        source!.Send(0, 0, 0, 0, 0); clock.Now = origin + 1000; source.Send(1, .25, .25, .25, .25);
        await Until(() => controller.Current.StepHistogram?.TotalCount == 4);
        Assert.Equal(seconds, controller.Current.StepHistogram!.WindowSeconds);
        Assert.All(controller.Current.History!.Values, w => Assert.Equal(seconds, w.WindowSeconds));
        clock.Now = origin + seconds * 1000;
        await Until(() => controller.Current.StepHistogram?.Timestamp == clock.Timestamp);
        Assert.Equal(4, controller.Current.StepHistogram!.TotalCount);
        clock.Now += 100;
        await Until(() => controller.Current.StepHistogram?.TotalCount == 0);
        Assert.All(controller.Current.Analysis!.Axes.Values, a => Assert.Equal(1, a.Count));
        var epoch = controller.Current.Status.Epoch;
        await controller.SetMeasurementWindowAsync(seconds);
        Assert.Equal(epoch, controller.Current.Status.Epoch);
        await controller.SetMeasurementWindowAsync(seconds == 300 ? 10 : 300);
        Assert.True(controller.Current.Status.Epoch > epoch);
        Assert.Empty(controller.Current.Latest);
        Assert.True(controller.Current.StepHistogram is null || controller.Current.StepHistogram.TotalCount == 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetMeasurementWindowAsync(20));
    }

    private sealed class Clock : IMonotonicClock
    { public long Now = 1_000_000; public long Timestamp => Interlocked.Read(ref Now); public long Frequency => 1000; }
    private sealed class Source(Clock clock) : InputSourceBase(clock)
    {
        public override BackendKind Kind => BackendKind.Simulation;
        public override Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeviceInfo>>([SimulationInputSource.DemoDevice]);
        protected override async Task CaptureAsync(InputSession session, CancellationToken cancellationToken)
        {
            SetStatus(session.Epoch, ConnectionState.Connected, session.Device);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        public void Send(long sequence, params double[] values)
        {
            PublishFrame(new(Kind, Status.Epoch, sequence, clock.Timestamp,
                Enum.GetValues<Axis>().Select((axis, i) => new AxisReading(axis, sequence, values[i], 0)).ToImmutableArray(), "histogram-probe"));
        }
        public void Disconnect() => SetStatus(Status.Epoch, ConnectionState.Disconnected, Status.Device);
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { Assert.True(DateTime.UtcNow < deadline, "Histogram state was not reached"); await Task.Delay(5); }
    }

    [Fact]
    public async Task FourAxesExpireTogetherWithoutDependingOnDisplayPollingOrZeroDeltaFrames()
    {
        var clock = new Clock(); Source? source = null;
        await using var controller = new GpssController(_ => source = new Source(clock), clock);
        controller.Start(); await controller.SelectAsync(SimulationInputSource.DemoDevice);
        await Until(() => source?.Status.State == ConnectionState.Connected);
        var origin = clock.Timestamp;
        source!.Send(0, 0, 0, 0, 0);
        clock.Now = origin + 1000;
        source.Send(1, 1d / 128, -1d / 256, 1d / 512, -1d / 1024);
        await Until(() => controller.Current.StepHistogram?.TotalCount == 4);
        var saved = controller.Current.StepHistogram!;
        Assert.Equal(new[] { 8, 9, 10, 11 }, saved.Bins.Select(b => b.LowerBit));
        Assert.All(saved.Bins, b => Assert.Equal(1, b.Count));
        Assert.Equal(30, saved.WindowSeconds);
        for (var i = 0; i < 30; i++) Assert.Equal(4, controller.GetDisplaySnapshot().StepHistogram!.TotalCount);
        clock.Now = origin + 2000;
        source.Send(2, 1d / 128, -1d / 256, 1d / 512, -1d / 1024);
        await Until(() => controller.Current.StepHistogram?.Timestamp == clock.Timestamp);
        Assert.Equal(4, controller.Current.StepHistogram!.TotalCount);
        // The controller publishes analysis at 10Hz; keep 100ms between checks.
        clock.Now = origin + 29901;
        await Until(() => controller.Current.StepHistogram?.Timestamp == clock.Timestamp);
        Assert.Equal(4, controller.Current.StepHistogram!.TotalCount);
        clock.Now = origin + 30001;
        await Until(() => controller.Current.StepHistogram?.TotalCount == 0);
        Assert.Equal(4, saved.TotalCount);
        Assert.All(controller.Current.Analysis!.Axes.Values, a => Assert.Equal(2, a.Count));
    }

    [Fact]
    public async Task ResetModeChangeAndDisconnectNeverShowThePreviousDistribution()
    {
        var clock = new Clock(); Source? source = null;
        await using var controller = new GpssController(_ => source = new Source(clock), clock);
        controller.Start(); await controller.SelectAsync(SimulationInputSource.DemoDevice);
        await Until(() => source?.Status.State == ConnectionState.Connected);
        source!.Send(0, 0, 0, 0, 0); clock.Now += 1000; source.Send(1, .125, .125, .125, .125);
        await Until(() => controller.Current.StepHistogram?.TotalCount == 4);
        var oldEpoch = controller.Current.Status.Epoch;
        await controller.ResetAsync();
        Assert.Null(controller.GetDisplaySnapshot().StepHistogram);
        await Until(() => source!.Status.State == ConnectionState.Connected);
        source!.Send(0, 0, 0, 0, 0); clock.Now += 1000; source.Send(1, .25, 0, 0, 0);
        await Until(() => controller.Current.StepHistogram?.TotalCount == 1);
        Assert.True(controller.Current.StepHistogram!.Epoch > oldEpoch);
        Assert.Equal(new StepHistogramBin(3, 1), Assert.Single(controller.Current.StepHistogram.Bins));
        await controller.SetModeAsync(new(AcquisitionMode.LowLatency));
        Assert.Null(controller.GetDisplaySnapshot().StepHistogram);
        await Until(() => source!.Status.State == ConnectionState.Connected);
        source!.Disconnect(); await Until(() => controller.Current.Status.State == ConnectionState.Disconnected);
        Assert.Null(controller.GetDisplaySnapshot().StepHistogram);
    }
}
