using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The frameless startup splash: a small transparent always-on-top window showing the product
/// icon while the shell window initializes. It stays on top of the main window and fades out
/// linearly once the shell window has been shown, mirroring the legacy startup choreography.
/// </summary>
public sealed class AvaloniaSplashWindow : Window
{
    private readonly DispatcherTimer _fadeTimer;
    private readonly Stopwatch _fadeClock = new();
    private readonly Bitmap _icon;
    private TimeSpan _fade = TimeSpan.FromMilliseconds(AvaloniaMotionTokens.SplashFadeMilliseconds);
    private bool _dismissed;

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
        Opacity = 1;

        _icon = new Bitmap(iconStream);
        Content = new Image
        {
            Source = _icon,
            Width = 112,
            Height = 112,
            Stretch = Stretch.Uniform,
        };

        _fadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AvaloniaMotionTokens.FrameMilliseconds),
        };
        _fadeTimer.Tick += OnFadeTick;
    }

    /// <summary>
    /// Starts the single guarded linear fade-out. Repeat calls and calls after close are no-ops.
    /// </summary>
    public void DismissWithFade(TimeSpan fade)
    {
        if (_dismissed)
        {
            return;
        }

        _dismissed = true;
        _fade = fade;
        _fadeClock.Start();
        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        double elapsed = _fadeClock.Elapsed.TotalMilliseconds;
        if (elapsed >= _fade.TotalMilliseconds)
        {
            _fadeTimer.Stop();
            Close();
            return;
        }

        Opacity = 1 - (elapsed / _fade.TotalMilliseconds);
    }
}
