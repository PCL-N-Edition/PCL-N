using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static int RunNativeCornerSmoke()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native corner smoke requires Windows.");
        return AppBuilder.Configure<NativeCornerProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
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
                        await Task.Delay(200);
                        VerifyClientCorners(window);
                        window.Width += 100; window.Height += 50;
                        await Task.Delay(100);
                        VerifyClientCorners(window);
                        window.WindowState = WindowState.Maximized;
                        await Task.Delay(200);
                        VerifyNoRegion(window);
                        window.WindowState = WindowState.Normal;
                        await Task.Delay(200);
                        VerifyClientCorners(window);
                        Console.WriteLine("PASS: all four client corners, edge centers, resize and maximize/restore");
                        desktop.Shutdown(0);
                    }
                    catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
                };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    private static void VerifyClientCorners(AvaloniaUiShellWindow window)
    {
        var content = (Control)window.Content!;
        nint handle = window.TryGetPlatformHandle()!.Handle;
        AssertTrue(GetWindowRect(handle, out var outer) != 0);
        var origin = content.PointToScreen(default);
        int left = origin.X - outer.Left, top = origin.Y - outer.Top;
        int right = left + (int)Math.Round(content.Bounds.Width * window.RenderScaling) - 1;
        int bottom = top + (int)Math.Round(content.Bounds.Height * window.RenderScaling) - 1;
        nint region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            AssertTrue(GetWindowRgn(handle, region) == 3);
            // These are content corners, deliberately not the larger HWND corners.
            foreach (var point in new[] { (left, top), (right, top), (left, bottom), (right, bottom) })
                AssertTrue(PtInRegion(region, point.Item1, point.Item2) == 0);
            foreach (var point in new[] { ((left + right) / 2, top), ((left + right) / 2, bottom), (left, (top + bottom) / 2), (right, (top + bottom) / 2) })
                AssertTrue(PtInRegion(region, point.Item1, point.Item2) != 0);
            AssertTrue(window.TransparencyLevelHint.All(level => level == WindowTransparencyLevel.None));
        }
        finally { _ = DeleteObject(region); }
    }
    private static void VerifyNoRegion(AvaloniaUiShellWindow window)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        try { AssertEqual(0, GetWindowRgn(window.TryGetPlatformHandle()!.Handle, region)); }
        finally { _ = DeleteObject(region); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CornerRect { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll")] private static partial int GetWindowRect(nint window, out CornerRect rectangle);
    [LibraryImport("user32.dll")] private static partial int GetWindowRgn(nint window, nint region);
    [LibraryImport("gdi32.dll")] private static partial nint CreateRectRgn(int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] private static partial int PtInRegion(nint region, int x, int y);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint region);
}
