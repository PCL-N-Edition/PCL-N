using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Suppresses the Windows 11 DWM hairline without disabling native window animations.</summary>
internal static partial class AvaloniaWindowsFrame
{
    internal const int BorderColorAttribute = 34;
    internal const uint NoBorderColor = 0xFFFFFFFE;

    internal static bool SuppressBorder(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle
            || handle.Handle == 0) return false;

        uint corner = 1; // Explicit host geometry owns the radius, not the Win11 preset.
        _ = DwmSetWindowAttribute(handle.Handle, 33, ref corner, sizeof(uint));
        uint color = NoBorderColor;
        return DwmSetWindowAttribute(handle.Handle, BorderColorAttribute, ref color, sizeof(uint)) >= 0;
    }

    private sealed class ShapeState { public (int, int, int, int, int, int, int) Shape; public bool Applied; public bool Updating; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, ShapeState> Shapes = new();
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    internal static void ApplyCornerRadius(
        Window window,
        double radius,
        double? revealRadius = null,
        double? iconKeepAliveRadius = null)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle) return;
        var state = Shapes.GetOrCreateValue(window);
        if (state.Updating) return;
        state.Updating = true;
        try
        {
            if (revealRadius is null && window.WindowState is (WindowState.Maximized or WindowState.FullScreen))
            {
                if (state.Applied) { _ = SetWindowRgn(handle.Handle, 0, 1); state.Applied = false; }
                return;
            }
            if (GetWindowRect(handle.Handle, out var outer) == 0) return;
            // DWM frame bounds include native resize/decorations margins. The rounded
            // region must follow the actual rendered client surface, not that outer frame.
            if (window.Content is not Control content || content.Bounds.Width <= 0 || content.Bounds.Height <= 0) return;
            var origin = content.PointToScreen(default);
            int left = origin.X - outer.Left, top = origin.Y - outer.Top;
            int right = left + (int)Math.Round(content.Bounds.Width * window.RenderScaling);
            int bottom = top + (int)Math.Round(content.Bounds.Height * window.RenderScaling);
            int diameter = (int)Math.Round(radius * window.RenderScaling * 2);
            int reveal = revealRadius is { } r ? (int)Math.Ceiling(r * window.RenderScaling) : -1;
            int keepAlive = iconKeepAliveRadius is { } i ? (int)Math.Ceiling(i * window.RenderScaling) : -1;
            var shape = (left, top, right, bottom, diameter, reveal, keepAlive);
            if (state.Applied && state.Shape == shape) return;
            nint region;
            if (reveal >= 0)
            {
                // During reveal/collapse the native window follows the same circular mask as
                // the scene. Keep only a small disk behind the product icon when the content
                // radius reaches zero; the icon remains visible while the window body is gone.
                int cx = (left + right) / 2, cy = (top + bottom) / 2;
                int visibleRadius = Math.Max(1, reveal);
                region = CreateEllipticRgn(
                    cx - visibleRadius,
                    cy - visibleRadius,
                    cx + visibleRadius + 1,
                    cy + visibleRadius + 1);
                if (region != 0 && keepAlive > visibleRadius)
                {
                    nint icon = CreateEllipticRgn(
                        cx - keepAlive,
                        cy - keepAlive,
                        cx + keepAlive + 1,
                        cy + keepAlive + 1);
                    if (icon != 0) { _ = CombineRgn(region, region, icon, 2); _ = DeleteObject(icon); }
                }
            }
            else
            {
                region = CreateRoundRectRgn(left, top, right + 1, bottom + 1, diameter, diameter);
            }
            if (region == 0) return;
            if (SetWindowRgn(handle.Handle, region, 1) == 0) { _ = DeleteObject(region); return; }
            // Windows owns the region after a successful SetWindowRgn.
            state.Shape = shape; state.Applied = true;
        }
        finally { state.Updating = false; }
    }

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint window, out NativeRect rectangle);
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateEllipticRgn(int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(nint destination, nint first, nint second, int mode);
    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint value);
    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint window, nint region, int redraw);

    internal static void SetNonClientRendering(Window window, bool enabled)
    {
        if (!OperatingSystem.IsWindows()
            || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle) return;
        uint policy = enabled ? 2u : 1u; // DWMNCRP_ENABLED / DWMNCRP_DISABLED
        _ = DwmSetWindowAttribute(handle.Handle, 2, ref policy, sizeof(uint));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);
}
