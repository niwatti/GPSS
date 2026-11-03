using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Gpss.Core;

namespace Gpss.Desktop;

public sealed class AcquisitionSettingsWindow : Window
{
    private readonly TextBox _spin=new();
    private readonly TextBox _yield=new();
    private readonly TextBlock _error=new() { TextWrapping=TextWrapping.Wrap };
    public AcquisitionSettings Result { get; private set; }
    public AcquisitionSettingsWindow(AcquisitionSettings settings)
    {
        Result=settings;Title="取得設定";Width=520;Height=370;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
        Icon=AppBranding.CreateWindowIcon();
        _spin.Text=settings.BusyWaitMicroseconds.ToString(CultureInfo.InvariantCulture);
        _yield.Text=settings.LowLatencyYieldMicroseconds.ToString(CultureInfo.InvariantCulture);
        var root=new StackPanel { Margin=new(20) };
        root.Children.Add(new TextBlock { Text="取得は低遅延に固定しています。値を変更すると計測履歴をリセットします。設定は保存され、次回起動時に復元されます。",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12) });
        Add(root,"低遅延HIDの最大完了ポーリング（µs、0〜2000）",_spin);
        Add(root,"低遅延XInputの休止/譲歩（µs、0以上）",_yield);
        root.Children.Add(_error);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        var apply=new Button { Content="適用",IsDefault=true };apply.Click+=(_,_)=>Apply();buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content="取消",IsCancel=true });root.Children.Add(buttons);Content=root;
    }
    private void Apply()
    {
        if(!double.TryParse(_spin.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var spin)||!double.IsFinite(spin)||spin<0||spin>2000||
            !double.TryParse(_yield.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var yield)||!double.IsFinite(yield)||yield<0||yield>100000)
        { _error.Text="有限の数値を入力してください（HIDポーリングは最大2000µs、休止は最大100000µs）。";return; }
        Result=Result with { Mode=AcquisitionMode.LowLatency,BusyWaitMicroseconds=spin,LowLatencyYieldMicroseconds=yield };
        DialogResult=true;
    }
    private static void Add(Panel root,string label,TextBox input)
    { root.Children.Add(new TextBlock { Text=label });input.Margin=new(0,4,0,10);input.Padding=new(8,4,8,4);root.Children.Add(input); }
}
