using System.Windows;
using Gpss.Application;
using Gpss.Core;
using Gpss.Input;
using Gpss.Input.HidDirect;
using Gpss.Input.XInput;
using Gpss.Input.Simulation;

namespace Gpss.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IInputSource Factory(BackendKind kind) => kind switch
        {
            BackendKind.HidDirect => new HidDirectInputSource(),
            BackendKind.XInput => new XInputSource(),
            BackendKind.Simulation => new SimulationInputSource(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        string? performanceLogPath=null;
        for(var i=0;i<e.Args.Length;i++)
        {
            if(e.Args[i].StartsWith("--performance-log=",StringComparison.OrdinalIgnoreCase))
                performanceLogPath=e.Args[i]["--performance-log=".Length..];
            else if(string.Equals(e.Args[i],"--performance-log",StringComparison.OrdinalIgnoreCase))
            {
                if(i+1>=e.Args.Length||e.Args[i+1].StartsWith("--",StringComparison.Ordinal))
                {
                    MessageBox.Show("--performance-log の後にログ保存先を指定してください。", "GPSS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown(2);return;
                }
                performanceLogPath=e.Args[++i];
            }
        }
        if(performanceLogPath is not null && string.IsNullOrWhiteSpace(performanceLogPath))
        {
            MessageBox.Show("性能ログの保存先を空にはできません。", "GPSS", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);return;
        }
        var controller = new GpssController(Factory);
        controller.Start();
        MainWindow = new MainWindow(controller, e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase),performanceLogPath:performanceLogPath);
        MainWindow.Show();
    }
}
