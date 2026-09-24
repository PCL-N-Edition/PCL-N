using System.Runtime.InteropServices;

namespace Nexa.Services.Capabilities;

/// <summary>Native device presence, separate from recent input usage.</summary>
internal static partial class WindowsInputProbe
{
    internal static bool HasTouch(int flags) => (flags & 0x80) != 0 && (flags & 0x03) != 0;
    internal static bool HasPen(int flags) => (flags & 0x80) != 0 && (flags & 0x0c) != 0;

    internal static unsafe bool? KeyboardPresent()
    {
        if (!OperatingSystem.IsWindows()) return null;
        // Hot-plug may grow the list between the size and data calls. Retry, but never
        // turn an API failure into a claim that no keyboard exists.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, (uint)sizeof(Device)) == uint.MaxValue) return null;
            if (count == 0) return false;
            if (count > 65536) return null;
            Device[] devices = new Device[count];
            fixed (Device* buffer = devices)
            {
                uint read = GetRawInputDeviceList(buffer, ref count, (uint)sizeof(Device));
                if (read == uint.MaxValue) continue;
                for (int index = 0; index < read; index++)
                    if (devices[index].Type == 1) return true;
                return false;
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Device { public nint Handle; public uint Type; }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint GetRawInputDeviceList(Device* devices, ref uint count, uint size);
}
