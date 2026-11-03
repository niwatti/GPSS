using Gpss.Core;

namespace Gpss.Core.Tests;

public sealed class AxisWindowTests
{
    private sealed class Clock(long frequency = 1000) : IMonotonicClock
    {
        public long Timestamp => 0;
        public long Frequency => frequency;
    }
    private static AxisSample Sample(long sequence, long time, double value = 0, long epoch = 0, long continuity = 0)
        => new(epoch, sequence, time, 0, value, continuity);

    [Fact]
    public void ThirtySecondBoundaryIsInclusiveAndCurrentClockExpiresWithoutInput()
    {
        var window = new AxisWindow(Axis.LX, new Clock());
        foreach (var (seq, time, value) in new[] { (0, 0, .0), (1, 1, .1), (2, 29999, .2), (3, 30000, .3) })
            Assert.True(window.Append(Sample(seq, time, value)));
        var snapshot = window.Snapshot(30000);
        Assert.Equal(4, snapshot.Samples.Length);
        Assert.Equal(30, snapshot.RetainedSeconds);
        Assert.Equal(3, snapshot.Differences.Length); // No automatic 100ms discontinuity.
        Assert.Equal(3, window.Snapshot(30001).Samples.Length);
        Assert.Equal(2, window.Snapshot(30001).Differences.Length);
        Assert.Empty(window.Snapshot(60001).Samples);
    }

    [Fact]
    public void DifferenceNeedsBothEndpointsInsideWindow()
    {
        var window = new AxisWindow(Axis.LX, new Clock(), durationSeconds: .010);
        window.Append(Sample(0, 0));
        window.Append(Sample(1, 1, .1));
        window.Append(Sample(2, 2, .3));
        Assert.Single(window.Snapshot(11).Differences);
        Assert.Equal(.2, window.Snapshot(11).Differences[0], 12);
        Assert.Empty(window.Snapshot(12).Differences);
    }

    [Fact]
    public void EqualTicksAndMissingOtherAxisReportsDoNotBreakDifferences()
    {
        var window = new AxisWindow(Axis.LY, new Clock());
        Assert.True(window.Append(Sample(0, 100, .1)));
        Assert.True(window.Append(Sample(5, 100, .2))); // Other reports need not contain LY.
        Assert.True(window.Append(Sample(9, 1000, .4)));
        var snapshot = window.Snapshot(1000);
        Assert.Equal(3, snapshot.Samples.Length);
        Assert.Equal(new[] { .1, .2 }, snapshot.Differences.Select(v => Math.Round(v, 8)));
        Assert.Equal(0, snapshot.DroppedCount);
    }

    [Fact]
    public void ExplicitContinuityBoundaryCutsOnlyItsEdge()
    {
        var window = new AxisWindow(Axis.LX, new Clock());
        window.Append(Sample(0, 0));
        window.Append(Sample(1, 1, .2, continuity: 1));
        window.Append(Sample(2, 2, .3, continuity: 1));
        var snapshot = window.Snapshot(2);
        Assert.Single(snapshot.Differences);
        Assert.Equal(.1, snapshot.Differences[0], 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1.01)]
    [InlineData(-1.01)]
    public void InvalidValueIsRejectedAndCutsNextEdge(double invalid)
    {
        var window = new AxisWindow(Axis.LX, new Clock());
        window.Append(Sample(0, 0));
        Assert.False(window.Append(Sample(1, 1, invalid)));
        window.Append(Sample(2, 2, .2));
        window.Append(Sample(3, 3, .3));
        var snapshot = window.Snapshot(3);
        Assert.Equal(3, snapshot.Samples.Length);
        Assert.Single(snapshot.Differences);
        Assert.Equal(1, snapshot.DroppedCount);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(0, 200)]
    [InlineData(2, 99)]
    public void DuplicateSequenceOrBackwardsTimeRejectsAndBreaks(long badSequence, long badTime)
    {
        var window = new AxisWindow(Axis.LX, new Clock());
        window.Append(Sample(1, 100));
        Assert.False(window.Append(Sample(badSequence, badTime, .5)));
        Assert.True(window.Append(Sample(3, 300, .6)));
        Assert.Empty(window.Snapshot(300).Differences);
    }

    [Fact]
    public void WrongEpochCannotChangeCurrentWindowAndResetClearsEverything()
    {
        var window = new AxisWindow(Axis.RX, new Clock(), epoch: 5);
        window.Append(Sample(10, 100, .2, epoch: 5));
        Assert.False(window.Append(Sample(11, 101, .3, epoch: 4)));
        Assert.Single(window.Snapshot(101).Samples);
        Assert.Equal(0, window.Snapshot(101).DroppedCount);
        window.Reset(6);
        Assert.Empty(window.Snapshot(101).Samples);
        Assert.True(window.Append(Sample(0, 101, .4, epoch: 6)));
        Assert.Empty(window.Snapshot(101).Differences);
    }

    [Fact]
    public void CapacityWarnsOnlyWhileLostValueWouldStillBelongToWindow()
    {
        var window = new AxisWindow(Axis.LX, new Clock(), capacity: 2, durationSeconds: .005);
        for (var i = 0; i < 3; i++) window.Append(Sample(i, i, i / 10.0));
        var snapshot = window.Snapshot(2);
        Assert.Equal(2, snapshot.Samples.Length);
        Assert.True(snapshot.Truncated);
        Assert.Equal(1, snapshot.DroppedCount);
        Assert.Equal(.001, snapshot.RetainedSeconds);
        Assert.False(window.Snapshot(6).Truncated);
    }

    [Fact]
    public void SnapshotOmitsFutureSamplesAndAxesAreIndependent()
    {
        var left = new AxisWindow(Axis.LX, new Clock());
        var right = new AxisWindow(Axis.RX, new Clock());
        left.Append(Sample(0, 10, .5));
        left.Append(Sample(1, 20, .7));
        right.Append(Sample(1, 20, -.2));
        Assert.Single(left.Snapshot(10).Samples);
        Assert.Equal(2, left.Snapshot(20).Samples.Length);
        Assert.Single(right.Snapshot(20).Samples);
        Assert.Empty(right.Snapshot(20).Differences);
    }

    [Fact]
    public void ConfigurationRequiresPositiveLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisWindow(Axis.LX, new Clock(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisWindow(Axis.LX, new Clock(), capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisWindow(Axis.LX, new Clock(), durationSeconds: double.NaN));
    }
}
