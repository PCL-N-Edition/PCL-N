using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace PCL.UI.Next.Backend.Avalonia;

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

        uint color = NoBorderColor;
        return DwmSetWindowAttribute(handle.Handle, BorderColorAttribute, ref color, sizeof(uint)) >= 0;
    }

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
