namespace PCL.Services.Minecraft.Process;

public enum MinecraftWindowProbeResult
{
    /// <summary>This platform has no window detection wired; the caller skips waiting.</summary>
    Unsupported,

    /// <summary>Detection is supported and the process owns no visible window yet.</summary>
    NotVisible,

    /// <summary>The process owns a visible top-level window.</summary>
    Visible,
}

/// <summary>
/// Detects whether the launched game process owns a visible top-level window. The launch
/// pipeline uses this as the legacy "wait for window" confirmation: the narration stays honest
/// until the game has actually presented itself. Unsupported must be distinguishable from
/// not-visible, or platforms without a probe would stall the launch for the whole wait limit.
/// </summary>
public interface IMinecraftWindowProbe
{
    ValueTask<MinecraftWindowProbeResult> ProbeAsync(int processId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default probe. Windows enumerates top-level windows and matches the owner PID with a
/// non-empty visible title; other platforms report Unsupported so the launch skips the wait.
/// X11/Wayland/macOS probes can slot in behind the same contract later.
/// </summary>
public sealed class MinecraftWindowProbe : IMinecraftWindowProbe
{
    private const int WindowTitleProbeDelayMilliseconds = 500;

    public async ValueTask<MinecraftWindowProbeResult> ProbeAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            return MinecraftWindowProbeResult.Unsupported;
        }

        // A first-frame window can take a moment to register a title; probe a few times before
        // giving up for this poll. Wayland compositors do not expose a global window list, so
        // a Wayland session reports Unsupported and the launch skips the wait.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MinecraftWindowProbeResult probed;
            if (OperatingSystem.IsWindows())
            {
                probed = HasVisibleWindowWindows(processId)
                    ? MinecraftWindowProbeResult.Visible
                    : MinecraftWindowProbeResult.NotVisible;
            }
            else if (OperatingSystem.IsLinux() && !IsWaylandSession())
            {
                // null = no reachable X server (headless or Wayland-only): unsupported.
                bool? x11 = ProbeX11Linux(processId);
                probed = x11 == true
                    ? MinecraftWindowProbeResult.Visible
                    : x11 == false
                        ? MinecraftWindowProbeResult.NotVisible
                        : MinecraftWindowProbeResult.Unsupported;
            }
            else
            {
                probed = MinecraftWindowProbeResult.Unsupported;
            }

            if (probed == MinecraftWindowProbeResult.Visible
                || probed == MinecraftWindowProbeResult.Unsupported)
            {
                return probed;
            }

            await Task.Delay(WindowTitleProbeDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        return MinecraftWindowProbeResult.NotVisible;
    }

    private static bool IsWaylandSession() =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.Equals("wayland", StringComparison.OrdinalIgnoreCase) == true;

    private static bool? ProbeX11Linux(int processId)
    {
        nint display = XOpenDisplay(0);
        if (display == 0)
        {
            // No reachable X server (headless CI or a Wayland-only stack): the caller skips.
            return null;
        }

        try
        {
            nint root = XDefaultRootWindow(display);
            return OwnedVisibleWindowExists(display, root, (uint)processId);
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    private static bool OwnedVisibleWindowExists(nint display, nint window, uint pid)
    {
        if (IsViewableAndOwnedByPid(display, window, pid))
        {
            return true;
        }

        if (XQueryTree(display, window, out nint _, out nint _, out nint children, out uint count) == 0
            || children == 0)
        {
            return false;
        }

        try
        {
            for (uint index = 0; index < count; index++)
            {
                nint child = System.Runtime.InteropServices.Marshal.ReadIntPtr(children, (int)(index * nint.Size));
                if (OwnedVisibleWindowExists(display, child, pid))
                {
                    return true;
                }
            }
        }
        finally
        {
            _ = XFree(children);
        }

        return false;
    }

    private static bool IsViewableAndOwnedByPid(nint display, nint window, uint pid)
    {
        if (!IsWindowViewable(display, window))
        {
            return false;
        }

        nint pidAtom = XInternAtom(display, "_NET_WM_PID", false);
        if (pidAtom == 0)
        {
            return false;
        }

        if (XGetWindowProperty(
                display, window, pidAtom, 0, 64, false, XaCardinal,
                out nint actualType, out int actualFormat, out nint itemCount, out nint bytesAfter, out nint property) != 0
            || property == 0
            || itemCount < 1)
        {
            return false;
        }

        try
        {
            uint owner = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(property);
            return owner == pid;
        }
        finally
        {
            _ = XFree(property);
        }
    }

    private static bool IsWindowViewable(nint display, nint window)
    {
        // XWindowAttributes up to map_state: six ints, three pointers, six ints, one int,
        // one pointer, two ints on a 64-bit process.
        Span<int> attributes = stackalloc int[16];
        if (XGetWindowAttributes(display, window, attributes) == 0)
        {
            return false;
        }

        // map_state: 0 = IsUnviewable, 1 = IsUnmaped, 2 = IsViewable.
        return attributes[14] == 2;
    }

    private const int XaCardinal = 6;

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(int displayName);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern nint XDefaultRootWindow(nint display);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XQueryTree(
        nint display, nint window, out nint root, out nint parent, out nint children, out uint childCount);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XFree(nint data);

    // The analyzer flags the ANSI string even with explicit marshaling; the atom name is a
    // fixed ASCII literal, so the import is suppressed rather than re-marshaled.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization", "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "Fixed ASCII atom name; LPStr marshaling is already explicit.")]
    [System.Runtime.InteropServices.DllImport("libX11.so.6", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint XInternAtom(
        nint display,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStr)] string name,
        bool onlyIfExists);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XGetWindowAttributes(nint display, nint window, Span<int> attributes);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(
        nint display, nint window, nint property, long longOffset, long longLength, bool delete, nint requestedType,
        out nint actualType, out int actualFormat, out nint itemCount, out nint bytesAfter, out nint data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    private delegate bool EnumWindowsProc(nint window, nint lParam);

    private static bool HasVisibleWindowWindows(int processId)
    {
        bool found = false;
        EnumWindows((nint window, nint lParam) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0)
            {
                return true;
            }

            _ = GetWindowThreadProcessId(window, out uint owner);
            if (owner == (uint)processId)
            {
                found = true;
                return false;
            }

            return true;
        }, nint.Zero);
        return found;
    }
}
