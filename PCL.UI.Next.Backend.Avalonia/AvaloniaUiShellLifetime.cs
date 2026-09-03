using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The desktop lifetime contract for the product shell, extracted from the Application class so
/// tests can run it under a headless platform. The splash is pure decoration: it never becomes
/// the application main window and never owns the process lifetime. The shell window is the
/// main window, and the automatic <see cref="ShutdownMode.OnMainWindowClose"/> contract
/// terminates the process when it closes for real — including after the close-collapse
/// animation finishes.
/// </summary>
public static class AvaloniaUiShellLifetime
{
    public static AvaloniaUiShellWindow Compose(
        IClassicDesktopStyleApplicationLifetime desktop,
        XsrUiShell shell,
        Stream? splashIcon,
        Stream? windowIcon)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(shell);

        // Restoring the automatic lifetime contract up front is the whole point: with the
        // splash handling startup there is no window that should keep the process alive by
        // itself, and closing the shell window must always exit the process.
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        AvaloniaSplashWindow? splash = splashIcon is null ? null : new AvaloniaSplashWindow(splashIcon);
        AvaloniaUiShellWindow window = new(shell, windowIcon);
        if (splash is not null)
        {
            splash.Show();
            // Hard guarantee that a lost reveal event can never leave the topmost splash stuck
            // over the launcher: whichever side reaches the icon first closes it, and the
            // guarded close makes every other caller a no-op.
            DispatcherTimer fallback = new() { Interval = TimeSpan.FromSeconds(2) };
            bool splashClosed = false;
            void CloseSplash()
            {
                if (splashClosed)
                {
                    return;
                }

                splashClosed = true;
                fallback.Stop();
                splash.Close();
            }

            window.StartupRevealCompleted += (_, _) => CloseSplash();
            fallback.Tick += (_, _) => CloseSplash();
            fallback.Start();
        }

        desktop.MainWindow = window;
        window.Show();
        return window;
    }
}
