namespace PCL.Services.Minecraft.Process;

/// <summary>
/// Detects whether the launched game process owns a visible top-level window. The launch
/// pipeline uses this as the legacy "wait for window" confirmation: the narration stays honest
/// until the game has actually presented itself.
/// </summary>
public interface IMinecraftWindowProbe
{
    ValueTask<bool> HasVisibleWindowAsync(int processId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default probe. Windows enumerates top-level windows and matches the owner PID with a
/// non-empty visible title; other platforms report false and the pipeline's wait stage times
/// out into the launched state instead of blocking the flow forever.
/// </summary>
public sealed class MinecraftWindowProbe : IMinecraftWindowProbe
{
    private const int WindowTitleProbeDelayMilliseconds = 500;

    public async ValueTask<bool> HasVisibleWindowAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            return false;
        }

        // A first-frame window can take a moment to register a title; probe a few times before
        // giving up for this poll.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows() && HasVisibleWindowWindows(processId))
            {
                return true;
            }

            await Task.Delay(WindowTitleProbeDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        return false;
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
