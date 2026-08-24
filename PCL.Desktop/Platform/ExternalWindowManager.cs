// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.InteropServices;

namespace PCL.Desktop.Platform;

internal enum ExternalWindowProbeResult
{
    Unsupported,
    NotFound,
    Found
}

/// <summary>
/// Queries the operating-system window manager for visible top-level windows
/// owned by an external process. Wayland intentionally has no portable global
/// window enumeration API, so unsupported platforms report that explicitly.
/// </summary>
internal static class ExternalWindowManager
{
    private const uint GetWindowOwner = 4;

    public static ExternalWindowProbeResult ProbeVisibleTopLevelWindow(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return ExternalWindowProbeResult.Unsupported;

        bool found = false;
        _ = EnumWindows((window, parameter) =>
        {
            _ = parameter;
            if (!IsWindowVisible(window) || GetWindow(window, GetWindowOwner) != 0)
                return true;

            _ = GetWindowThreadProcessId(window, out uint ownerProcessId);
            if (ownerProcessId != (uint)processId || !GetWindowRect(window, out NativeRect bounds))
                return true;

            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
                return true;

            found = true;
            return false;
        }, 0);

        return found ? ExternalWindowProbeResult.Found : ExternalWindowProbeResult.NotFound;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
}
