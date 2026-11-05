using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Threadbook.Desktop;

internal static class ApplicationBrand
{
    private static readonly Lazy<BitmapFrame> Icon = new(() => Load("Threadbook.ico"));
    private static readonly Lazy<BitmapFrame> Header = new(() => Load("Threadbook-32.png"));
    internal static ImageSource Mark => Header.Value;
    internal static void Apply(Window window) => window.Icon = Icon.Value;
    private static BitmapFrame Load(string file)
    {
        var frame = BitmapFrame.Create(new Uri($"pack://application:,,,/Threadbook;component/Assets/{file}"), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
