using System.Collections.Immutable;
using Gpss.Application;
using Gpss.Core;
using Gpss.Input;
using Gpss.Input.Simulation;

namespace Gpss.Application.Tests;

public sealed class DisplayPipelineTests
{
    private sealed class ProbeSource : InputSourceBase
    {
        public override BackendKind Kind => BackendKind.Simulation;
        public override Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken=default)
            => Task.FromResult<IReadOnlyList<DeviceInfo>>([SimulationInputSource.DemoDevice]);
        protected override async Task CaptureAsync(InputSession session,CancellationToken cancellationToken)
        {
            SetStatus(session.Epoch,ConnectionState.Connected,session.Device);
            await Task.Delay(Timeout.Infinite,cancellationToken);
        }
        public void Send(long sequence,double value)
        {
            var now=StopwatchClock.Instance.Timestamp;
            PublishFrame(new(Kind,Status.Epoch,sequence,now,[new(Axis.LX,sequence,value,0)],"probe"));
        }
    }
    private sealed class BlockingEstimator : IStepEstimator
    {
        public readonly ManualResetEventSlim Entered=new(),Release=new();
        public string MethodId=>"blocked";
        public StepEstimate Estimate(ReadOnlySpan<double> differences)
        {
            Entered.Set();
            if(!Release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Test did not release estimator");
            return new(MethodId,EstimateStatus.Valid,.5,4,2);
        }
    }
    private sealed class ReplacementEstimator : IStepEstimator
    {
        public string MethodId=>"replacement";
        public StepEstimate Estimate(ReadOnlySpan<double> differences)=>new(MethodId,EstimateStatus.Valid,.25,8,3);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(!predicate()) { Assert.True(DateTime.UtcNow<deadline,"Expected pipeline state was not reached");await Task.Delay(5); }
    }
    [Fact]
    public async Task LatestPositionRemainsReadableWhileAnalysisIsBlockedAndClearsOnSelectionChange()
    {
        var blocking=new BlockingEstimator();var registry=EstimatorRegistry.CreateDefault();registry.Register(blocking.MethodId,()=>blocking);
        ProbeSource? source=null;
        await using var controller=new GpssController(_=>source=new ProbeSource(),registry:registry);
        try
        {
            controller.Start();await controller.SelectAsync(SimulationInputSource.DemoDevice);
            await Until(()=>source!.Status.State==ConnectionState.Connected);
            source!.Send(0,.1);await Until(()=>controller.Current.Latest.ContainsKey(Axis.LX));
            controller.SetEstimator(blocking.MethodId);
            Assert.True(await Task.Run(()=>blocking.Entered.Wait(TimeSpan.FromSeconds(3))));
            source.Send(1,.75);
            var display=controller.GetDisplaySnapshot();
            Assert.Equal(.75,display.Latest[Axis.LX].Value);
            Assert.Equal(.1,controller.Current.Latest[Axis.LX].Value); // Analysis worker is still blocked.
            Assert.Null(display.Analysis); // New method never displays its predecessor's result.
            blocking.Release.Set();
            await Until(()=>controller.Current.Analysis?.MethodId==blocking.MethodId);
            Assert.NotNull(controller.Current.Performance);
            Assert.True(controller.Current.AnalysisRevision>0);
            Assert.Equal(controller.Current.AnalysisRevision,controller.Current.HistoryRevision);
            await controller.ClearSelectionAsync(BackendKind.XInput);
            Assert.Empty(controller.GetDisplaySnapshot().Latest);
            Assert.Null(controller.GetDisplaySnapshot().Analysis);
        }
        finally { blocking.Release.Set(); }
    }
    [Fact]
    public async Task ReplacedEstimatorCannotPublishAfterTheNewMethodWasChosen()
    {
        var blocking=new BlockingEstimator();var registry=EstimatorRegistry.CreateDefault();
        registry.Register(blocking.MethodId,()=>blocking);registry.Register("replacement",()=>new ReplacementEstimator());
        ProbeSource? source=null;
        await using var controller=new GpssController(_=>source=new ProbeSource(),registry:registry);
        try
        {
            controller.Start();await controller.SelectAsync(SimulationInputSource.DemoDevice);
            await Until(()=>source!.Status.State==ConnectionState.Connected);
            source!.Send(0,.2);await Until(()=>controller.Current.Latest.Count==1);
            controller.SetEstimator("blocked");
            Assert.True(await Task.Run(()=>blocking.Entered.Wait(TimeSpan.FromSeconds(3))));
            controller.SetEstimator("replacement");blocking.Release.Set();
            await Until(()=>controller.Current.Analysis?.MethodId=="replacement");
            Assert.Equal(1,controller.Current.Analysis!.Axes[Axis.LX].Count);
            Assert.Equal(3,controller.Current.Analysis.Axes[Axis.LX].Step.Bits);
            Assert.Equal("replacement",controller.Current.MethodId);
        }
        finally { blocking.Release.Set(); }
    }
}
