using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Owns the Avalonia application edge for the XSR product shell. The shell contract is supplied
/// by the composition root; this host only creates the native application and window, and runs
/// the legacy startup choreography: the splash appears immediately, the shell window shows
/// underneath it, and the splash fades once the shell window is on screen.
/// </summary>
public static class AvaloniaUiShellHost
{
    private static XsrUiShell? _shell;

    /// <summary>
    /// Builds an Avalonia app configured for the supplied shell. Keeping this separate from
    /// <see cref="Run"/> makes the composition edge testable without starting a native loop.
    /// </summary>
    public static AppBuilder Build(XsrUiShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        return AppBuilder.Configure<ShellApplication>().UsePlatformDetect();
    }

    /// <summary>
    /// Starts the classic desktop lifetime: splash first, then the shell window with the splash
    /// dismissing on top of it.
    /// </summary>
    public static int Run(XsrUiShell shell, string[]? args = null)
    {
        return Build(shell).StartWithClassicDesktopLifetime(args ?? []);
    }

    private static XsrUiShell CurrentShell =>
        _shell ?? throw new InvalidOperationException("The Avalonia shell host was not configured.");

    /// <summary>
    /// Resolves a brand asset embedded in the product assembly (the composition root owns the
    /// assets; this host only consumes streams). Missing assets disable the decoration that
    /// needed them — the splash is never a startup dependency.
    /// </summary>
    private static Stream? TryOpenProductAsset(string resourceName)
    {
        try
        {
            Assembly? product = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, "PCL.Desktop", StringComparison.Ordinal));
            return product?.GetManifestResourceStream(resourceName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class ShellApplication : Application
    {
        private AvaloniaSplashWindow? _splash;

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // The splash is the only window for a moment, so the lifetime must not treat it
                // as the process lifetime; the shell window becomes the main window below.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                Stream? icon = TryOpenProductAsset("PCL.Desktop.Assets.icon.png");
                if (icon is not null)
                {
                    _splash = new AvaloniaSplashWindow(icon);
                    desktop.MainWindow = _splash;
                    _splash.Show();
                }

                AvaloniaUiShellWindow shell = new(
                    CurrentShell,
                    TryOpenProductAsset("PCL.Desktop.Assets.icon.ico"));
                shell.Opened += OnShellWindowOpened;
                desktop.MainWindow = shell;
                shell.Show();

                if (_splash is not null)
                {
                    // Hard guarantee that the splash never outlives startup even if the Opened
                    // event is missed; the guarded dismiss makes the second call a no-op.
                    Dispatcher.UIThread.Post(DismissSplash, DispatcherPriority.Background);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void OnShellWindowOpened(object? sender, EventArgs e) => DismissSplash();

        private void DismissSplash()
        {
            AvaloniaSplashWindow? splash = _splash;
            if (splash is null)
            {
                return;
            }

            _splash = null;
            splash.DismissWithFade(TimeSpan.FromMilliseconds(AvaloniaMotionTokens.SplashFadeMilliseconds));
        }
    }
}
