using System.Collections.Immutable;
using Gpss.Core;

namespace Gpss.Core.Tests;

public sealed class HistoryProjectionTests
{
    [Fact]
    public void PixelProjectionRetainsExtremaEndpointsAndKnownGaps()
    {
        var points=Enumerable.Range(0,1000).Select(i=>new AxisSample(1,i,i,0,
            i==15?-1:i==16?1:0,i<500?0:1)).ToImmutableArray();
        var window=new AxisWindowSnapshot(1,Axis.LX,1000,100,points,[],9.99,0,false);
        var projected=HistoryProjection.Reduce(window,1);
        Assert.Contains(projected.Samples,s=>s.Value==-1);
        Assert.Contains(projected.Samples,s=>s.Value==1);
        Assert.Equal(0,projected.Samples[0].Sequence);
        Assert.Equal(999,projected.Samples[^1].Sequence);
        Assert.Contains(projected.Samples,s=>s.Sequence==499);
        Assert.Contains(projected.Samples,s=>s.Sequence==500);
        Assert.Equal(1000,window.Samples.Length);
        Assert.True(projected.Samples.Length<20);
    }

    [Fact]
    public void ExcessiveContinuityChangesAreBoundedAndWarned()
    {
        var points=Enumerable.Range(0,100).Select(i=>new AxisSample(0,i,i,0,0,i)).ToImmutableArray();
        var projected=HistoryProjection.Reduce(new(0,Axis.LX,100,10,points,[],9.9,0,false),1,16);
        Assert.InRange(projected.Samples.Length,2,16);
        Assert.True(projected.ProjectionTruncated);
        Assert.False(projected.Truncated);
        Assert.Equal(0,projected.Samples[0].Sequence);
        Assert.Equal(99,projected.Samples[^1].Sequence);
    }

    [Fact]
    public void RejectedValueCreatesAnExplicitGraphBoundary()
    {
        var clock=new TestClock();var window=new AxisWindow(Axis.LX,clock);
        window.Append(new(0,0,0,0,0,0));
        window.Append(new(0,1,1,0,double.NaN,0));
        window.Append(new(0,2,2,0,.5,0));
        var snapshot=window.Snapshot(2);
        Assert.NotEqual(snapshot.Samples[0].ContinuityId,snapshot.Samples[1].ContinuityId);
        Assert.Empty(snapshot.Differences);
    }

    [Fact]
    public void OverflowPreservesOldestAndNewestPointsInsteadOfOnlyTheTail()
    {
        var points = Enumerable.Range(0, 100).Select(i => new AxisSample(1, i, i, 0,
            i == 20 ? -1 : i == 70 ? 1 : 0, i)).ToImmutableArray();
        var projected = HistoryProjection.Reduce(new(1, Axis.LX, 100, 10, points, [], 9.9, 0, false), 1, 16);
        Assert.Equal(0, projected.Samples[0].Sequence);
        Assert.Equal(99, projected.Samples[^1].Sequence);
        Assert.Contains(projected.Samples, p => p.Value == -1);
        Assert.Contains(projected.Samples, p => p.Value == 1);
        Assert.True(projected.ProjectionTruncated);
        Assert.False(projected.Truncated);
    }

    [Fact]
    public void OmittedRunsDoNotReconnectReusedContinuityIds()
    {
        var points = Enumerable.Range(0, 19).Select(i => new AxisSample(1, i, i, 0, 0, i % 2 == 0 ? 7 : 8)).ToImmutableArray();
        var projected = HistoryProjection.Reduce(new(1, Axis.LX, 20, 1, points, [], 18, 0, false), 1, 4);
        Assert.Equal(0, projected.Samples[0].Sequence);
        Assert.Equal(18, projected.Samples[^1].Sequence);
        Assert.NotEqual(projected.Samples[0].ContinuityId, projected.Samples[^1].ContinuityId);
    }
    private sealed class TestClock:IMonotonicClock {public long Timestamp=>0;public long Frequency=>1000;}
}
