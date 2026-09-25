using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static int RunNativeCornerSmoke()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native corner smoke requires Windows.");
        return AppBuilder.Configure<NativeCornerProbeApp>().UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface],
            }).StartWithClassicDesktopLifetime([]);
    }

    public sealed class NativeCornerProbeApp : Application
    {
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var shell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
                shell.Renderer.ReducedMotion = true;
                var window = new AvaloniaUiShellWindow(shell);
                desktop.MainWindow = window;
                window.Opened += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(300);
                        VerifyCompositedViewport(window, 850, 500);
                        window.Width += 100; window.Height += 50;
                        await Task.Delay(200);
                        VerifyCompositedViewport(window, 950, 550);
                        window.WindowState = WindowState.Maximized;
                        await Task.Delay(300);
                        VerifyNoRegion(window);
                        var root = (Control)window.Content!;
                        AssertEqual(root.Bounds.Size, window.Surface.Bounds.Size);
                        AssertEqual(new Point(0, 0), window.Surface.TranslatePoint(default, root)!.Value);
                        window.WindowState = WindowState.Normal;
                        await Task.Delay(300);
                        VerifyCompositedViewport(window, 950, 550);
                        window.WindowState = WindowState.Minimized;
                        await Task.Delay(200);
                        window.WindowState = WindowState.Normal;
                        await Task.Delay(300);
                        VerifyCompositedViewport(window, 950, 550);
                        window.TransparencyLevelHint = [WindowTransparencyLevel.None];
                        await Task.Delay(200);
                        AssertFalse(window.UsesCompositedEdges);
                        AssertEqual(((Control)window.Content!).Bounds.Size, window.Surface.Bounds.Size);
                        AssertTrue(Math.Abs(window.Surface.Bounds.Width - 950) <= 1 / window.RenderScaling);
                        AssertTrue(window.Background is LinearGradientBrush fallback && fallback.GradientStops.All(stop => stop.Color.A == 255));
                        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
                        await Task.Delay(200);
                        VerifyCompositedViewport(window, 950, 550);
                        Console.WriteLine("PASS: compositor without layered window or integer region; preserved scene and input origin; resize, maximize and minimize restoration");
                        desktop.Shutdown(0);
                    }
                    catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
                };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    private static void VerifyCompositedViewport(AvaloniaUiShellWindow window, double width, double height)
    {
        Console.WriteLine($"transparency={window.ActualTransparencyLevel}, layered={AvaloniaWindowsFrame.IsLayered(window)}, scene={window.Surface.Bounds.Size}");
        AssertTrue(window.UsesCompositedEdges);
        AssertFalse(AvaloniaWindowsFrame.IsLayered(window));
        VerifyNoRegion(window);
        // Native pixel bounds round fractional DIPs at scales such as 125%.
        AssertTrue(Math.Abs(width - window.Surface.Bounds.Width) <= 1 / window.RenderScaling);
        AssertTrue(Math.Abs(height - window.Surface.Bounds.Height) <= 1 / window.RenderScaling);
        var root = (Control)window.Content!;
        AssertEqual(new Size(window.Surface.Bounds.Width + 48, window.Surface.Bounds.Height + 48), root.Bounds.Size);
        AssertEqual(new Point(24, 24), window.Surface.TranslatePoint(default, root)!.Value);
        AssertEqual(new Point(0, 0), root.TranslatePoint(new Point(24, 24), window.Surface)!.Value);
        AssertTrue(window.Background is ISolidColorBrush { Color.A: 0 });
        var chrome = (Border)window.Surface.Parent!.Parent!;
        AssertEqual(new CornerRadius(24), chrome.CornerRadius);
        AssertTrue(chrome.Clip is RectangleGeometry { RadiusX: 24, RadiusY: 24 });
        AssertTrue(chrome.Background is LinearGradientBrush background && background.GradientStops.All(stop => stop.Color.A == 255));
    }
    private static void VerifyNoRegion(AvaloniaUiShellWindow window)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        try { AssertEqual(0, GetWindowRgn(window.TryGetPlatformHandle()!.Handle, region)); }
        finally { _ = DeleteObject(region); }
    }
    [LibraryImport("user32.dll")] private static partial int GetWindowRgn(nint window, nint region);
    [LibraryImport("gdi32.dll")] private static partial nint CreateRectRgn(int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint region);
}
