using System.Collections.Immutable;
using Gpss.Core;

namespace Gpss.Core.Tests;

public sealed class MeasurementWindowTests
{
    private sealed class Clock : IMonotonicClock
    { public long Timestamp => 0; public long Frequency => 1000; }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public void StatisticsMeanHistogramAndProjectionShareTheSelectedWindow(int seconds)
    {
        var live = new LiveAxisWindow(Axis.LX, new Clock(), capacity: MeasurementWindow.SampleCapacity(seconds), durationSeconds: seconds);
        var estimator = new MeanBitStepEstimator();
        for (var i = 0; i <= seconds; i++) live.Append(new(0, i, i * 1000, i, i % 2 / 256d, 0));
        var exact = live.Analyze(seconds * 1000, estimator);
        Assert.Equal(seconds + 1, exact.Analysis.Count);
        Assert.Equal(9, exact.Analysis.Step.Bits);
        Assert.Equal(new StepHistogramBin(9, seconds), Assert.Single(exact.StepHistogram));
        Assert.Equal(seconds, exact.History.WindowSeconds);
        Assert.Equal(0, exact.History.Samples[0].Timestamp);
        Assert.Equal(seconds, exact.History.RetainedSeconds);
        var expired = live.Analyze(seconds * 1000 + 1, estimator);
        Assert.Equal(seconds, expired.Analysis.Count);
        Assert.Equal(new StepHistogramBin(9, seconds - 1), Assert.Single(expired.StepHistogram));
        Assert.Equal(1000, expired.History.Samples[0].Timestamp);
        Assert.Equal(9, expired.Analysis.Step.Bits);
        Assert.Empty(live.Analyze(seconds * 2000 + 1, estimator).History.Samples);
    }

    [Fact]
    public void FiveMinuteWindowDoesNotAllocateItsMaximumRawStorageOnSelection()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var live = new LiveAxisWindow(Axis.LX, new Clock(), capacity: MeasurementWindow.SampleCapacity(300), durationSeconds: 300);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 8 * 1024 * 1024, $"Empty axis allocated {bytes} bytes");
        Assert.Equal(0, live.AllocatedSampleCapacity);
        live.Append(new(0, 0, 0, 0, 0, 0));
        Assert.Equal(1, live.Analyze(0, new MeanBitStepEstimator()).Analysis.Count);
    }

    [Fact]
    public void SparseLongRunningFiveMinuteWindowReleasesExpiredRawPages()
    {
        var live = new LiveAxisWindow(Axis.LX, new Clock(), capacity: MeasurementWindow.SampleCapacity(300), durationSeconds: 300);
        for (var i = 0; i < 50000; i++) live.Append(new(0, i, i * 1000L, i, i % 2 / 256d, 0));
        Assert.Equal(301, live.Analyze(49999000, new MeanBitStepEstimator()).Analysis.Count);
        Assert.True(live.AllocatedSampleCapacity < 32768, $"Sparse window retained {live.AllocatedSampleCapacity} raw slots");
    }

    [Fact]
    public void PagedRingWrapsThroughAPartialFinalPageWithoutChangingResults()
    {
        const int capacity = 8195;
        var clock = new Clock(); var estimator = new MeanBitStepEstimator();
        var live = new LiveAxisWindow(Axis.LX, clock, capacity: capacity);
        var reference = new AxisWindow(Axis.LX, clock, capacity: capacity);
        for (var i = 0; i < 20000; i++)
        {
            var sample = new AxisSample(0, i, i, i, i % 251 / 256d - .5, i / 101);
            live.Append(sample); reference.Append(sample);
            if (i % 997 != 0) continue;
            var expected = reference.Snapshot(i); var actual = live.Analyze(i, estimator);
            Assert.Equal(expected.Samples.Length, actual.Analysis.Count);
            Assert.Equal(expected.Truncated, actual.Analysis.Truncated);
            var bits = estimator.Estimate(expected.Differences.AsSpan()).Bits;
            if (bits is null) Assert.Null(actual.Analysis.Step.Bits);
            else Assert.Equal(bits.Value, actual.Analysis.Step.Bits!.Value, 10);
        }
        live.Reset(1); live.Append(new(1, 0, 20001, 0, .5, 0));
        var reset = live.Analyze(20001, estimator);
        Assert.Equal(1, reset.Analysis.Count); Assert.Empty(reset.StepHistogram);
    }

    [Fact]
    public void ReferenceProjectionRetainsFiveMinuteEndpointsUnderADenseBudget()
    {
        var samples = Enumerable.Range(0, 30001).Select(i => new AxisSample(0, i, i * 10L, i, i % 251 / 128d - 1, i)).ToImmutableArray();
        var window = new AxisWindowSnapshot(0, Axis.LX, 300000, 1000, samples, [], 300, 0, false, WindowSeconds: 300);
        var result = HistoryProjection.Reduce(window, 1024, 8192);
        Assert.Equal(0, result.Samples[0].Timestamp);
        Assert.Equal(300000, result.Samples[^1].Timestamp);
        Assert.Equal(300, result.RetainedSeconds); Assert.True(result.Samples.Length <= 8192);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(301)]
    public void UnsupportedPeriodsAreRejected(int seconds) => Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementWindow.Validate(seconds));
}
