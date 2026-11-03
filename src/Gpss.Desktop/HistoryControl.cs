using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Gpss.Application;
using Gpss.Core;

namespace Gpss.Desktop;

/// <summary>Two independently scaled-in-color but identically scaled-in-value plots for one stick.</summary>
public sealed class HistoryControl : Grid
{
    private readonly Axis _xAxis, _yAxis;
    private readonly HistoryPlot _xPlot = new("X", Color.FromRgb(37, 99, 235));
    private readonly HistoryPlot _yPlot = new("Y", Color.FromRgb(220, 38, 38));
    private readonly TextBlock _stdDevX = MetricCell(), _stdDevY = MetricCell();
    private readonly TextBlock _meanX = MetricCell(), _meanY = MetricCell();
    private readonly TextBlock _windowHeading = new() { Text="震え（最新30秒）",Margin=new(3,1,3,1),FontSize=11,FontWeight=FontWeights.SemiBold };
    private readonly TextBlock _legend = new() { FontSize=10,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#475569"),Margin=new(2,0,2,1) };
    private int _windowSeconds=MeasurementWindow.DefaultSeconds;
    private readonly TextBlock _maxDevX = MetricCell(), _maxDevY = MetricCell();
    private readonly TextBlock _retention = new() { Margin=new(2,2,2,2), FontSize=11, TextWrapping=TextWrapping.Wrap, Foreground=Brush("#475569") };
    private string _statisticsText = "";
    private long _lastEpoch=long.MinValue,_analysisRevision,_historyRevision,_lastWindowEndTicks=long.MinValue;
    private bool _lastConnected;
    private AnalysisSnapshot? _lastAnalysis;
    private AxisWindowSnapshot? _lastXWindow,_lastYWindow;
    public string StatisticsText => _statisticsText;
    public HistoryControl(Axis xAxis, Axis yAxis)
    {
        _xAxis=xAxis; _yAxis=yAxis;
        RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });

        var table=new Grid { Margin=new(0,0,0,2),ToolTip="受信サンプルを等しい重みで集計。選択した期間に満たない場合は取得済みの範囲で計算します。" };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1.55,GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
        for(var i=0;i<4;i++) table.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        AddCell(table,"",0,0,element:_windowHeading); AddCell(table,"X",0,1,true); AddCell(table,"Y",0,2,true);
        AddCell(table,"標準偏差",1,0); AddCell(table,"",1,1,element:_stdDevX); AddCell(table,"",1,2,element:_stdDevY);
        AddCell(table,"平均",2,0); AddCell(table,"",2,1,element:_meanX); AddCell(table,"",2,2,element:_meanY);
        AddCell(table,"平均からの最大偏差",3,0); AddCell(table,"",3,1,element:_maxDevX); AddCell(table,"",3,2,element:_maxDevY);
        Children.Add(table);

        _retention.Text="X: 0点 / 0.0秒　　Y: 0点 / 0.0秒";
        Grid.SetRow(_retention,1); Children.Add(_retention);
        UpdatePeriodLabels();
        Grid.SetRow(_legend,2); Children.Add(_legend);
        Grid.SetRow(_xPlot,3); Children.Add(_xPlot);
        Grid.SetRow(_yPlot,4); Children.Add(_yPlot);
        _xPlot.MinHeight=88; _yPlot.MinHeight=88;
    }
    public void Render(ApplicationSnapshot snapshot)
    {
        var connected=snapshot.Status.State==ConnectionState.Connected;
        AxisAnalysis? x = null, y = null;
        if (connected && snapshot.Analysis?.Epoch == snapshot.Status.Epoch)
        {
            snapshot.Analysis.Axes.TryGetValue(_xAxis, out x);
            snapshot.Analysis.Axes.TryGetValue(_yAxis, out y);
        }
        var h = snapshot.History;
        AxisWindowSnapshot? wx=null, wy=null;
        if(h is not null) { if(h.TryGetValue(_xAxis,out var xw)) wx=xw; if(h.TryGetValue(_yAxis,out var yw)) wy=yw; }
        if(!IsWindowCurrent(snapshot.Status.State,snapshot.Status.Epoch,wx)) wx=null;
        if(!IsWindowCurrent(snapshot.Status.State,snapshot.Status.Epoch,wy)) wy=null;
        var periodChanged=_windowSeconds!=snapshot.MeasurementWindowSeconds;
        if(periodChanged){_windowSeconds=snapshot.MeasurementWindowSeconds;UpdatePeriodLabels();}
        var boundary=periodChanged||_lastEpoch!=snapshot.Status.Epoch||_lastConnected!=connected;
        var analysisChanged=boundary||!ReferenceEquals(snapshot.Analysis,_lastAnalysis)||
            (snapshot.AnalysisRevision!=0?snapshot.AnalysisRevision!=_analysisRevision:false);
        var windowEnd=wx?.Now??wy?.Now??long.MinValue;
        var historyChanged=boundary||(snapshot.HistoryRevision!=0?snapshot.HistoryRevision!=_historyRevision:
            !ReferenceEquals(wx,_lastXWindow)||!ReferenceEquals(wy,_lastYWindow)||windowEnd!=_lastWindowEndTicks);
        if(analysisChanged||historyChanged)
        {
            _statisticsText = $"震え統計（受信サンプル等重み・最新{MeasurementWindow.Label(_windowSeconds)}）\nX: {FormatStatistics(x,wx)}　　Y: {FormatStatistics(y,wy)}";
            SetCellText(_retention,$"X: {Retention(x,wx)}　　Y: {Retention(y,wy)}");
        }
        if(analysisChanged)
        {
            SetMetrics(x,_stdDevX,_meanX,_maxDevX);
            SetMetrics(y,_stdDevY,_meanY,_maxDevY);
        }
        if(historyChanged||analysisChanged)
        {
            SetPlot(_xPlot, wx, x, snapshot);
            SetPlot(_yPlot, wy, y, snapshot);
        }
        _lastEpoch=snapshot.Status.Epoch;_lastConnected=connected;
        _analysisRevision=snapshot.AnalysisRevision;_historyRevision=snapshot.HistoryRevision;_lastWindowEndTicks=windowEnd;
        _lastAnalysis=snapshot.Analysis;_lastXWindow=wx;_lastYWindow=wy;
    }
    private static void SetPlot(HistoryPlot plot, AxisWindowSnapshot? window, AxisAnalysis? analysis, ApplicationSnapshot snapshot)
    {
        plot.Set(window, analysis, snapshot.Now, snapshot.Frequency, snapshot.Status.State == ConnectionState.Connected, snapshot.Status.Epoch, snapshot.HistoryRevision,snapshot.MeasurementWindowSeconds);
    }
    private void UpdatePeriodLabels()
    {
        SetCellText(_windowHeading,$"震え（最新{MeasurementWindow.Label(_windowSeconds)}）");
        SetCellText(_legend,$"線: 波形　破線: 平均　帯: ±標準偏差　横軸: −{_windowSeconds}〜0秒 / 縦軸: −1〜1");
    }
    public static bool IsWindowCurrent(ConnectionState state,long epoch,AxisWindowSnapshot? window)
        => state==ConnectionState.Connected && window is not null && window.Epoch==epoch;
    public static string FormatStatistics(AxisAnalysis? axis, AxisWindowSnapshot? window)
    {
        var count=axis?.Count ?? window?.Samples.Length ?? 0;
        var retained=axis?.RetainedSeconds ?? window?.RetainedSeconds ?? 0;
        var acquisitionTruncated=(axis?.Truncated ?? false) || (window?.Truncated ?? false);
        var projectionTruncated=window?.ProjectionTruncated??false;
        var warning=(acquisitionTruncated?"　取得保持上限到達":"")+(projectionTruncated?"　描画点を省略":"");
        if(axis is null || axis.Jitter.Count==0 || axis.Jitter.Mean is null) return $"標準偏差 —　平均 —　平均からの最大偏差 —　データなし　{count:N0}点 / {retained:F1}秒"+warning;
        return $"標準偏差 {F(axis.Jitter.PopulationStdDev)}　平均 {F(axis.Jitter.Mean)}　平均からの最大偏差 {F(axis.Jitter.MaxDeviation)}　{count:N0}点 / {retained:F1}秒"+warning;
    }
    private static void SetMetrics(AxisAnalysis? axis,TextBlock sigma,TextBlock mean,TextBlock maxDeviation)
    {
        var valid=axis is not null && axis.Jitter.Count>0 && axis.Jitter.Mean is not null;
        SetCellText(sigma,valid?F(axis!.Jitter.PopulationStdDev):"—");
        SetCellText(mean,valid?F(axis!.Jitter.Mean):"—");
        SetCellText(maxDeviation,valid?F(axis!.Jitter.MaxDeviation):"—");
    }
    private static string Retention(AxisAnalysis? axis,AxisWindowSnapshot? window)
    {
        var count=axis?.Count??window?.Samples.Length??0;
        var seconds=axis?.RetainedSeconds??window?.RetainedSeconds??0;
        var text=$"{count:N0}点 / {seconds:F1}秒";
        if((axis?.Truncated??false)||(window?.Truncated??false))text+="（取得保持上限到達）";
        if(window?.ProjectionTruncated??false)text+="（描画点を省略）";
        return text;
    }
    private static TextBlock MetricCell()=>new() { Margin=new(3,1,3,1), FontSize=11, Foreground=Brush("#0f172a"), HorizontalAlignment=HorizontalAlignment.Left };
    private static void SetCellText(TextBlock block,string value) { if(block.Text!=value)block.Text=value; }
    private static void AddCell(Grid grid,string text,int row,int column,bool bold=false,TextBlock? element=null)
    {
        var cell=element??new TextBlock { Text=text, Margin=new(3,1,3,1), FontSize=11, Foreground=Brush("#334155"), FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal };
        Grid.SetRow(cell,row); Grid.SetColumn(cell,column); grid.Children.Add(cell);
    }
    private static string F(double? value)=>value is double v && double.IsFinite(v)?v.ToString("0.000000;-0.000000;0.000000",CultureInfo.InvariantCulture):"—";
    private static Brush Brush(string hex)=>(Brush)new BrushConverter().ConvertFromString(hex)!;

    private sealed class HistoryPlot : FrameworkElement
    {
        private static readonly Typeface Typeface=new("Segoe UI");
        private static readonly Brush GridBrush=FrozenBrush(Color.FromRgb(226,232,240));
        private static readonly Brush AxisBrush=FrozenBrush(Color.FromRgb(148,163,184));
        private static readonly Brush LabelBrush=FrozenBrush(Color.FromRgb(100,116,139));
        private static readonly Brush TitleBrush=FrozenBrush(Color.FromRgb(15,23,42));
        private static readonly Pen GridPen=FrozenPen(GridBrush,1);
        private static readonly Pen AxisPen=FrozenPen(AxisBrush,1);
        private readonly string _title; private readonly Brush _line; private readonly Brush _band; private readonly Pen _wavePen,_meanPen;
        private readonly DispatcherTimer _buildTimer=new(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(100) };
        private ImmutableArray<AxisSample> _samples=[];
        private DrawingGroup? _staticLayer;
        private Geometry? _bandGeometry,_meanGeometry,_waveGeometry,_plotClip;
        private double? _mean,_sigma; private long _now,_frequency,_epoch=long.MinValue,_lastBuildWallTicks,_historyRevision; private bool _connected,_pendingBuild;
        private int _displayPointCount;
        private double _builtWidth=-1,_builtHeight=-1,_builtPixelsPerDip=-1;
        private bool _staticConnected;
        private double _windowSeconds=MeasurementWindow.DefaultSeconds;
        public HistoryPlot(string title, Color color)
        {
            _title=title;_line=FrozenBrush(color);_band=FrozenBrush(Color.FromArgb(35,color.R,color.G,color.B));
            _wavePen=FrozenPen(_line,1.25);_meanPen=FrozenPen(_line,1.2,DashStyles.Dash);
            MinHeight=88; HorizontalAlignment=HorizontalAlignment.Stretch;ClipToBounds=true;
            _buildTimer.Tick+=(_,_)=>{_buildTimer.Stop();if(IsVisible)BuildLayers();else _pendingBuild=true;};
            IsVisibleChanged+=(_,_)=>
            {
                if(!IsVisible){_buildTimer.Stop();_pendingBuild=true;}
                else if(_pendingBuild)RequestBuild(force:true);
            };
            Unloaded+=(_,_)=>{_buildTimer.Stop();_pendingBuild=true;};
            SizeChanged+=(_,_)=>{_pendingBuild=true;RequestBuild();};
        }
        public void Set(AxisWindowSnapshot? window, AxisAnalysis? analysis, long now, long frequency, bool connected, long epoch, long historyRevision,int windowSeconds)
        {
            var current=connected && window is not null && window.Epoch==epoch;
            var boundary=_epoch!=epoch||_connected!=current||_windowSeconds!=windowSeconds;
            var changed=boundary||historyRevision!=_historyRevision||!ReferenceEquals(window,_window)||window?.Now!=_window?.Now||!ReferenceEquals(analysis,_analysis);
            if(!changed)return;
            if(boundary)
            {
                _staticLayer=null;_bandGeometry=null;_meanGeometry=null;_waveGeometry=null;_plotClip=null;
            }
            var statsChanged=boundary||!ReferenceEquals(analysis,_analysis);
            _epoch=epoch;_historyRevision=historyRevision;_window=window;_analysis=analysis;
            _windowSeconds=windowSeconds;
            _samples=current?window!.Samples:[];_now=window?.Now??now;_frequency=window?.Frequency??frequency;_connected=current;
            _mean=analysis?.Jitter.Mean;_sigma=analysis?.Jitter.PopulationStdDev;
            if(statsChanged){_bandGeometry=null;_meanGeometry=null;}
            if(boundary)RequestBuild(force:true);else RequestBuild();
        }
        protected override void OnRender(DrawingContext dc)
        {
            var started=Stopwatch.GetTimestamp();
            try
            {
                base.OnRender(dc);
                var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
                if(_builtWidth!=ActualWidth||_builtHeight!=ActualHeight||_builtPixelsPerDip!=dpi)
                {
                    _pendingBuild=true;RequestBuild();
                }
                if(_staticLayer is null)
                {
                    if(IsVisible&&!_buildTimer.IsEnabled)RequestBuild();
                    if(_staticLayer is null)return;
                }
                dc.DrawDrawing(_staticLayer);
                if(_plotClip is not null)
                {
                    dc.PushClip(_plotClip);
                    if(_bandGeometry is not null)dc.DrawGeometry(_band,null,_bandGeometry);
                    if(_meanGeometry is not null)dc.DrawGeometry(null,_meanPen,_meanGeometry);
                    if(_waveGeometry is not null)dc.DrawGeometry(null,_wavePen,_waveGeometry);
                    dc.Pop();
                }
            }
            finally { DesktopPerformanceMetrics.RecordRender(Stopwatch.GetTimestamp()-started,true); }
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _pendingBuild=true;RequestBuild();
        }
        private void RequestBuild(bool force=false)
        {
            _pendingBuild=true;
            if(!IsVisible||ActualWidth<=0||ActualHeight<=0)return;
            var now=Stopwatch.GetTimestamp();
            var elapsed=now-_lastBuildWallTicks;
            if(force||_lastBuildWallTicks==0||elapsed>=Stopwatch.Frequency/10)
            {
                _buildTimer.Stop();BuildLayers();return;
            }
            _buildTimer.Interval=TimeSpan.FromSeconds((Stopwatch.Frequency/10-elapsed)/(double)Stopwatch.Frequency);
            if(!_buildTimer.IsEnabled)_buildTimer.Start();
        }
        private void BuildLayers()
        {
            if(!IsVisible||ActualWidth<=0||ActualHeight<=0){_pendingBuild=true;return;}
            var started=Stopwatch.GetTimestamp();
            var width=ActualWidth;var height=ActualHeight;var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var left=36d;var right=Math.Max(left+1,width-8);var top=20d;var bottom=Math.Max(top+1,height-20);var plotWidth=right-left;var plotHeight=bottom-top;
            double X(double seconds)=>HistoryChartGeometry.MapX(seconds,left,plotWidth,_windowSeconds);
            double Y(double value)=>top+(1-Math.Clamp(value,-1,1))/2*plotHeight;

            if(_staticLayer is null||_builtWidth!=width||_builtHeight!=height||_builtPixelsPerDip!=dpi||_staticConnected!=_connected)
            {
                var layer=new DrawingGroup();
                using(var dc=layer.Open())
                {
                    dc.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
                    DrawText(dc,_title,new Point(0,1),12,TitleBrush,dpi);
                    foreach(double value in new[]{-1d,-.5,0,.5,1})
                    {
                        var y=Y(value);dc.DrawLine(value==0?AxisPen:GridPen,new Point(left,y),new Point(right,y));
                        DrawText(dc,value.ToString("0.0",CultureInfo.InvariantCulture),new Point(1,y-7),10,LabelBrush,dpi);
                    }
                    dc.DrawLine(AxisPen,new Point(left,top),new Point(left,bottom));
                    foreach(double seconds in new[]{-_windowSeconds,-_windowSeconds*2/3,-_windowSeconds/3,0})
                    {
                        var x=X(seconds);dc.DrawLine(GridPen,new Point(x,top),new Point(x,bottom));
                        var label=seconds.ToString("0.#",CultureInfo.InvariantCulture);
                        var labelWidth=new FormattedText(label,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,Typeface,10,LabelBrush,dpi).Width;
                        DrawText(dc,label,new Point(Math.Clamp(x-labelWidth/2,left,Math.Max(left,right-labelWidth)),bottom+2),10,LabelBrush,dpi);
                    }
                    if(!_connected)DrawText(dc,"未接続",new Point(left+8,top+8),11,LabelBrush,dpi);
                }
                layer.Freeze();_staticLayer=layer;
                var clip=new RectangleGeometry(new Rect(left,top,plotWidth,plotHeight));if(clip.CanFreeze)clip.Freeze();_plotClip=clip;
                _staticConnected=_connected;
            }

            _bandGeometry=null;_meanGeometry=null;
            if(_mean is double mean&&_sigma is double sigma&&double.IsFinite(mean)&&double.IsFinite(sigma))
            {
                var high=Y(Math.Clamp(mean+sigma,-1,1));var low=Y(Math.Clamp(mean-sigma,-1,1));
                var band=new StreamGeometry();using(var context=band.Open())
                {context.BeginFigure(new(left,high),true,true);context.LineTo(new(right,high),true,false);context.LineTo(new(right,low),true,false);context.LineTo(new(left,low),true,false);}
                band.Freeze();_bandGeometry=band;
                var average=new StreamGeometry();using(var context=average.Open()){context.BeginFigure(new(left,Y(mean)),false,false);context.LineTo(new(right,Y(mean)),true,true);}
                average.Freeze();_meanGeometry=average;
            }

            _waveGeometry=null;_displayPointCount=0;
            if(_connected&&!_samples.IsDefaultOrEmpty)
            {
                // Production history is already bounded and preserves extrema/continuity.
                // Consume it directly rather than allocating a second set of sample arrays.
                var decimated=_samples.Length<=8192?_samples:
                    HistoryProjection.Reduce(_window!,Math.Clamp((int)Math.Ceiling(plotWidth),1,1024),8192).Samples;
                var wave=new StreamGeometry();using(var context=wave.Open())
                {
                    long continuity=long.MinValue;bool active=false,hasSegment=false;Point last=default;
                    foreach(var sample in decimated)
                    {
                        var point=new Point(X((sample.Timestamp-_now)/(double)Math.Max(1,_frequency)),Y(sample.Value));
                        if(!active||sample.ContinuityId!=continuity)
                        {
                            if(active&&!hasSegment)context.LineTo(new Point(last.X+1,last.Y),true,false);
                            context.BeginFigure(point,false,false);continuity=sample.ContinuityId;active=true;hasSegment=false;
                        }
                        else {context.LineTo(point,true,false);hasSegment=true;}
                        last=point;
                    }
                    if(active&&!hasSegment)context.LineTo(new Point(last.X+1,last.Y),true,false);
                }
                wave.Freeze();_waveGeometry=wave;_displayPointCount=decimated.Length;
            }
            _builtWidth=width;_builtHeight=height;_builtPixelsPerDip=dpi;_pendingBuild=false;_lastBuildWallTicks=Stopwatch.GetTimestamp();
            DesktopPerformanceMetrics.RecordGeometry(_displayPointCount,Stopwatch.GetTimestamp()-started);
            InvalidateVisual();
        }
        private static void DrawText(DrawingContext dc,string value,Point point,double size,Brush brush,double pixelsPerDip)
            =>dc.DrawText(new FormattedText(value,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,Typeface,size,brush,pixelsPerDip),point);
        private static Brush FrozenBrush(Color color){var brush=new SolidColorBrush(color);brush.Freeze();return brush;}
        private static Pen FrozenPen(Brush brush,double thickness,DashStyle? dash=null){var pen=new Pen(brush,thickness){DashStyle=dash??DashStyles.Solid};pen.Freeze();return pen;}
        private AxisWindowSnapshot? _window;
        private AxisAnalysis? _analysis;
    }
}

public static class HistoryChartGeometry
{
    public static double MapX(double seconds,double left,double width,double windowSeconds=MeasurementWindow.DefaultSeconds)=>left+(seconds+windowSeconds)/windowSeconds*width;
    public static double MapY(double value,double top,double height)=>top+(1-Math.Clamp(value,-1,1))/2*height;
}

/// <summary>Retains extrema in each horizontal pixel bucket, ordered in time. Continuity boundaries are never merged.</summary>
public static class HistoryMinMaxDecimator
{
    public static ImmutableArray<AxisSample> Decimate(ImmutableArray<AxisSample> samples,long now,long frequency,double pixelWidth,double windowSeconds=MeasurementWindow.DefaultSeconds)
    {
        if(samples.IsDefaultOrEmpty || frequency<=0 || pixelWidth<=0 || !double.IsFinite(windowSeconds) || windowSeconds<=0) return [];
        int buckets=Math.Max(1,(int)Math.Min(100_000,Math.Ceiling(pixelWidth)));
        var result=ImmutableArray.CreateBuilder<AxisSample>(); int start=0;
        while(start<samples.Length)
        {
            int end=start+1; long continuity=samples[start].ContinuityId;
            while(end<samples.Length && samples[end].ContinuityId==continuity) end++;
            int i=start;
            while(i<end)
            {
                var s=samples[i]; double t=(s.Timestamp-now)/(double)frequency;
                if(t < -windowSeconds || t > 0) { i++; continue; }
                int bucket=Math.Clamp((int)((t+windowSeconds)/windowSeconds*buckets),0,buckets-1); int j=i+1,min=i,max=i,last=i;
                for(;j<end;j++) { var c=samples[j]; double ct=(c.Timestamp-now)/(double)frequency; if(ct < -windowSeconds || ct>0) continue; int cb=Math.Clamp((int)((ct+windowSeconds)/windowSeconds*buckets),0,buckets-1); if(cb!=bucket) break; last=j; if(c.Value<samples[min].Value)min=j;if(c.Value>samples[max].Value)max=j; }
                Add(i);
                if(min!=i && max!=i && min!=max)
                {
                    if(min<max) { Add(min); Add(max); }
                    else { Add(max); Add(min); }
                }
                else if(min!=i) Add(min);
                else if(max!=i) Add(max);
                Add(last);
                i=Math.Max(i+1,j);
                void Add(int index){if(result.Count==0 || result[^1].Sequence!=samples[index].Sequence || result[^1].Epoch!=samples[index].Epoch)result.Add(samples[index]);}
            }
            start=end;
        }
        return result.ToImmutable();
    }
}
