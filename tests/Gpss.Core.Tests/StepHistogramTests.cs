using Gpss.Core;

namespace Gpss.Core.Tests;

public sealed class StepHistogramTests
{
    private sealed class Clock : IMonotonicClock
    { public long Timestamp => 0; public long Frequency => 1000; }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7.994353436858858, 8)]
    [InlineData(8, 8)]
    [InlineData(8.9, 8)]
    [InlineData(8.949999, 8)]
    [InlineData(8.950001, 9)]
    [InlineData(9, 9)]
    [InlineData(15.999977986052736, 16)]
    [InlineData(32, 32)]
    public void BinsUseTheDisplayedOneDecimalBitValue(double bits, int expectedBin)
    {
        var counts = new StepHistogramCounts();
        var delta = 2 / Math.Pow(2, bits);
        counts.AddDifference(delta);
        Assert.Equal(new StepHistogramBin(expectedBin, 1), Assert.Single(counts.Snapshot()));
        counts.RemoveDifference(delta);
        Assert.Empty(counts.Snapshot()); Assert.Equal(0, counts.TotalCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-.1)]
    [InlineData(2.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.Epsilon)]
    public void InvalidOrUnrepresentableStepsDoNotBecomeData(double delta)
    {
        var counts = new StepHistogramCounts();
        counts.AddDifference(delta); counts.RemoveDifference(delta);
        Assert.Equal(0, counts.TotalCount); Assert.Empty(counts.Snapshot());
    }

    [Fact]
    public void NormalizedEightBitLevelsAndAllAxesAreCountedWithoutMutatingSnapshots()
    {
        var counts = new StepHistogramCounts();
        counts.AddDifference(2d / 255); counts.AddDifference(2d / 255);
        var saved = counts.Snapshot();
        counts.AddBins([new(8, 3), new(9, 4)]);
        Assert.Equal(9, counts.TotalCount);
        Assert.Equal(new[] { new StepHistogramBin(8, 5), new(9, 4) }, counts.Snapshot().ToArray());
        counts.Clear();
        Assert.Empty(counts.Snapshot()); Assert.Equal(0, counts.TotalCount);
        Assert.Equal(new StepHistogramBin(8, 2), Assert.Single(saved));
    }

    [Fact]
    public void EdgeDisappearsWhenEitherEndpointLeavesTheWindow()
    {
        var window = new LiveAxisWindow(Axis.LX, new Clock(), epoch: 4);
        var estimator = new ModalStepEstimator();
        window.Append(new(4, 0, 0, 0, 0, 0));
        window.Append(new(4, 1, 1000, 1, 2d / 256, 0));
        Assert.Equal(new StepHistogramBin(8, 1), Assert.Single(window.Analyze(30000, estimator).StepHistogram));
        var expired = window.Analyze(30001, estimator);
        Assert.Equal(1, expired.Analysis.Count);
        Assert.Empty(expired.StepHistogram);
        Assert.Empty(window.Analyze(31001, estimator).StepHistogram);
    }

    [Fact]
    public void GapsInvalidValuesEpochAndPastClocksKeepOnlyEligibleEdges()
    {
        var window = new LiveAxisWindow(Axis.LX, new Clock(), epoch: 6);
        var estimator = new ModalStepEstimator();
        window.Append(new(6, 0, 0, 0, 0, 0));
        window.Append(new(6, 1, 1, 0, .125, 0)); // 4bit
        window.Append(new(6, 2, 2, 0, .25, 1)); // Known gap
        window.Append(new(6, 3, 3, 0, double.NaN, 1));
        window.Append(new(6, 4, 4, 0, .5, 1)); // Invalid preceding point
        window.Append(new(6, 5, 5, 0, .75, 1)); // 3bit
        var past = window.Analyze(1, estimator);
        Assert.Equal(new StepHistogramBin(4, 1), Assert.Single(past.StepHistogram));
        var full = window.Analyze(5, estimator).StepHistogram;
        Assert.Equal(new[] { new StepHistogramBin(3, 1), new(4, 1) }, full.ToArray());
        window.Reset(7);
        Assert.False(window.Append(new(6, 6, 6, 0, 0, 0)));
        Assert.Empty(window.Analyze(6, estimator).StepHistogram);
        Assert.Equal(2, full.Sum(b => b.Count));
    }

    [Fact]
    public void HistogramIncludesFineStepsEvenBelowTheModalQuantum()
    {
        var window = new LiveAxisWindow(Axis.RY, new Clock());
        window.Append(new(0, 0, 0, 0, 0, 0));
        window.Append(new(0, 1, 1, 0, Math.ScaleB(2, -32), 0));
        var result = window.Analyze(1, new ModalStepEstimator());
        Assert.Null(result.Analysis.Step.Bits);
        Assert.Equal(new StepHistogramBin(32, 1), Assert.Single(result.StepHistogram));
    }

    [Theory]
    [InlineData(17, 30)]
    [InlineData(4096, .2)]
    public void IncrementalCountsMatchAnIndependentReferenceThroughRollingAndCapacityLimits(int capacity, double seconds)
    {
        var clock = new Clock();
        var reference = new AxisWindow(Axis.LX, clock, epoch: 9, capacity: capacity, durationSeconds: seconds);
        var live = new LiveAxisWindow(Axis.LX, clock, epoch: 9, capacity: capacity, durationSeconds: seconds);
        var estimator = new ModalStepEstimator();
        long now = 0;
        for (var i = 0; i < 4000; i++)
        {
            now += i % 3;
            var value = i % 117 == 0 ? double.NaN : i % 5 == 0 ? 0 : (i % 251) / 256d - .5;
            var sample = new AxisSample(9, i, now, i, value, i / 19 % 2);
            Assert.Equal(reference.Append(sample), live.Append(sample));
            if (i % 13 != 0) continue;
            foreach (var at in new[] { now, Math.Max(0, now - 5) })
            {
                var expected = reference.Snapshot(at).Differences.Where(d => d > 0)
                    .GroupBy(d => (int)Math.Floor(Math.Round(Math.Log2(2 / d), 1, MidpointRounding.AwayFromZero)))
                    .OrderBy(g => g.Key).Select(g => new StepHistogramBin(g.Key, g.LongCount())).ToArray();
                Assert.Equal(expected, live.Analyze(at, estimator).StepHistogram.ToArray());
            }
        }
        Assert.Empty(live.Analyze(now + 31000, estimator).StepHistogram);
    }
}
