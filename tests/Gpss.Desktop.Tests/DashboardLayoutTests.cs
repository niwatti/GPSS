using System.Collections.Immutable;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gpss.Application;
using Gpss.Core;
using Gpss.Desktop;
using Gpss.Input.Simulation;

namespace Gpss.Desktop.Tests;

public sealed class DashboardLayoutTests
{
    // Approximate client areas on a 1920x1080 display, including taskbar/window chrome.
    [Theory]
    [InlineData(1900, 990)]
    [InlineData(1516, 790)]
    [InlineData(1260, 650)]
    [InlineData(940, 480)]
    public void BothSticksMetricsAndPlotsFitWithoutDashboardScrolling(double width, double height)
    {
        Exception? error=null;
        var thread=new Thread(()=>
        {
            GpssController? controller=null;MainWindow? window=null;
            try
            {
                controller=new GpssController(_=>new SimulationInputSource());
                window=new MainWindow(controller,settingsStore:new SettingsStore(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"settings.json")))
                    { FontFamily=new FontFamily("Yu Gothic UI, Segoe UI"),FontSize=13 };
                Assert.Equal(WindowState.Maximized,window.WindowState);
                var icon=Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapFrame>(window.Icon);
                Assert.NotNull(icon.Decoder);
                Assert.Equal(new[] {16,20,24,32,40,48,64,128,256},icon.Decoder.Frames.Select(f=>f.PixelWidth).Order().ToArray());
                window.Render(Snapshot());
                var content=Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                // SizeChanged updates the finite design canvas after the first arrange.
                for(var i=0;i<3;i++)
                {
                    content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
                }
                Assert.DoesNotContain(Descendants(content),e=>e is ScrollViewer);
                AssertInside(window.StepHistogram, content);
                Assert.True(window.StepHistogram.ActualHeight >= 154);
                Assert.Equal(3, Descendants(content).OfType<ComboBox>().Count());
                var period = Assert.Single(Descendants(content).OfType<ComboBox>(), c => c.Items.Count == 4);
                Assert.Equal(1, period.SelectedIndex); // 30 seconds is the default.
                Assert.Contains(Descendants(content).OfType<TextBlock>(), t => t.Text == "低遅延（固定）");
                Assert.Contains(Descendants(content).OfType<TextBlock>(), t => t.Text == "平均ステップ精度");
                foreach(var card in new[] { window.LeftPanel,window.RightPanel })
                {
                    AssertInside(card,content);AssertInside(card.Stick,card);AssertInside(card.History,card);
                    var top=Assert.IsType<Grid>(card.Child).Children.OfType<Grid>().Single(e=>Grid.GetRow(e)==1);
                    foreach(var element in Descendants(top))AssertInside(element,top);
                    foreach(var element in Descendants(card.History))AssertInside(element,card.History);
                    Assert.True(card.Stick.ActualWidth>=160);Assert.True(card.Stick.ActualHeight>=160);
                    Assert.Contains("標準偏差",card.History.StatisticsText);
                    Assert.Contains("平均からの最大偏差",card.History.StatisticsText);
                    var plots=card.History.Children.OfType<FrameworkElement>().Where(e=>e.GetType().Name=="HistoryPlot").ToArray();
                    Assert.Equal(2,plots.Length);
                    foreach(var plot in plots) { AssertInside(plot,card.History);Assert.True(plot.ActualHeight>=88); }
                    foreach(var text in Descendants(card).OfType<TextBlock>()) AssertInside(text,card);
                }
                foreach(var button in Descendants(content).OfType<Button>()) AssertInside(button,content);
                // Check transformed bounds after high-DPI fallback scaling as well.
                AssertInside(window.LeftPanel,content);AssertInside(window.RightPanel,content);
            }
            catch(Exception ex) { error=ex; }
            finally
            {
                // The window is never shown or given a native handle in this layout-only test.
                controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
        thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)),"Layout did not complete");
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static IEnumerable<FrameworkElement> Descendants(FrameworkElement root)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<FrameworkElement>())
        {
            yield return child;
            foreach(var nested in Descendants(child))yield return nested;
        }
    }
    private static void AssertInside(FrameworkElement element,FrameworkElement ancestor)
    {
        var bounds=element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
        const double tolerance=1;
        Assert.True(bounds.Left>=-tolerance && bounds.Top>=-tolerance && bounds.Right<=ancestor.ActualWidth+tolerance && bounds.Bottom<=ancestor.ActualHeight+tolerance,
            $"{element.GetType().Name} {bounds} extends outside {ancestor.GetType().Name} {ancestor.RenderSize}");
    }
    private static ApplicationSnapshot Snapshot()
    {
        var axes=ImmutableDictionary.CreateBuilder<Axis,AxisAnalysis>();
        var latest=ImmutableDictionary.CreateBuilder<Axis,AxisSample>();
        var history=ImmutableDictionary.CreateBuilder<Axis,AxisWindowSnapshot>();
        foreach(var axis in Enum.GetValues<Axis>())
        {
            var sample=new AxisSample(3,30000,30_000,127,.25,0);latest.Add(axis,sample);
            var step=new StepEstimate(MeanBitStepEstimator.Id,EstimateStatus.Reference,2d/255,255,8,12,10,10d/12,true,2,"参考値（非ゼロ差分30個未満）／最頻値が同率");
            axes.Add(axis,new(axis,sample,new(30000,.000001,.123456,.999999),step,30000,30,false,0,1000));
            history.Add(axis,new(3,axis,30_000,1000,[sample],[],30,0,false));
        }
        var analysis=new AnalysisSnapshot(3,30_000,axes.ToImmutable(),8,8,MeanBitStepEstimator.Id);
        return new(new(3,ConnectionState.Connected,SimulationInputSource.DemoDevice),new(),BackendKind.XInput,analysis,
            latest.ToImmutable(),new(30000,30000),30_000,1000,1000,1000,10,MeanBitStepEstimator.Id,30,history.ToImmutable(),
            StepHistogram: new(3, 30_000, 30, "取得差分のbit数", [new(8, 12345), new(10, 9876)], 22221));
    }
}
