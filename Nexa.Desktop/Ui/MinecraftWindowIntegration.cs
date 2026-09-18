using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Nexa.Desktop.Ui;

/// <summary>
/// Window-manager integration for the launched Minecraft process. The game window must be a
/// separate taskbar application — never grouped under the launcher — while keeping the game's
/// own (grass block) icon: the per-window AppUserModelID breaks the launcher group without
/// relabeling the window, which an icon-overriding launcher AUMID would do.
/// </summary>
internal static partial class MinecraftWindowIntegration
{
    /// <summary>
    /// Assigns a per-launch AppUserModelID to the game's top-level windows so the taskbar
    /// never groups them with the launcher. The property is written through the shell
    /// property store scoped to each game window, so the game keeps its own icon.
    /// </summary>
    public static void DetachGameWindows(int processId, string appId, Action<string>? warn = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DetachGameWindowsWindows(processId, appId, warn);
    }

    [SupportedOSPlatform("windows")]
    private static void DetachGameWindowsWindows(int processId, string appId, Action<string>? warn)
    {
        List<string> failures = [];
        bool enumerated = EnumWindows((window, lParam) =>
        {
            _ = GameWindowOwnerPid(window, out uint owner);
            if (owner != (uint)processId || !IsWindowVisible(window) || GetWindowTextLength(window) == 0)
            {
                return true;
            }

            if (ApplyAppUserModelId(window, appId) is { } failure)
                failures.Add($"AUMID write failed pid={processId} hwnd=0x{window:X} hr=0x{failure.HResult:X8}: {failure.Message}");
            return true;
        }, nint.Zero);
        if (!enumerated)
            failures.Add($"Game window enumeration failed pid={processId} win32={Marshal.GetLastWin32Error()}");
        // Never call host code across the unmanaged EnumWindows callback boundary.
        foreach (string failure in failures) warn?.Invoke(failure);
    }

    [SupportedOSPlatform("windows")]
    internal static Exception? ApplyAppUserModelId(nint window, string appId)
    {
        try
        {
            Guid storeGuid = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref storeGuid, out nint storePtr));
            if (storePtr == 0) return new InvalidOperationException("The window property store returned a null pointer.");

            try
            {
                IPropertyStore store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(storePtr, CreateObjectFlags.None);
                PropVariant variant = new() { ValueType = 31, Pointer = Marshal.StringToCoTaskMemUni(appId) };

                try
                {
                    Marshal.ThrowExceptionForHR(store.SetValue(in AppUserModelIdKey, in variant));
                    Marshal.ThrowExceptionForHR(store.Commit());
                }
                finally
                {
                    _ = PropVariantClear(ref variant);
                }
            }
            finally
            {
                Marshal.Release(storePtr);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Report after returning to managed code, while keeping enumeration best-effort.
            return exception;
        }
        return null;
    }

    private static PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C285F-C90D-11D2-9D8B-2ED9F57BD72F"), 5);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;

        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public short ValueType;
        [FieldOffset(8)] public nint Pointer;
        // The native union contains a count + pointer, making PROPVARIANT 24 bytes
        // on x64/arm64 and 16 on x86 even when only VT_LPWSTR is being written.
        [FieldOffset(8)] public CountedPointer Storage;
    }

    /// <summary>Minimal IPropertyStore surface: SetValue + Commit are all the AUMID write needs.</summary>
    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(in PropertyKey key, in PropVariant value);
        [PreserveSig] int Commit();
    }

    private static readonly StrategyBasedComWrappers Wrappers = new();
    [StructLayout(LayoutKind.Sequential)]
    internal struct CountedPointer { public uint Count; public nint Pointer; }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint window, ref Guid interfaceId, out nint propertyStore);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsProc(nint window, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GameWindowOwnerPid(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);
}
