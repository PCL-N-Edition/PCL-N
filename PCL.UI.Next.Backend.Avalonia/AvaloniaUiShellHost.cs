using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Owns the Avalonia application edge for the XSR product shell. The shell contract is supplied
/// by the composition root; this host only creates the native application and window.
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
    /// Starts the classic desktop lifetime and shows one shell window.
    /// </summary>
    public static int Run(XsrUiShell shell, string[]? args = null)
    {
        return Build(shell).StartWithClassicDesktopLifetime(args ?? []);
    }

    private static XsrUiShell CurrentShell =>
        _shell ?? throw new InvalidOperationException("The Avalonia shell host was not configured.");

    private sealed class ShellApplication : Application
    {
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new AvaloniaUiShellWindow(CurrentShell);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
