using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Gpss.Application;
using Gpss.Core;

namespace Gpss.Desktop;

/// <summary>Explicit temporary mappings let users inspect arbitrary HID axes without guessing offsets.</summary>
public sealed class AxisMappingWindow : Window
{
    private readonly GpssController _controller;
    private readonly DeviceInfo _device;
    private readonly AcquisitionSettings _settings;
    private readonly Dictionary<Axis,ComboBox> _selectors=new();
    private readonly Dictionary<Axis,CheckBox> _inversions=new();
    private readonly Dictionary<Axis,TextBlock> _readings=new();
    private readonly TextBlock _message=new() { TextWrapping=TextWrapping.Wrap,Margin=new(4,10,4,10) };
    private readonly DispatcherTimer _timer=new() { Interval=TimeSpan.FromMilliseconds(100) };
    private AxisMapping? _preview;
    private bool _busy;
    public AxisMapping? AcceptedMapping { get; private set; }
    private sealed record DescriptorOption(AxisDescriptor Descriptor)
    {
        public override string ToString()=> $"{Descriptor.Name} [{Descriptor.Key}]  {Descriptor.LogicalMin}..{Descriptor.LogicalMax}"+
            (Descriptor.Usage is ushort usage?$" Usage=0x{usage:X2} / ID={Descriptor.ReportId} / LC={Descriptor.LinkCollection}":"");
    }
    public AxisMappingWindow(GpssController controller,DeviceInfo device,AxisMapping existing,AcquisitionSettings settings)
    {
        _controller=controller;_device=device;_settings=settings;
        Icon=AppBranding.CreateWindowIcon();
        Title="軸割当・生値の確認";Width=960;Height=640;MinWidth=640;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel { Margin=new(20) };
        root.Children.Add(new TextBlock { Text=device.Name,FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text="候補を選び「生値をプレビュー」で実際に動く軸を確認してください。プレビュー・適用・取消時に計測履歴をリセットします。\nHIDの割当は機器と軸情報が一致する場合に復元します。XInputのslotは毎回確認してください。トリガーや別Report IDを右スティックと誤認しないよう確認してください。",TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,16) });
        var options=device.Axes.Where(d=>d.Supported&&d.IsAbsolute).Select(d=>new DescriptorOption(d)).ToArray();
        foreach(var axis in Enum.GetValues<Axis>())
        {
            var row=new Grid { Margin=new(0,5,0,5) };
            row.ColumnDefinitions.Add(new() { Width=new(45) });row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new() { Width=new(80) });row.ColumnDefinitions.Add(new() { Width=new(190) });
            row.Children.Add(new TextBlock { Text=axis.ToString(),VerticalAlignment=VerticalAlignment.Center });
            var combo=new ComboBox { ItemsSource=options,MinWidth=280,Margin=new(4),SelectedIndex=-1 };
            var old=existing.Bindings.FirstOrDefault(b=>b.Axis==axis);
            combo.SelectedItem=options.FirstOrDefault(o=>o.Descriptor.Key==old?.DescriptorKey);
            Grid.SetColumn(combo,1);row.Children.Add(combo);_selectors[axis]=combo;
            var invert=new CheckBox { Content="反転",IsChecked=old?.Inverted??false,VerticalAlignment=VerticalAlignment.Center,Margin=new(8) };
            Grid.SetColumn(invert,2);row.Children.Add(invert);_inversions[axis]=invert;
            var reading=new TextBlock { Text="生値: —",VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap };
            Grid.SetColumn(reading,3);row.Children.Add(reading);_readings[axis]=reading;
            root.Children.Add(row);
        }
        root.Children.Add(_message);
        var buttons=new WrapPanel();
        var preview=new Button { Content="生値をプレビュー" };preview.Click+=async(_,_)=>await ApplyPreviewAsync(false);buttons.Children.Add(preview);
        var accept=new Button { Content="この割当を適用" };accept.Click+=async(_,_)=>await ApplyPreviewAsync(true);buttons.Children.Add(accept);
        var cancel=new Button { Content="取消",IsCancel=true };buttons.Children.Add(cancel);root.Children.Add(buttons);
        var unsupported=device.Axes.Where(a=>!a.Supported).Select(a=>$"{a.Name}: {a.UnsupportedReason}");
        root.Children.Add(new TextBlock { Text="\n"+string.Join("\n",unsupported),TextWrapping=TextWrapping.Wrap });
        Content=new ScrollViewer { Content=root,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        _timer.Tick+=(_,_)=>RenderReadings();Loaded+=(_,_)=>_timer.Start();Closed+=(_,_)=>_timer.Stop();Closing+=(_,e)=> { if(_busy&&AcceptedMapping is null)e.Cancel=true; };
    }
    public async Task ApplyPreviewAsync(bool accept)
    {
        if(_busy)return;
        var bindings=ImmutableArray.CreateBuilder<AxisBinding>();
        foreach(var axis in Enum.GetValues<Axis>())
        {
            if(_selectors[axis].SelectedItem is not DescriptorOption selected) { _message.Text=$"{axis}の候補を選択してください。";return; }
            bindings.Add(new(axis,selected.Descriptor.Key,_inversions[axis].IsChecked==true));
        }
        _busy=true;IsEnabled=false;
        try
        {
            var mapping=new AxisMapping(bindings.ToImmutable());
            SuggestedMapping.Validate(_device,mapping);
            await _controller.SelectAsync(_device,mapping,_settings);
            _preview=mapping;
            if(accept) { AcceptedMapping=mapping;DialogResult=true; }
            else _message.Text="選択した軸を動かしてください。右欄は元整数値と正規化値です。";
        }
        catch(Exception ex){_message.Text=$"プレビュー失敗: {ex.Message}";}
        finally {_busy=false;IsEnabled=true;}
    }
    private void RenderReadings()
    {
        var snapshot=_controller.Current;
        foreach(var axis in Enum.GetValues<Axis>())
        {
            var selected=(_selectors[axis].SelectedItem as DescriptorOption)?.Descriptor.Key;
            var binding=_preview?.Bindings.FirstOrDefault(b=>b.Axis==axis);
            if(binding is null||binding.DescriptorKey!=selected||binding.Inverted!=(_inversions[axis].IsChecked==true)||!snapshot.Latest.TryGetValue(axis,out var sample)||sample.Epoch!=snapshot.Status.Epoch)
            { _readings[axis].Text="生値: —（プレビュー待ち）";continue; }
            var age=Math.Max(0,(snapshot.Now-sample.Timestamp)/(double)snapshot.Frequency);
            _readings[axis].Text=$"生値: {sample.RawValue}\n正規化: {sample.Value:+0.000000;-0.000000;0.000000} / {age:F2}秒前";
        }
    }
}
