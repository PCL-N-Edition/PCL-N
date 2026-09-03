using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The frameless startup splash: a small transparent always-on-top window showing the product
/// icon while the shell window initializes. It stays on top of the main window until the shell
/// window's circular reveal has finished — the window inherits the icon pixel-for-pixel at the
/// same position, so the splash simply closes and the icon continues animating in place.
/// </summary>
public sealed class AvaloniaSplashWindow : Window
{
    public AvaloniaSplashWindow(Stream iconStream)
    {
        ArgumentNullException.ThrowIfNull(iconStream);
        Width = 136;
        Height = 136;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        Content = new Image
        {
            Source = new Bitmap(iconStream),
            Width = 112,
            Height = 112,
            Stretch = Stretch.Uniform,
        };
    }
}
