using Gpss.Core;

namespace Gpss.Core.Tests;

public sealed class MeanBitStepEstimatorTests
{
    [Fact]
    public void AveragesUnroundedBitsRatherThanDeltasOrDisplayedBins()
    {
        var estimator = new MeanBitStepEstimator();
        var result = estimator.Estimate([2d / 256, 2d / 1024]);
        Assert.Equal(9, result.Bits);
        Assert.Equal(512, result.Levels);
        Assert.Equal(2d / 512, result.Step);
        Assert.Equal(2, result.NonzeroCount);
        Assert.Equal(EstimateStatus.Reference, result.Status);
        var unrounded = estimator.Estimate([2 / Math.Pow(2, 8.049), 2 / Math.Pow(2, 8.149)]);
        Assert.Equal(8.099, unrounded.Bits!.Value, 12);
        Assert.Equal("8.1bit", StepPrecision.FormatBits(unrounded.Bits));
    }

    [Fact]
    public void IgnoresOnlyZeroInvalidAndUnrepresentableDifferences()
    {
        var result = new MeanBitStepEstimator().Estimate([0, double.NaN, double.PositiveInfinity, -.1, 2.1, double.Epsilon, Math.ScaleB(2, -32)]);
        Assert.Equal(32, result.Bits);
        Assert.Equal(1, result.NonzeroCount);
        Assert.Equal(6, result.ExcludedCount);
        Assert.Null(new MeanBitStepEstimator().Estimate([0, double.NaN]).Bits);
        Assert.Equal(0, new MeanBitStepEstimator().Estimate([2]).Bits);
    }

    [Fact]
    public void ThirtyValidStepsRemoveTheReferenceNotice()
    {
        var result = new MeanBitStepEstimator().Estimate(Enumerable.Repeat(2d / 255, 30).ToArray());
        Assert.Equal(EstimateStatus.Valid, result.Status);
        Assert.Empty(result.Message);
        Assert.Equal("8.0bit", StepPrecision.FormatBits(result.Bits));
    }

    [Fact]
    public void IncrementalAddRemoveMatchesAnIndependentRollingMean()
    {
        var estimator = new MeanBitStepEstimator(); var session = estimator.CreateSession();
        var retained = new Queue<double>(); var random = new Random(608);
        for (var i = 0; i < 20_000; i++)
        {
            var delta = i % 31 == 0 ? 0 : 2 / Math.Pow(2, random.NextDouble() * 32);
            session.Add(delta); retained.Enqueue(delta);
            if (retained.Count > 101) session.Remove(retained.Dequeue());
            if (i % 137 != 0) continue;
            var expected = retained.Where(d => d > 0).Select(d => Math.Log2(2 / d)).ToArray();
            var result = session.Estimate();
            Assert.Equal(expected.Length, result.NonzeroCount);
            Assert.Equal(retained.Count - expected.Length, result.ExcludedCount);
            if (expected.Length > 0) Assert.Equal(expected.Average(), result.Bits!.Value, 11);
            else Assert.Null(result.Bits);
        }
        foreach (var delta in retained) session.Remove(delta);
        Assert.Null(session.Estimate().Bits); Assert.Equal(0, session.Estimate().ExcludedCount);
        session.Add(2d / 1024); Assert.Equal(10, session.Estimate().Bits);
    }

    [Fact]
    public void StickMeanUsesTheNumberOfValidStepsAndCanUseOnlyOneAxis()
    {
        var x = new StepEstimate(MeanBitStepEstimator.Id, Bits: 8, NonzeroCount: 1);
        var y = new StepEstimate(MeanBitStepEstimator.Id, Bits: 10, NonzeroCount: 3);
        Assert.Equal(9.5, StepPrecision.MeanBits(x, y));
        Assert.Equal(8, StepPrecision.MeanBits(x, null));
        Assert.Equal(10, StepPrecision.MeanBits(x with { NonzeroCount = 0 }, y));
        Assert.Null(StepPrecision.MeanBits(null, null));
    }

    private sealed class Clock : IMonotonicClock
    { public long Timestamp => 0; public long Frequency => 1000; }

    [Theory]
    [InlineData(17, 30)]
    [InlineData(4096, .2)]
    public void WindowMatchesRawReferenceAcrossExpiryGapsAndCapacity(int capacity, double seconds)
    {
        var clock = new Clock(); var estimator = new MeanBitStepEstimator();
        var reference = new AxisWindow(Axis.LX, clock, capacity: capacity, durationSeconds: seconds);
        var live = new LiveAxisWindow(Axis.LX, clock, capacity: capacity, durationSeconds: seconds);
        for (var i = 0; i < 4000; i++)
        {
            var value = i % 117 == 0 ? double.NaN : (i % 251) / 256d - .5;
            var sample = new AxisSample(0, i, i, i, value, i / 19);
            Assert.Equal(reference.Append(sample), live.Append(sample));
            if (i % 13 != 0) continue;
            var deltas = reference.Snapshot(i).Differences.Where(d => d > 0).ToArray();
            var result = live.Analyze(i, estimator).Analysis.Step;
            Assert.Equal(deltas.Length, result.NonzeroCount);
            if (deltas.Length > 0) Assert.Equal(deltas.Average(d => Math.Log2(2 / d)), result.Bits!.Value, 10);
            else Assert.Null(result.Bits);
        }
        Assert.Null(live.Analyze(34000, estimator).Analysis.Step.Bits);
        live.Reset(1);
        Assert.Null(live.Analyze(34000, estimator).Analysis.Step.Bits);
    }
}
