using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
        window.StartupRevealCompleted += (_, _) => splash?.Close();
        if (splash is not null)
        {
            splash.Show();
        }

        desktop.MainWindow = window;
        window.Show();
        return window;
    }
}
