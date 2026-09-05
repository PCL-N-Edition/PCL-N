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
/// non-empty visible title. Linux drives Xlib over the same three-way contract: the X11 types
/// (Window, Atom, Colormap) are unsigned long on LP64, so the declarations use nuint and the
/// XWindowAttributes structure mirrors the native ABI exactly — Xlib writes the full struct,
/// and a compact or undersized managed buffer is a native stack overwrite. Wayland sessions
/// and headless environments report Unsupported (no reachable display or no global window
/// list), so the launch skips the wait instead of burning the limit.
/// </summary>
public sealed class MinecraftWindowProbe : IMinecraftWindowProbe
{
    private const int WindowTitleProbeDelayMilliseconds = 500;
    private const int MapStateViewable = 2;
    private const int XaCardinal = 6;

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
            nuint root = XDefaultRootWindow(display);
            return OwnedVisibleWindowExists(display, root, (nuint)processId);
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    internal static bool OwnedVisibleWindowExists(nint display, nuint window, nuint pid)
    {
        if (IsViewableAndOwnedByPid(display, window, pid))
        {
            return true;
        }

        if (XQueryTree(display, window, out nuint _, out nuint _, out nint children, out uint count) == 0
            || children == 0)
        {
            return false;
        }

        try
        {
            for (uint index = 0; index < count; index++)
            {
                nint child = System.Runtime.InteropServices.Marshal.ReadIntPtr(children, (int)(index * nint.Size));
                if (OwnedVisibleWindowExists(display, (nuint)child, pid))
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

    private static bool IsViewableAndOwnedByPid(nint display, nuint window, nuint pid)
    {
        if (XGetWindowAttributes(display, window, out XWindowAttributes attributes) == 0
            || attributes.MapState != MapStateViewable)
        {
            return false;
        }

        nuint pidAtom = XInternAtom(display, "_NET_WM_PID", false);
        if (pidAtom == 0)
        {
            return false;
        }

        if (XGetWindowProperty(
                display, window, pidAtom, 0, 64, false, XaCardinal,
                out nuint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out nint property) != 0
            || property == 0
            || itemCount < 1)
        {
            return false;
        }

        try
        {
            nuint owner = (nuint)System.Runtime.InteropServices.Marshal.ReadInt64(property);
            return owner == pid;
        }
        finally
        {
            _ = XFree(property);
        }
    }

    /// <summary>
    /// The LP64 Xlib ABI of XWindowAttributes, field for field: three pointers (Visual,
    /// Colormap) and two unsigned-long Windows (root) plus four unsigned longs (backing
    /// planes/pixel) and three longs (event masks) interleave the ints, so a compact int
    /// buffer would be a native stack overwrite, not a wrong field read.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int BorderWidth;
        public int Depth;

        public nint Visual;
        public nuint Root;

        public int Class;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;

        public nuint BackingPlanes;
        public nuint BackingPixel;

        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled;
        public int MapState;

        public nint AllEventMasks;
        public nint YourEventMasks;
        public nint DoNotPropagateMask;

        public int OverrideRedirect;
        public nint Screen;
    }

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(int displayName);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern nuint XDefaultRootWindow(nint display);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XQueryTree(
        nint display, nuint window, out nuint root, out nuint parent, out nint children, out uint childCount);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XFree(nint data);

    private static nuint XInternAtom(nint display, string name, bool onlyIfExists)
    {
        // Xlib expects a NUL-terminated C string; passing raw UTF-8 bytes sidesteps every
        // string-marshaling analyzer rule and matches the wire format exactly.
        byte[] terminated = new byte[System.Text.Encoding.UTF8.GetByteCount(name) + 1];
        _ = System.Text.Encoding.UTF8.GetBytes(name, 0, name.Length, terminated, 0);
        return XInternAtomBytes(display, terminated, onlyIfExists);
    }

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern nuint XInternAtomBytes(nint display, byte[] name, bool onlyIfExists);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XGetWindowAttributes(nint display, nuint window, out XWindowAttributes attributes);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(
        nint display, nuint window, nuint property, long longOffset, long longLength, bool delete, nuint requestedType,
        out nuint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out nint data);

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
