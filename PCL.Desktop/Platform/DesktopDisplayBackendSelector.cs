// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Platform;

internal static class DesktopDisplayBackendSelector
{
    public static bool ShouldUseWaylandForCurrentProcess()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        return ShouldUseWayland(
            isLinux: true,
            isWindowsSubsystemForLinux: IsWindowsSubsystemForLinux(),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            File.Exists);
    }

    internal static bool ShouldUseWayland(
        bool isLinux,
        bool isWindowsSubsystemForLinux,
        string? waylandDisplay,
        string? xdgRuntimeDirectory,
        Func<string, bool> socketExists)
    {
        ArgumentNullException.ThrowIfNull(socketExists);

        // WSL and WSLg expose a mixture of Linux and Windows display state. X11/platform
        // detection is the compatible path there; registering Wayland would override
        // Avalonia's Windows/X11 selection and can prevent the process from starting.
        if (!isLinux || isWindowsSubsystemForLinux || string.IsNullOrWhiteSpace(waylandDisplay))
            return false;

        string display = waylandDisplay.Trim();
        string socketPath;
        if (Path.IsPathRooted(display))
        {
            socketPath = display;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
                return false;

            string runtimeDirectory = xdgRuntimeDirectory.Trim();
            if (!Path.IsPathRooted(runtimeDirectory))
                return false;

            socketPath = Path.Combine(runtimeDirectory, display);
        }

        try
        {
            return socketExists(socketPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsWindowsSubsystemForLinux()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP")))
        {
            return true;
        }

        return ContainsMicrosoftMarker("/proc/sys/kernel/osrelease") ||
               ContainsMicrosoftMarker("/proc/version");
    }

    private static bool ContainsMicrosoftMarker(string path)
    {
        try
        {
            return File.Exists(path) &&
                   File.ReadAllText(path).Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
