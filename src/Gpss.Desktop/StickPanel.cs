using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Gpss.Core;

namespace Gpss.Desktop;

public sealed class StickPanel : Border
{
    private readonly Axis _xAxis, _yAxis;
    private readonly TextBlock _coordinates = Text("X —    Y —", 16);
    private readonly TextBlock _precision = Text("—", 32, true);
    private readonly TextBlock _axisBits = Text("X —    Y —");
    private readonly TextBlock _notes = Text("XまたはY未計測", 12);
    private readonly TextBlock _age = Text("最終受信値: —", 12);
    private readonly TextBlock _details = Text("データなし", 12);
    private AxisSample? _lastX,_lastY;
    private AxisAnalysis? _cachedXAnalysis,_cachedYAnalysis;
    private AnalysisSnapshot? _lastAnalysis;
    private long _positionEpoch=long.MinValue,_analysisEpoch=long.MinValue,_analysisRevision,_lastAgeUpdate=long.MinValue;
    private ConnectionState _lastPositionState;
    private bool _lastAnalysisConnected,_detailsDirty=true,_detailsDialogVisible;
    public HistoryControl History { get; }
    public StickControl Stick { get; }
    public string PrecisionText => _precision.Text;
    public string AxisBitsText => _axisBits.Text;
    public string NotesText => _notes.Text;
    public string DetailsText { get { UpdateDetailsText(); return _details.Text; } }
    public StickPanel(string title, Axis xAxis, Axis yAxis, Brush brush)
    {
        _xAxis=xAxis; _yAxis=yAxis;
        Background=Brushes.White; BorderBrush=new SolidColorBrush(Color.FromRgb(226,232,240)); BorderThickness=new(1); CornerRadius=new(12); Padding=new(12); Margin=new(4);
        var panel=new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height=new GridLength(0.9,GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1.1,GridUnitType.Star) });
        var heading=Text(title,18,true); Grid.SetRow(heading,0); panel.Children.Add(heading);

        var top=new Grid { Margin=new(0,4,0,8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1.15,GridUnitType.Star) });
        Stick=new StickControl { DotBrush=brush, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
        Grid.SetColumn(Stick,0); top.Children.Add(Stick);
        var readout=new StackPanel { VerticalAlignment=VerticalAlignment.Center, Margin=new(8,0,0,0) };
        _coordinates.HorizontalAlignment=HorizontalAlignment.Left; _coordinates.FontSize=14; _coordinates.Margin=new(0,1,0,1);
        _age.FontSize=11; _age.Margin=new(0,1,0,1);
        _axisBits.FontSize=12; _axisBits.Margin=new(0,1,0,1);
        _notes.FontSize=11; _notes.Margin=new(0,2,0,1);
        var precisionHeading=new Grid();
        precisionHeading.ColumnDefinitions.Add(new ColumnDefinition());
        precisionHeading.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var precisionLabel=Text("平均ステップ精度",12); Grid.SetColumn(precisionLabel,0); precisionHeading.Children.Add(precisionLabel);
        var detailsButton=new Button { Content="詳細…", MinHeight=28, HorizontalAlignment=HorizontalAlignment.Right, Margin=new(2,2,0,2), Padding=new(7,2,7,2), FontSize=11 };
        detailsButton.Click+=(_,_)=>ShowDetails(title);
        Grid.SetColumn(detailsButton,1); precisionHeading.Children.Add(detailsButton);
        readout.Children.Add(precisionHeading);
        _precision.FontSize=28; _precision.Margin=new(0,1,0,2);
        _precision.Foreground=new SolidColorBrush(Color.FromRgb(29,78,216));
        readout.Children.Add(_precision); readout.Children.Add(_axisBits);
        readout.Children.Add(_coordinates); readout.Children.Add(_age); readout.Children.Add(_notes);
        Grid.SetColumn(readout,1); top.Children.Add(readout);
        Grid.SetRow(top,1); panel.Children.Add(top);

        History = new HistoryControl(xAxis, yAxis) { Margin=new(0,2,0,0) };
        Grid.SetRow(History,2); panel.Children.Add(History);
        Child=panel;
    }
    public void Render(Gpss.Application.ApplicationSnapshot snapshot)
    {
        History.Render(snapshot);
        var connected=snapshot.Status.State==ConnectionState.Connected;
        var x=connected && snapshot.Latest.TryGetValue(_xAxis,out var xv) && xv.Epoch==snapshot.Status.Epoch ? xv : (AxisSample?)null;
        var y=connected && snapshot.Latest.TryGetValue(_yAxis,out var yv) && yv.Epoch==snapshot.Status.Epoch ? yv : (AxisSample?)null;
        var positionBoundary=_positionEpoch!=snapshot.Status.Epoch||_lastPositionState!=snapshot.Status.State;
        if(positionBoundary||x?.Value!=_lastX?.Value||y?.Value!=_lastY?.Value)
        {
            if(positionBoundary||x?.Value!=_lastX?.Value)Stick.X=x?.Value;
            if(positionBoundary||y?.Value!=_lastY?.Value)Stick.Y=y?.Value;
            SetText(_coordinates,$"X {Position(x)}    Y {Position(y)}");
        }
        _positionEpoch=snapshot.Status.Epoch;_lastPositionState=snapshot.Status.State;_lastX=x;_lastY=y;
        if(positionBoundary||_lastAgeUpdate==long.MinValue||snapshot.Now-_lastAgeUpdate>=Math.Max(1,snapshot.Frequency/5))
        {
            _lastAgeUpdate=snapshot.Now;
            var prefix=snapshot.Backend==BackendKind.XInput ? "最終状態" : "最終受信値";
            SetText(_age,$"{prefix}: X {Age(snapshot,x)} / Y {Age(snapshot,y)}");
        }
        var analysis=connected && snapshot.Analysis?.Epoch==snapshot.Status.Epoch ? snapshot.Analysis : null;
        var analysisChanged=_analysisEpoch!=snapshot.Status.Epoch||_lastAnalysisConnected!=connected||
            !ReferenceEquals(analysis,_lastAnalysis)||
            (snapshot.AnalysisRevision!=0?snapshot.AnalysisRevision!=_analysisRevision:false);
        if(analysisChanged)
        {
            _analysisEpoch=snapshot.Status.Epoch;_lastAnalysisConnected=connected;_analysisRevision=snapshot.AnalysisRevision;_lastAnalysis=analysis;
            _cachedXAnalysis=null;_cachedYAnalysis=null;
            analysis?.Axes.TryGetValue(_xAxis,out _cachedXAnalysis);analysis?.Axes.TryGetValue(_yAxis,out _cachedYAnalysis);
            var representative=StepPrecision.MeanBits(_cachedXAnalysis?.Step,_cachedYAnalysis?.Step);
            SetText(_precision,Bits(representative));
            SetText(_axisBits,$"X {Bits(_cachedXAnalysis?.Step.Bits)}    Y {Bits(_cachedYAnalysis?.Step.Bits)}");
            SetText(_notes,(representative is null ? "有効な差分なし" : "X/Yの有効差分を合算した平均")+
                $"\nX: {Note(_cachedXAnalysis?.Step)}\nY: {Note(_cachedYAnalysis?.Step)}");
            _detailsDirty=true;
            if(_detailsDialogVisible)UpdateDetailsText();
        }
    }
    private static string Details(AxisAnalysis? a)
    {
        if(a is null) return "データなし";
        var e=a.Step;
        return $"平均ステップ精度: {Bits(e.Bits)}\n平均bit換算のステップ差（幾何平均）: {Number(e.Step)}\n段階数（平均bit換算）: {Number(e.Levels)}\n"+
            $"有効な非ゼロ差分: {e.NonzeroCount:N0}\n"+
            $"除外: {e.ExcludedCount:N0}\n"+
            $"保持: {a.RetainedSeconds:F1}秒 / {a.Count:N0}点 / {a.SampleRate:F1}サンプル/秒"+
            (a.Truncated ? "\n保持上限に到達（選択期間未満）":"");
    }
    private static string Note(StepEstimate? e)=>e is null ? "データなし" :
        (!string.IsNullOrEmpty(e.Message) ? e.Message : e.Status==EstimateStatus.Reference ? "参考値" : "");
    private static string Position(AxisSample? s)=>s is null?"—":s.Value.Value.ToString("+0.000000;-0.000000;0.000000",CultureInfo.InvariantCulture);
    private static string Number(double? n)=>n is null?"—":n.Value.ToString("G9",CultureInfo.InvariantCulture);
    public static string Bits(double? n)=>StepPrecision.FormatBits(n);
    private static string Age(Gpss.Application.ApplicationSnapshot s,AxisSample? v)=>v is null?"—":$"{Math.Max(0,(s.Now-v.Value.Timestamp)/(double)s.Frequency):F2}秒前";
    private static void SetText(TextBlock target,string value) { if(target.Text!=value)target.Text=value; }
    private void UpdateDetailsText()
    {
        if(!_detailsDirty)return;
        _details.Text=$"X軸\n{Details(_cachedXAnalysis)}\n\nY軸\n{Details(_cachedYAnalysis)}";
        _detailsDirty=false;
    }
    private static TextBlock Text(string value,double size=13,bool bold=false)=>new() { Text=value,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new(0,5,0,5),Foreground=new SolidColorBrush(Color.FromRgb(15,23,42)) };
    private void ShowDetails(string title)
    {
        var owner=Window.GetWindow(this);
        var text=new TextBlock { Margin=new(16),TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas") };
        text.SetBinding(TextBlock.TextProperty,new Binding(nameof(TextBlock.Text)) { Source=_details });
        UpdateDetailsText();_detailsDialogVisible=true;
        var dialog=new Window
        {
            Icon=AppBranding.CreateWindowIcon(),
            Title=$"{title} — 平均ステップ精度の詳細", Width=520, Height=430,
            WindowStartupLocation=owner is null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner,
            Owner=owner,
            Content=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
                Content=text }
        };
        dialog.Closed+=(_,_)=>_detailsDialogVisible=false;
        dialog.ShowDialog();
    }
}
