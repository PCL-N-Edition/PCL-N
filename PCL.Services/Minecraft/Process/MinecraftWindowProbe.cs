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
        if (processId <= 0 || !OperatingSystem.IsWindows())
        {
            return MinecraftWindowProbeResult.Unsupported;
        }

        // A first-frame window can take a moment to register a title; probe a few times before
        // giving up for this poll.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasVisibleWindowWindows(processId))
            {
                return MinecraftWindowProbeResult.Visible;
            }

            await Task.Delay(WindowTitleProbeDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        return MinecraftWindowProbeResult.NotVisible;
    }

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
