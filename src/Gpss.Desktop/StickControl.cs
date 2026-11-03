using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Gpss.Desktop;

/// <summary>Unprojected [-1,1]² domain, with a reference circle and current point.</summary>
public sealed class StickControl : FrameworkElement
{
    private static readonly Typeface Typeface=new("Segoe UI");
    private static readonly Brush BorderBrush=FrozenBrush(Color.FromRgb(203,213,225));
    private static readonly Brush GridBrush=FrozenBrush(Color.FromRgb(226,232,240));
    private static readonly Brush BackgroundBrush=FrozenBrush(Color.FromRgb(248,250,252));
    private static readonly Brush LabelBrush=FrozenBrush(Color.FromRgb(100,116,139));
    private static readonly Brush HaloBrush=FrozenBrush(Color.FromArgb(35,37,99,235));
    private static readonly Pen BorderPen=FrozenPen(BorderBrush,1);
    private static readonly Pen GridPen=FrozenPen(GridBrush,1,DashStyles.Dash);
    private static readonly Pen DotPen=FrozenPen(Brushes.White,2);
    private DrawingGroup? _background;
    private double _backgroundWidth=-1,_backgroundHeight=-1,_pixelsPerDip=-1;
    private bool _backgroundDataMissing;
    public static readonly DependencyProperty XProperty = DependencyProperty.Register(nameof(X), typeof(double?), typeof(StickControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YProperty = DependencyProperty.Register(nameof(Y), typeof(double?), typeof(StickControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double? X { get => (double?)GetValue(XProperty); set => SetValue(XProperty, value); }
    public double? Y { get => (double?)GetValue(YProperty); set => SetValue(YProperty, value); }
    public Brush DotBrush { get; set; } = new SolidColorBrush(Color.FromRgb(37, 99, 235));
    public StickControl()
    {
        MinWidth=160;MinHeight=160;
        SizeChanged+=(_,_)=>{_background=null;InvalidateVisual();};
    }
    public static Point MapPoint(double width, double height, double x, double y)
    {
        var radius = Math.Max(1, Math.Min(width, height) / 2 - 30);
        return new(width / 2 + radius * x, height / 2 + radius * y);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 360 : availableSize.Height;
        var side = Math.Min(width, height);
        if (!double.IsFinite(side) || side <= 0) side = 360;
        return new(side, side);
    }
    protected override void OnRender(DrawingContext dc)
    {
        var started=Stopwatch.GetTimestamp();
        try
        {
            base.OnRender(dc);
            var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var valid=X is double candidateX&&Y is double candidateY&&double.IsFinite(candidateX)&&double.IsFinite(candidateY)&&Math.Abs(candidateX)<=1&&Math.Abs(candidateY)<=1;
            if(_background is null||_backgroundWidth!=ActualWidth||_backgroundHeight!=ActualHeight||_pixelsPerDip!=dpi||_backgroundDataMissing != !valid)BuildBackground(dpi,!valid);
            if(_background is not null)dc.DrawDrawing(_background);
            if(valid&&X is double x&&Y is double y)
            {
                var point=MapPoint(ActualWidth,ActualHeight,x,y);
                dc.DrawEllipse(HaloBrush,null,point,13,13);
                dc.DrawEllipse(DotBrush,DotPen,point,7,7);
            }
        }
        finally { DesktopPerformanceMetrics.RecordRender(Stopwatch.GetTimestamp()-started,false); }
    }
    private void BuildBackground(double pixelsPerDip,bool dataMissing)
    {
        var width=ActualWidth;var height=ActualHeight;var center=new Point(width/2,height/2);var radius=Math.Max(1,Math.Min(width,height)/2-30);
        var drawing=new DrawingGroup();using(var dc=drawing.Open())
        {
            dc.DrawRectangle(BackgroundBrush,BorderPen,new Rect(center.X-radius,center.Y-radius,radius*2,radius*2));
            dc.DrawEllipse(Brushes.White,BorderPen,center,radius,radius);
            dc.DrawEllipse(null,GridPen,center,radius/2,radius/2);
            dc.DrawLine(GridPen,new(center.X-radius,center.Y),new(center.X+radius,center.Y));
            dc.DrawLine(GridPen,new(center.X,center.Y-radius),new(center.X,center.Y+radius));
            DrawText(dc,"−1",new(center.X-radius-25,center.Y-8),pixelsPerDip);
            DrawText(dc,"+1",new(center.X+radius+5,center.Y-8),pixelsPerDip);
            DrawText(dc,"−1",new(center.X-8,center.Y-radius-25),pixelsPerDip);
            DrawText(dc,"+1",new(center.X-8,center.Y+radius+5),pixelsPerDip);
            DrawText(dc,"0",new(center.X+6,center.Y+5),pixelsPerDip);
            if(dataMissing)DrawText(dc,"データなし",new(center.X-32,center.Y+30),pixelsPerDip);
        }
        drawing.Freeze();_background=drawing;_backgroundWidth=width;_backgroundHeight=height;_pixelsPerDip=pixelsPerDip;_backgroundDataMissing=dataMissing;
    }
    private static void DrawText(DrawingContext dc,string text,Point origin,double pixelsPerDip)
        =>dc.DrawText(new FormattedText(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,Typeface,12,LabelBrush,pixelsPerDip),origin);
    private static Brush FrozenBrush(Color color){var brush=new SolidColorBrush(color);brush.Freeze();return brush;}
    private static Pen FrozenPen(Brush brush,double thickness,DashStyle? dash=null){var pen=new Pen(brush,thickness){DashStyle=dash??DashStyles.Solid};pen.Freeze();return pen;}
}
