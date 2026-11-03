using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using Gpss.Application;
using Gpss.Core;
using Gpss.Desktop;

namespace Gpss.Desktop.Tests;

public sealed class WidgetsTests
{
    private static void Sta(Action action)
    {
        Exception? error=null;
        var thread=new Thread(()=> { try{action();}catch(Exception ex){error=ex;} });
        thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Fact]
    public void SquareCornersAreNotProjectedIntoCircle()=>Sta(()=>
    {
        var center=StickControl.MapPoint(400,320,0,0);
        var corner=StickControl.MapPoint(400,320,1,1);
        Assert.Equal(200,center.X);Assert.Equal(160,center.Y);
        Assert.Equal(330,corner.X);Assert.Equal(290,corner.Y);
        Assert.True(Math.Pow(corner.X-center.X,2)+Math.Pow(corner.Y-center.Y,2)>130*130);
        Assert.Equal(30,StickControl.MapPoint(400,320,0,-1).Y);
    });
    [Fact]
    public void BitsUseOneDecimalAwayFromZero()
    {
        Assert.Equal("8.1bit",StickPanel.Bits(8.05));Assert.Equal("—",StickPanel.Bits(null));
        Assert.Equal("—",StickPanel.Bits(double.NaN));
    }
    [Fact]
    public void MappingDoesNotGuessAmbiguousUsages()
    {
        var device=new DeviceInfo("pad","pad",BackendKind.HidDirect,
            [new("x1","X1",0,255,8,1,0x30,1),new("x2","X2",0,255,8,1,0x30,2),new("y","Y",0,255,8,1,0x31)]);
        var mapping=MainWindow.SuggestMapping(device);
        Assert.DoesNotContain(mapping.Bindings,b=>b.Axis==Axis.LX);
        Assert.Empty(mapping.Bindings);
    }
    [Fact]
    public void StickControlCanRenderFullDomain()=>Sta(()=>
    {
        var stick=new StickControl { Width=400,Height=320,X=1,Y=1 };
        stick.Measure(new Size(400,320));stick.Arrange(new Rect(0,0,400,320));stick.UpdateLayout();
        var target=new System.Windows.Media.Imaging.RenderTargetBitmap(400,320,96,96,PixelFormats.Pbgra32);
        target.Render(stick);Assert.Equal(400,target.PixelWidth);
    });
    [Fact]
    public void StickControlMeasuresToFiniteSquareCell()=>Sta(()=>
    {
        var stick=new StickControl();stick.Measure(new Size(300,180));
        Assert.Equal(new Size(180,180),stick.DesiredSize);
    });
}

public sealed class PanelStateTests
{
    private static void Sta(Action action)
    {
        Exception? error=null;
        var thread=new Thread(()=> { try{action();}catch(Exception ex){error=ex;} });
        thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static ApplicationSnapshot Snapshot()
    {
        var device=new DeviceInfo("demo","Demo",BackendKind.Simulation,[]);
        var axes=ImmutableDictionary.CreateBuilder<Axis,AxisAnalysis>();
        var latest=ImmutableDictionary.CreateBuilder<Axis,AxisSample>();
        foreach(var axis in Enum.GetValues<Axis>())
        {
            var value=axis switch { Axis.LX=>1d,Axis.LY=>1d,Axis.RX=>-.5d,_=>.5d };
            var sample=new AxisSample(3,1,1000,123,value,0);latest.Add(axis,sample);
            var bits=axis==Axis.LY?9d:8d;
            var step=new StepEstimate(MeanBitStepEstimator.Id,EstimateStatus.Reference,2/Math.Pow(2,bits),Math.Pow(2,bits),bits,12,10,10d/12,false,2,"参考値");
            axes.Add(axis,new(axis,sample,new(1,value,0,0),step,40,1,false,0,1000));
        }
        var analysis=new AnalysisSnapshot(3,2000,axes.ToImmutable(),8,8,MeanBitStepEstimator.Id);
        return new(new(3,ConnectionState.Connected,device),new(),BackendKind.Simulation,analysis,latest.ToImmutable(),new(),2000,1000,1000,0,0,MeanBitStepEstimator.Id,2);
    }
    [Fact]
    public void PanelShowsIndependentPositionAndAxisEstimates()=>Sta(()=>
    {
        var panel=new StickPanel("Left",Axis.LX,Axis.LY,Brushes.Blue);
        panel.Render(Snapshot());
        Assert.Equal(1d,panel.Stick.X!.Value);Assert.Equal(1d,panel.Stick.Y!.Value);
        Assert.Equal("8.5bit",panel.PrecisionText);
        Assert.Contains("Y 9.0bit",panel.AxisBitsText);
        Assert.Contains("参考値",panel.NotesText);Assert.Contains("有効な非ゼロ差分: 12",panel.DetailsText);
    });
    [Fact]
    public void PanelRejectsOldEpochAndDisconnectedReadings()=>Sta(()=>
    {
        var snapshot=Snapshot();var panel=new StickPanel("Left",Axis.LX,Axis.LY,Brushes.Blue);
        panel.Render(snapshot with { Status=snapshot.Status with { Epoch=4 } });
        Assert.Null(panel.Stick.X);Assert.Equal("—",panel.PrecisionText);
        panel.Render(snapshot with { Status=snapshot.Status with { State=ConnectionState.Disconnected } });
        Assert.Null(panel.Stick.Y);Assert.Equal("—",panel.PrecisionText);
    });
    [Fact]
    public void SameRevisionAnalysisClearImmediatelyClearsPanelAndHistoryMetrics()=>Sta(() =>
    {
        var snapshot=Snapshot() with { AnalysisRevision=5,HistoryRevision=5 };
        var panel=new StickPanel("Left",Axis.LX,Axis.LY,Brushes.Blue);
        panel.Render(snapshot);
        Assert.Equal("8.5bit",panel.PrecisionText);
        Assert.Contains("標準偏差",panel.History.StatisticsText);
        panel.Render(snapshot with { Analysis=null });
        Assert.Equal("—",panel.PrecisionText);
        Assert.Contains("データなし",panel.History.StatisticsText);
    });
}


public sealed class MappingDialogTests
{
    [Fact]
    public void XInputMappingUsesSharedYAxisInversion()
    {
        var device=new DeviceInfo("xinput:0","XInput",BackendKind.XInput,
            [new("lx","LX",-32768,32767),new("ly","LY",-32768,32767),new("rx","RX",-32768,32767),new("ry","RY",-32768,32767)]);
        var mapping=MainWindow.SuggestMapping(device);
        Assert.Equal(4,mapping.Bindings.Length);
        Assert.True(mapping.Bindings.Single(b=>b.Axis==Axis.LY).Inverted);
        Assert.True(mapping.Bindings.Single(b=>b.Axis==Axis.RY).Inverted);
        Assert.False(mapping.Bindings.Single(b=>b.Axis==Axis.LX).Inverted);
    }
    [Fact]
    public void AcceptedMappingClosesModalDialogEvenDuringPreviewOperation()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            GpssController? controller=null;
            try
            {
                controller=new GpssController(_=>new Gpss.Input.Simulation.SimulationInputSource());
                var device=Gpss.Input.Simulation.SimulationInputSource.DemoDevice;
                var mapping=SuggestedMapping.Create(device);
                var dialog=new AxisMappingWindow(controller,device,mapping,new());
                dialog.Dispatcher.BeginInvoke(new Action(async()=>
                {
                    try { await dialog.ApplyPreviewAsync(true); }
                    catch(Exception ex) { failure=ex;dialog.Close(); }
                }));
                Assert.True(dialog.ShowDialog());
                Assert.NotNull(dialog.AcceptedMapping);
                Assert.False(dialog.IsVisible);
            }
            catch(Exception ex) { failure=ex; }
            finally { controller?.StopAsync().GetAwaiter().GetResult(); }
        });
        thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)),"Axis dialog did not close after accepting a mapping");
        if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

public sealed class HistoryDecimationTests
{
    [Fact]
    public void ChartUsesSelectedPeriodAndMinusOneToOneGeometry()
    {
        foreach(var seconds in MeasurementWindow.Options)
        {
            Assert.Equal(36,HistoryChartGeometry.MapX(-seconds,36,400,seconds));
            Assert.Equal(236,HistoryChartGeometry.MapX(-seconds/2d,36,400,seconds));
            Assert.Equal(436,HistoryChartGeometry.MapX(0,36,400,seconds));
        }
        Assert.Equal(36,HistoryChartGeometry.MapX(-30,36,400));
        Assert.Equal(436,HistoryChartGeometry.MapX(0,36,400));
        Assert.Equal(20,HistoryChartGeometry.MapY(1,20,80));
        Assert.Equal(60,HistoryChartGeometry.MapY(0,20,80));
        Assert.Equal(100,HistoryChartGeometry.MapY(-1,20,80));
    }

    [Fact]
    public void PlotWindowIsRejectedAcrossEpochAndDisconnectBoundaries()
    {
        var window=new AxisWindowSnapshot(4,Axis.LX,100,10,[],[],0,0,false);
        Assert.True(HistoryControl.IsWindowCurrent(ConnectionState.Connected,4,window));
        Assert.False(HistoryControl.IsWindowCurrent(ConnectionState.Connected,5,window));
        Assert.False(HistoryControl.IsWindowCurrent(ConnectionState.Disconnected,4,window));
        Assert.False(HistoryControl.IsWindowCurrent(ConnectionState.Connected,4,null));
    }

    [Fact]
    public void KnownSeriesStatisticsAreShownWithoutRoundingAwayPrecision()
    {
        var analysis=new AxisAnalysis(Axis.LX,null,new(3,0,Math.Sqrt(2d/3d),1),new(MeanBitStepEstimator.Id),3,2.5,false,0,1.2);
        var text=HistoryControl.FormatStatistics(analysis,null);
        Assert.Contains("標準偏差 0.816497",text);
        Assert.Contains("平均 0.000000",text);
        Assert.Contains("平均からの最大偏差 1.000000",text);
        Assert.Contains("3点 / 2.5秒",text);
    }

    [Fact]
    public void ProjectionReductionIsNotReportedAsAcquisitionLoss()
    {
        var window=new AxisWindowSnapshot(1,Axis.LX,10,1,[],[],1,0,false,true);
        var analysis=new AxisAnalysis(Axis.LX,null,new(1,0,0,0),new(MeanBitStepEstimator.Id),1,1,false,0,0);
        var text=HistoryControl.FormatStatistics(analysis,window);
        Assert.Contains("描画点を省略",text);
        Assert.DoesNotContain("取得保持上限到達",text);
    }

    [Fact]
    public void MinMaxKeepsNarrowExtremaAndDoesNotJoinContinuityRuns()
    {
        // All points occupy one pixel; the short positive and negative peaks must survive.
        var points=ImmutableArray.Create(
            new AxisSample(1,1,0,0,0,7),
            new AxisSample(1,2,1,0,.9,7),
            new AxisSample(1,3,2,0,-.8,7),
            new AxisSample(1,4,3,0,.1,7),
            new AxisSample(1,5,4,0,.2,8),
            new AxisSample(1,6,5,0,.3,8));
        var reduced=HistoryMinMaxDecimator.Decimate(points,5,1,1);
        Assert.Contains(reduced,p=>p.Value==.9);
        Assert.Contains(reduced,p=>p.Value==-.8);
        Assert.Contains(reduced,p=>p.ContinuityId==7);
        Assert.Contains(reduced,p=>p.ContinuityId==8);
        Assert.Equal(points.Length,reduced.Length);
    }
}
