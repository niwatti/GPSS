using System.Windows.Media.Imaging;

namespace Gpss.Desktop;

public static class AppBranding
{
    /// <summary>Loads the multi-resolution ICO from the assembly, including in a single-file deployment.</summary>
    public static BitmapFrame CreateWindowIcon()
    {
        var icon = BitmapFrame.Create(
            new Uri("pack://application:,,,/Gpss.Desktop;component/Assets/gpss.ico", UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        icon.Freeze();
        return icon;
    }
}
