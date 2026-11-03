using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Gpss.Application;
using Gpss.Core;

namespace Gpss.Desktop;

public sealed class MainWindow : Window
{
    private readonly GpssController _controller;
    private readonly bool _demo;
    private readonly SettingsStore _store;
    private UserSettings _saved;
    private Task _pendingSave = Task.CompletedTask;
    private readonly string? _startupWarning;
    private readonly DispatcherTimer _timer=new() { Interval=TimeSpan.FromMilliseconds(17) };
    private readonly DispatcherTimer _performanceLogTimer=new() { Interval=TimeSpan.FromSeconds(5) };
    private readonly PerformanceJsonlWriter? _performanceLog;
    private readonly Process _process=Process.GetCurrentProcess();
    private readonly ComboBox _backend=new() { MinWidth=136, DisplayMemberPath="Label" };
    private readonly ComboBox _device=new() { MinWidth=220, DisplayMemberPath="Name" };
    private readonly ComboBox _windowPeriod=new() { MinWidth=92, DisplayMemberPath="Label", ToolTip="変更すると履歴をリセットし、選択した期間で蓄積し直します。" };
    private readonly TextBlock _mode=new() { Text="低遅延（固定）",VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBlock _method=new() { Text="bit数の平均",VerticalAlignment=VerticalAlignment.Center };
    private readonly Button _reset=new() { Content="計測をリセット" };
    private readonly Button _mapping=new() { Content="軸割当・生値" };
    private readonly Button _refresh=new() { Content="再検索" };
    private readonly Button _acquisitionSettings=new() { Content="取得設定" };
    private readonly TextBlock _status=new() { Text="機器を選択してください", TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock _metrics=new() { TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock _diagnostics=new() { TextWrapping=TextWrapping.Wrap };
    private bool _updating,_busy,_closed,_closing;
    private string? _notice;
    private long _lastStatusEpoch=long.MinValue,_lastDiagnosticAt=long.MinValue,_lastDispatcherTick;
    private long _lastLogTick,_lastAllocatedBytes,_lastGcPauseTicks,_lastProcessCpuTicks;
    private bool _performanceLogFailureShown;
    public DesktopPerformanceSnapshot UiPerformance => DesktopPerformanceMetrics.Capture();
    private AxisMapping _currentMapping=AxisMapping.Empty;
    private AcquisitionSettings _settings=new();
    private int _measurementWindowSeconds=MeasurementWindow.DefaultSeconds;
    public StickPanel LeftPanel { get; }=new("左スティック",Axis.LX,Axis.LY,new SolidColorBrush(Color.FromRgb(37,99,235)));
    public StickPanel RightPanel { get; }=new("右スティック",Axis.RX,Axis.RY,new SolidColorBrush(Color.FromRgb(15,118,110)));
    public StepHistogramControl StepHistogram { get; } = new();
    private sealed record Option<T>(string Label,T Value);

    public MainWindow(GpssController controller,bool demo=false,SettingsStore? settingsStore=null,string? performanceLogPath=null)
    {
        _controller=controller;_demo=demo;
        _store=settingsStore??new SettingsStore();var loaded=_store.Load();_saved=loaded.Settings;_startupWarning=loaded.Warning;
        _settings=_saved.Acquisition;_measurementWindowSeconds=_saved.MeasurementWindowSeconds;_controller.SetEstimator(_saved.EstimatorMethodId);
        if(!string.IsNullOrWhiteSpace(performanceLogPath))
        {
            try
            {
                _performanceLog=new PerformanceJsonlWriter(performanceLogPath);
                _lastLogTick=Stopwatch.GetTimestamp();_lastAllocatedBytes=GC.GetTotalAllocatedBytes(false);
                _lastGcPauseTicks=GC.GetTotalPauseDuration().Ticks;_lastProcessCpuTicks=_process.TotalProcessorTime.Ticks;
            }
            catch(Exception ex){_notice=$"性能ログを開始できません: {ex.Message}";}
        }
        Title="GPSS — Game Pad Stick Stats";
        Icon=AppBranding.CreateWindowIcon();
        Width=Math.Min(1600,SystemParameters.WorkArea.Width);Height=Math.Min(900,SystemParameters.WorkArea.Height);
        MinWidth=800;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterScreen;WindowState=WindowState.Maximized;
        Content=BuildDashboard();
        _updating=true;
        _backend.ItemsSource=new[] { new Option<BackendKind>("HID直接（推奨）",BackendKind.HidDirect),new("XInput（互換）",BackendKind.XInput),new("デモ",BackendKind.Simulation) };
        _backend.SelectedIndex=demo?2:(int)_saved.PreferredBackend;
        _windowPeriod.ItemsSource=MeasurementWindow.Options.Select(seconds=>new Option<int>(MeasurementWindow.Label(seconds),seconds)).ToArray();
        _windowPeriod.SelectedIndex=MeasurementWindow.Options.IndexOf(_measurementWindowSeconds);
        _updating=false;
        _backend.SelectionChanged+=async(_,_)=> { if(!_updating) await GuardAsync(async()=> { await _controller.ClearSelectionAsync(SelectedBackend);_currentMapping=AxisMapping.Empty;await RefreshAsync();await PersistAsync(); }); };
        _device.SelectionChanged+=async(_,_)=> { if(!_updating&&_device.SelectedItem is DeviceInfo device) await GuardAsync(()=>SelectAsync(device)); };
        _windowPeriod.SelectionChanged+=async(_,_)=>
        {
            if(!_updating&&_windowPeriod.SelectedItem is Option<int> period)
                await GuardAsync(async()=> { await _controller.SetMeasurementWindowAsync(period.Value);_measurementWindowSeconds=period.Value;await PersistAsync(); });
        };
        _refresh.Click+=async(_,_)=>await GuardAsync(RefreshAsync);
        _reset.Click+=async(_,_)=>await GuardAsync(()=>_controller.ResetAsync());
        _mapping.Click+=async(_,_)=>await GuardAsync(OpenMappingAsync);
        _acquisitionSettings.Click+=async(_,_)=>await GuardAsync(async()=> { var dialog=new AcquisitionSettingsWindow(_settings) { Owner=this };if(dialog.ShowDialog()==true) { _settings=dialog.Result; if(_controller.Current.Status.Device is not null) await _controller.SetModeAsync(_settings); await PersistAsync(); } });
        _timer.Tick+=(_,_)=>OnTimerTick();
        _performanceLogTimer.Tick+=(_,_)=>OnPerformanceLogTick();
        StateChanged+=(_,_)=>
        {
            if(WindowState==WindowState.Minimized){_timer.Stop();_lastDispatcherTick=0;}
            else if(IsLoaded&&!_closed&&!_closing){_lastDispatcherTick=0;_timer.Start();}
        };
        Loaded+=async(_,_)=> { await GuardAsync(async()=> { await _controller.SetMeasurementWindowAsync(_measurementWindowSeconds);await RefreshAsync();if(_demo&&_device.Items.Count>0) { _updating=true;_device.SelectedIndex=0;_updating=false;await SelectAsync((DeviceInfo)_device.Items[0],persist:false); } });_timer.Start();if(_performanceLog is not null)_performanceLogTimer.Start();if(_startupWarning is not null){_notice=_startupWarning;Render(_controller.Current);} };
        Closing+=OnClosing;
    }
    private BackendKind SelectedBackend=>(_backend.SelectedItem as Option<BackendKind>)?.Value??BackendKind.HidDirect;
    private void OnTimerTick()
    {
        if(WindowState==WindowState.Minimized)return;
        var start=Stopwatch.GetTimestamp();
        var interval=(long)(.017*Stopwatch.Frequency);
        var delay=_lastDispatcherTick==0?0:Math.Max(0,start-_lastDispatcherTick-interval);
        _lastDispatcherTick=start;
        Render(_controller.GetDisplaySnapshot());
        DesktopPerformanceMetrics.RecordUiUpdate(Stopwatch.GetTimestamp()-start,delay);
    }
    private void OnPerformanceLogTick()
    {
        if(_performanceLog is null)return;
        var nowTicks=Stopwatch.GetTimestamp();var elapsed=Math.Max(1,nowTicks-_lastLogTick);
        var allocated=GC.GetTotalAllocatedBytes(false);var pause=GC.GetTotalPauseDuration().Ticks;var cpu=_process.TotalProcessorTime.Ticks;
        var snapshot=_controller.GetDisplaySnapshot();var ui=DesktopPerformanceMetrics.Capture();var pipeline=snapshot.Performance;
        var sample=new
        {
            timestampUtc=DateTimeOffset.UtcNow,
            intervalSeconds=elapsed/(double)Stopwatch.Frequency,
            monotonicTicks=snapshot.Now,monotonicFrequency=snapshot.Frequency,
            epoch=snapshot.Status.Epoch,state=snapshot.Status.State.ToString(),
            analysisRevision=snapshot.AnalysisRevision,historyRevision=snapshot.HistoryRevision,
            analysisAgeMs=snapshot.Analysis is {} analysis&&snapshot.Frequency>0?Math.Max(0,(snapshot.Now-analysis.Timestamp)*1000d/snapshot.Frequency):(double?)null,
            widthDip=ActualWidth,heightDip=ActualHeight,dpiScaleX=VisualTreeHelper.GetDpi(this).DpiScaleX,dpiScaleY=VisualTreeHelper.GetDpi(this).DpiScaleY,
            uiRendering=WindowState==WindowState.Minimized?"suspended":"active",
            backend=snapshot.Backend.ToString(),mode=snapshot.Settings.Mode.ToString(),
            measurementWindowSeconds=snapshot.MeasurementWindowSeconds,
            observationHz=snapshot.ObservationRate,apiHz=snapshot.ApiCallRate,
            observationCount=snapshot.Diagnostics.Observations,apiCallCount=snapshot.Diagnostics.ApiCalls,droppedCount=snapshot.Diagnostics.Dropped,
            performanceLogDroppedRows=_performanceLog.DroppedRows,
            ui,
            pipeline,
            gc=new { allocatedBytesDelta=Math.Max(0,allocated-_lastAllocatedBytes),pauseMsDelta=Math.Max(0,pause-_lastGcPauseTicks)*1000d/TimeSpan.TicksPerSecond,
                gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2) },
            processCpuPercent=Math.Max(0,cpu-_lastProcessCpuTicks)*100d/TimeSpan.TicksPerSecond/(elapsed/(double)Stopwatch.Frequency)/Math.Max(1,Environment.ProcessorCount)
        };
        _lastLogTick=nowTicks;_lastAllocatedBytes=allocated;_lastGcPauseTicks=pause;_lastProcessCpuTicks=cpu;
        _performanceLog.TryWrite(sample);
        if(!_performanceLogFailureShown&&_performanceLog.Failure is string failure)
        {
            _performanceLogFailureShown=true;_notice=failure;
            SetText(_status,_notice);
        }
    }
    private async Task RefreshAsync()
    {
        var previous=(_device.SelectedItem as DeviceInfo)?.Id;
        var devices=await _controller.GetDevicesAsync(SelectedBackend);
        _updating=true;
        _device.ItemsSource=devices;
        _device.SelectedItem=devices.FirstOrDefault(d=>d.Id==previous);
        _updating=false;
        if(devices.Count==0) _notice=SelectedBackend==BackendKind.HidDirect?"HID機器が見つかりません。接続・権限を確認するかXInput候補を確認してください。":"接続された候補がありません。";
        if(_controller.DiscoveryWarnings.Count>0) _notice=string.Join(" / ",_controller.DiscoveryWarnings)+(_notice is null?"":" / "+_notice);
    }
    private async Task SelectAsync(DeviceInfo device,bool persist=true)
    {
        _notice=null;
        _currentMapping=SettingsStore.FindApplicableProfile(_saved,device)??SuggestMapping(device);
        await _controller.SelectAsync(device,_currentMapping,_settings);
        if(_currentMapping.Bindings.Length<4) _notice="4軸を確定できません。「軸割当・生値」で割り当ててください。";
        if(persist)await PersistAsync();
    }
    public static AxisMapping SuggestMapping(DeviceInfo device)=>SuggestedMapping.Create(device);
    private async Task OpenMappingAsync()
    {
        var device=_controller.Current.Status.Device??_device.SelectedItem as DeviceInfo;
        if(device is null) return;
        var dialog=new AxisMappingWindow(_controller,device,_currentMapping,_settings) { Owner=this };
        dialog.ShowDialog();
        if(dialog.AcceptedMapping is not null) { _currentMapping=dialog.AcceptedMapping;await PersistAsync(); }
        else await _controller.SelectAsync(device,_currentMapping,_settings);
    }
    private async Task PersistAsync()
    {
        var updated=_saved with { PreferredBackend=SelectedBackend, Acquisition=_settings,
            EstimatorMethodId=MeanBitStepEstimator.Id,MeasurementWindowSeconds=_measurementWindowSeconds,
            SelectedDeviceHint=(_device.SelectedItem as DeviceInfo)?.Id };
        var device=_device.SelectedItem as DeviceInfo;
        if(device is not null&&(device.Backend==BackendKind.XInput||DeviceFingerprint.FromDevice(device) is not null)&&_currentMapping.Bindings.Length==4)
            updated=SettingsStore.UpsertProfile(updated,device,_currentMapping);
        _saved=updated;
        try { _pendingSave=Task.Run(()=>_store.Save(updated));await _pendingSave; }
        catch(Exception ex) { _notice=$"設定を保存できません: {ex.Message}"; }
    }
    public void Render(ApplicationSnapshot snapshot)
    {
        if(WindowState==WindowState.Minimized)return;
        if(!_busy)
        {
            if(_lastStatusEpoch!=snapshot.Status.Epoch || snapshot.Status.State!=_lastStatusState || snapshot.Status.Message!=_lastStatusMessage || _notice!=_lastRenderedNotice)
            {
                _lastStatusEpoch=snapshot.Status.Epoch;_lastStatusState=snapshot.Status.State;_lastStatusMessage=snapshot.Status.Message;_lastRenderedNotice=_notice;
                SetText(_status,_notice??$"{StateName(snapshot.Status.State)}  {snapshot.Status.Message}");
            }
            SetEnabled(_reset,snapshot.Status.State==ConnectionState.Connected);
            SetEnabled(_mapping,snapshot.Status.Device is not null||_device.SelectedItem is DeviceInfo);
        }
        if(_lastDiagnosticAt==long.MinValue || snapshot.Now-_lastDiagnosticAt>=Math.Max(1,snapshot.Frequency/5))
        {
            _lastDiagnosticAt=snapshot.Now;
            var rateLabel=snapshot.Backend==BackendKind.XInput?"状態更新検出":"HIDレポート受信";
            if(snapshot.Backend==BackendKind.Simulation)rateLabel="デモ生成";
            var durationLabel=snapshot.ElapsedSeconds<snapshot.MeasurementWindowSeconds?
                $"収集中: {snapshot.ElapsedSeconds:F1} / {snapshot.MeasurementWindowSeconds:F1}秒":$"最新{MeasurementWindow.Label(snapshot.MeasurementWindowSeconds)}";
            var performance=snapshot.Performance;
            var analysisAge=performance is null||snapshot.Analysis is null||performance.AnalysisCompletedTicks<=0?"—":$"{AgeMilliseconds(snapshot,performance.AnalysisCompletedTicks):F0}ms";
            var metrics=$"{durationLabel}  •  {rateLabel}: {snapshot.ObservationRate:F1}/秒"+
                (snapshot.Backend==BackendKind.XInput?$"  •  API呼出し: {snapshot.ApiCallRate:F1}/秒":"")+
                $"  •  CPU（全CPU比）: {snapshot.CpuPercent:F1}%  •  既知の欠落: {snapshot.Diagnostics.Dropped:N0}";
            var apiAge=snapshot.Diagnostics.LastApiSuccess is long last?$"{Math.Max(0,(snapshot.Now-last)/(double)snapshot.Frequency):F2}秒前":"—";
            var pipeline=performance is null?"":$"  •  排出/解析: {performance.DrainDurationMs:F1}/{performance.AnalysisDurationMs:F1}ms  •  解析遅れ: {analysisAge}  •  キュー: {performance.QueueCount:N0} / {performance.QueueBytes:N0}B ({performance.QueueOldestAgeMs:F0}ms)  •  描画点: {performance.HistoryPointCount:N0}";
            var diagnostics=$"取得方式: {snapshot.Backend} / {snapshot.Settings.Mode}  •  最終API成功: {apiAge}  •  パース失敗: {snapshot.Diagnostics.ParseErrors:N0}  •  状態番号の非連番（参考）: {snapshot.Diagnostics.PotentialStateSkips:N0}"+pipeline;
            SetText(_metrics,metrics);SetText(_diagnostics,diagnostics);
        }
        LeftPanel.Render(snapshot);RightPanel.Render(snapshot);
        StepHistogram.Render(snapshot.Status.State == ConnectionState.Connected &&
            snapshot.StepHistogram?.Epoch == snapshot.Status.Epoch ? snapshot.StepHistogram : null);
    }
    private ConnectionState _lastStatusState;
    private string? _lastStatusMessage,_lastRenderedNotice;
    private static double AgeMilliseconds(ApplicationSnapshot snapshot,long timestamp)
        =>snapshot.Frequency<=0||timestamp<=0?0:Math.Max(0,(snapshot.Now-timestamp)*1000d/snapshot.Frequency);
    private static void SetText(TextBlock block,string value) { if(block.Text!=value)block.Text=value; }
    private static void SetEnabled(Control control,bool enabled) { if(control.IsEnabled!=enabled)control.IsEnabled=enabled; }
    private async Task GuardAsync(Func<Task> action)
    {
        if(_busy||_closed)return;
        _notice=null;_busy=true;_backend.IsEnabled=_device.IsEnabled=_windowPeriod.IsEnabled=_refresh.IsEnabled=_acquisitionSettings.IsEnabled=_mapping.IsEnabled=_reset.IsEnabled=false;
        try{await action();}catch(Exception ex){ShowError(ex);}finally { _busy=false;_backend.IsEnabled=_device.IsEnabled=_windowPeriod.IsEnabled=_refresh.IsEnabled=_acquisitionSettings.IsEnabled=true;Render(_controller.Current); }
    }
    private void ShowError(Exception ex) { _notice=$"操作エラー: {ex.Message}";_status.Text=_notice; }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_closed)return;
        e.Cancel=true;if(_closing)return;
        _closing=true;_timer.Stop();_performanceLogTimer.Stop();IsEnabled=false;_status.Text="入力を停止しています…";
        try
        {
            try { await _pendingSave; } catch { /* Already shown as a save warning. */ }
            var stopping=_controller.StopAsync();
            if(await Task.WhenAny(stopping,Task.Delay(2000))!=stopping)
                _status.Text="入力I/Oの終了を待っています。未完了バッファを保持し、安全に停止するまで待機します。";
            await stopping;await _controller.DisposeAsync();
            if(_performanceLog is not null)await _performanceLog.DisposeAsync();
            _closed=true;Close();
        }
        catch(Exception ex) { _closing=false;IsEnabled=true;ShowError(ex);_timer.Start(); }
    }
    private static string StateName(ConnectionState state)=>state switch { ConnectionState.Connected=>"計測中",ConnectionState.Searching=>"接続待ち",ConnectionState.Disconnected=>"未接続",ConnectionState.Unsupported=>"未対応",ConnectionState.Error=>"入力エラー",_=>"停止" };
    private UIElement BuildDashboard()
    {
        // Finite star rows keep both stick cards inside the client area. At high DPI,
        // scale the minimum usable canvas down as a whole rather than adding scrollbars.
        const double minimumWidth=1180,minimumHeight=920;
        var root=new Grid { Width=minimumWidth,Height=minimumHeight };
        root.RowDefinitions.Add(new() { Height=GridLength.Auto });
        root.RowDefinitions.Add(new() { Height=GridLength.Auto });
        root.RowDefinitions.Add(new() { Height=GridLength.Auto });
        root.RowDefinitions.Add(new() { Height=new(1,GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height=GridLength.Auto });
        root.RowDefinitions.Add(new() { Height=GridLength.Auto });

        var heading=new Grid { Margin=new(4,0,4,6) };
        heading.ColumnDefinitions.Add(new() { Width=GridLength.Auto });heading.ColumnDefinitions.Add(new());
        var title=new StackPanel { Orientation=Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text="GPSS",FontSize=23,FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text="スティックの位置・精度・震え",Foreground=Brushes.SlateGray,Margin=new(12,0,16,0),VerticalAlignment=VerticalAlignment.Center });
        heading.Children.Add(title);
        _status.TextWrapping=TextWrapping.NoWrap;_status.TextTrimming=TextTrimming.CharacterEllipsis;_status.VerticalAlignment=VerticalAlignment.Center;
        _status.SetBinding(ToolTipProperty,new Binding(nameof(TextBlock.Text)) { Source=_status });
        Grid.SetColumn(_status,1);heading.Children.Add(_status);root.Children.Add(heading);

        var controls=new Grid { Margin=new(4,0,4,6) };
        foreach(var width in new[] {1.2,2.0,.9,1.0,1.0})controls.ColumnDefinitions.Add(new() { Width=new(width,GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
        AddControl(controls,0,"取得方式",_backend);AddControl(controls,1,"コントローラー",_device);
        AddControl(controls,2,"計測・蓄積期間",_windowPeriod);AddControl(controls,3,"取得モード",_mode);AddControl(controls,4,"精度算出",_method);
        var actions=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Bottom };
        foreach(var button in new[] {_refresh,_acquisitionSettings,_mapping,_reset})
        {
            button.FontSize=12;button.Padding=new(8,5,8,5);button.Margin=new(3,0,0,0);button.MinHeight=32;actions.Children.Add(button);
        }
        Grid.SetColumn(actions,5);controls.Children.Add(actions);Grid.SetRow(controls,1);root.Children.Add(controls);

        var diagnostics=new StackPanel { Margin=new(4,0,4,4) };
        foreach(var text in new[] {_metrics,_diagnostics})
        {
            text.FontSize=12;text.TextWrapping=TextWrapping.NoWrap;text.TextTrimming=TextTrimming.CharacterEllipsis;
            text.SetBinding(ToolTipProperty,new Binding(nameof(TextBlock.Text)) { Source=text });diagnostics.Children.Add(text);
        }
        _diagnostics.Foreground=Brushes.SlateGray;Grid.SetRow(diagnostics,2);root.Children.Add(diagnostics);

        var panels=new Grid();panels.ColumnDefinitions.Add(new());panels.ColumnDefinitions.Add(new());
        panels.Children.Add(LeftPanel);Grid.SetColumn(RightPanel,1);panels.Children.Add(RightPanel);
        Grid.SetRow(panels,3);root.Children.Add(panels);
        Grid.SetRow(StepHistogram,4);root.Children.Add(StepHistogram);
        var footer=new TextBlock { Text="平均ステップ精度は選択期間内の各非ゼロ差分をbit換算した値の平均です。公称bit数の保証ではありません。+X: 右 / +Y: 下。\n"+
            "HIDは受信レポート、XInputは状態更新を記録。軸ごとの非ゼロ差分30個未満は参考値。取得は低遅延に固定しています。",
            FontSize=11,Foreground=Brushes.SlateGray,TextWrapping=TextWrapping.Wrap,Margin=new(4,4,4,0) };
        Grid.SetRow(footer,5);root.Children.Add(footer);

        var viewport=new Viewbox { Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,Child=root };
        viewport.SizeChanged+=(_,e)=>
        {
            if(e.NewSize.Width<=0||e.NewSize.Height<=0)return;
            var factor=Math.Max(1,Math.Max(minimumWidth/e.NewSize.Width,minimumHeight/e.NewSize.Height));
            root.Width=e.NewSize.Width*factor;root.Height=e.NewSize.Height*factor;
        };
        return new Border { Padding=new(10),Child=viewport };
    }
    private static void AddControl(Grid grid,int column,string label,FrameworkElement control)
    {
        var group=new StackPanel { Margin=new(0,0,8,0) };
        group.Children.Add(new TextBlock { Text=label,Foreground=Brushes.SlateGray,FontSize=11 });
        control.Margin=new(0,2,0,0);control.MinHeight=32;control.HorizontalAlignment=HorizontalAlignment.Stretch;
        group.Children.Add(control);Grid.SetColumn(group,column);grid.Children.Add(group);
    }
}
