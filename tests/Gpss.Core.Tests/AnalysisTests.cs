using System.Globalization;
using Gpss.Core;

namespace Gpss.Core.Tests;

public sealed class AnalysisTests
{
    [Fact]
    public void JitterUsesPopulationPositionStatistics()
    {
        var result = JitterCalculator.Calculate([-1, 0, 1]);
        Assert.Equal(3, result.Count);
        Assert.Equal(0, result.Mean);
        Assert.Equal(Math.Sqrt(2.0 / 3), result.PopulationStdDev!.Value, 12);
        Assert.Equal(1, result.MaxDeviation);
    }

    [Fact]
    public void EmptySingletonAndConstantJitter()
    {
        var empty = JitterCalculator.Calculate([]);
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Mean);
        Assert.Null(empty.PopulationStdDev);
        Assert.Null(empty.MaxDeviation);
        foreach (var values in new[] { new[] { .1 }, new[] { .1, .1, .1 } })
        {
            var result = JitterCalculator.Calculate(values);
            Assert.Equal(.1, result.Mean);
            Assert.Equal(0, result.PopulationStdDev);
            Assert.Equal(0, result.MaxDeviation);
        }
        Assert.Throws<ArgumentException>(() => JitterCalculator.Calculate([double.NaN]));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void ExactBinaryPrecisionAndStationaryDuplicates(int bits)
    {
        var step = 2.0 / (1 << bits);
        var deltas = Enumerable.Repeat(new[] { 0, step, 0, step, step }, 20).SelectMany(v => v).ToArray();
        var result = new ModalStepEstimator().Estimate(deltas);
        Assert.Equal(EstimateStatus.Valid, result.Status);
        Assert.Equal(step, result.Step);
        Assert.Equal(1 << bits, result.Levels);
        Assert.Equal(bits, result.Bits);
        Assert.Equal($"{bits}.0bit", StepPrecision.FormatBits(result.Bits));
    }

    [Fact]
    public void FractionalBitsAndHalfUpFormattingAreCultureIndependent()
    {
        var (levels, bits) = StepPrecision.Convert(.003);
        Assert.Equal(666.6666666666666, levels, 10);
        Assert.Equal("9.4bit", StepPrecision.FormatBits(bits));
        Assert.Equal(8.1m, StepPrecision.Round(8.05m));
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("8.1bit", StepPrecision.FormatBits(8.05));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        Assert.Equal("—", StepPrecision.FormatBits(null));
        Assert.Equal("—", StepPrecision.FormatBits(double.NaN));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-.1)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.Epsilon)]
    public void InvalidOrUnrepresentableStepFails(double step)
        => Assert.Throws<ArgumentOutOfRangeException>(() => StepPrecision.Convert(step));

    [Fact]
    public void ZeroInvalidAndBelowResolutionAreExcluded()
    {
        var result = new ModalStepEstimator().Estimate([0, 0, 1e-8, double.NaN, -.1, 2.1, .003]);
        Assert.Equal(.003, result.Step);
        Assert.Equal(1, result.NonzeroCount);
        Assert.Equal(1, result.ModalCount);
        Assert.Equal(6, result.ExcludedCount);
        Assert.Equal(EstimateStatus.Reference, result.Status);
        Assert.Contains("参考値", result.Message);
        var stationary = new ModalStepEstimator().Estimate([0, 0, 1e-8]);
        Assert.Equal(EstimateStatus.NoData, stationary.Status);
        Assert.Null(stationary.Bits);
        Assert.Contains("変化なし", stationary.Message);
    }

    [Fact]
    public void BucketBoundariesUseMeanOfOriginalValues()
    {
        var result = new ModalStepEstimator(new(Quantum: .01)).Estimate([.0149, .005, .0151]);
        Assert.Equal((.0149 + .005) / 2, result.Step!.Value, 12);
        Assert.Equal(2, result.ModalCount);
        Assert.Equal(3, result.NonzeroCount);
        Assert.False(result.Tie);
    }

    [Fact]
    public void TiesAndFloatingPointBucketsAreOrderIndependent()
    {
        var estimator = new ModalStepEstimator();
        var first = estimator.Estimate([.2, .1, .2, .1]);
        var second = estimator.Estimate([.1, .2, .1, .2]);
        Assert.Equal(first, second);
        Assert.True(first.Tie);
        Assert.Equal(.1, first.Step);
        Assert.Contains("同率", first.Message);
        var values = new[] { .003000001, .003000002, .003000003 };
        Assert.Equal(estimator.Estimate(values), estimator.Estimate(values.Reverse().ToArray()));
    }

    [Fact]
    public void RegistryAcceptsExtensionWithoutChangingAcquisitionOrUi()
    {
        var registry = EstimatorRegistry.CreateDefault();
        Assert.Equal(new[] { MeanBitStepEstimator.Id }, registry.Ids);
        registry.Register("test", () => new ConstantEstimator());
        Assert.Equal(2, registry.Create("test").Estimate([]).Bits);
        Assert.Throws<ArgumentException>(() => registry.Register("test", () => new ConstantEstimator()));
        Assert.Throws<KeyNotFoundException>(() => registry.Create("unknown"));
        registry.Register("wrong", () => new ConstantEstimator());
        Assert.Throws<InvalidOperationException>(() => registry.Create("wrong"));
    }

    private sealed class ConstantEstimator : IStepEstimator
    {
        public string MethodId => "test";
        public StepEstimate Estimate(ReadOnlySpan<double> differences) => new(MethodId, EstimateStatus.Valid, .5, 4, 2);
    }

    [Fact]
    public void ConfigurationValidationAndWindowExpiryChangeMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModalStepEstimator(new(Version: 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModalStepEstimator(new(Quantum: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModalStepEstimator(new(ReferenceCount: 0)));
        var window = new AxisWindow(Axis.LX, new TestClock(), durationSeconds: .010);
        var values = new[] { 0, .01, 0, .01, 0, .02, 0 };
        for (var i = 0; i < values.Length; i++) window.Append(new(0, i, i, i, values[i], 0));
        var estimator = new ModalStepEstimator();
        Assert.Equal(.01, estimator.Estimate(window.Snapshot(6).Differences.AsSpan()).Step);
        Assert.Equal(.02, estimator.Estimate(window.Snapshot(14).Differences.AsSpan()).Step);
        Assert.Null(estimator.Estimate(window.Snapshot(17).Differences.AsSpan()).Bits);
    }

    private sealed class TestClock : IMonotonicClock
    {
        public long Timestamp => 0;
        public long Frequency => 1000;
    }

    [Fact]
    public void NormalizationKeepsRawRangeAndDoesNotUseSeparateSignedScales()
    {
        Assert.Equal(-1, AxisNormalization.Normalize(0, 0, 255));
        Assert.Equal(1, AxisNormalization.Normalize(255, 0, 255));
        Assert.Equal(-1, AxisNormalization.Normalize(255, 0, 255, inverted: true));
        var zero = AxisNormalization.Normalize(0, -32768, 32767);
        Assert.NotEqual(0, zero); // No implicit recentering.
        Assert.Equal(2.0 / 65535, AxisNormalization.Normalize(1, -32768, 32767) - zero, 12);
        var step = AxisNormalization.Normalize(1, 0, 255) + 1;
        Assert.Equal(255, StepPrecision.Convert(step).Levels, 10);
        Assert.Equal("8.0bit", StepPrecision.FormatBits(StepPrecision.Convert(step).Bits));
        Assert.Equal(1, AxisNormalization.Normalize(uint.MaxValue, 0, uint.MaxValue));
        Assert.Equal(-1, AxisNormalization.Normalize(long.MaxValue - 1, long.MaxValue - 1, long.MaxValue));
        Assert.Equal(1, AxisNormalization.Normalize(long.MaxValue, long.MinValue, long.MaxValue));
        Assert.Equal(-1, AxisNormalization.Normalize(int.MinValue, int.MinValue, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => AxisNormalization.Normalize(256, 0, 255));
        Assert.Throws<ArgumentOutOfRangeException>(() => AxisNormalization.Normalize(0, 1, 1));
    }
}
