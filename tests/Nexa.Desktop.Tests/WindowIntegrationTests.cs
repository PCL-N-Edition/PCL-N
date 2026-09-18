using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Nexa.Desktop.Ui;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void WindowPropertyStoreRoundTripsAppId()
    {
        if (!OperatingSystem.IsWindows()) return;
        int initialized = WindowApi.CoInitializeEx(0, 0);
        Marshal.ThrowExceptionForHR(initialized);
        nint window = WindowApi.CreateWindowExW(0, "STATIC", "Nexa AUMID test", 0x10000000, -32000, -32000, 20, 20, 0, 0, 0, 0);
        try
        {
            AssertTrue(window != 0);
            AssertTrue(MinecraftWindowIntegration.ApplyAppUserModelId(new nint(-1), "Nexa.InvalidWindow") is not null);
            const string appId = "Nexa.Nexa.Tests.GameWindow";
            MinecraftWindowIntegration.DetachGameWindows(Environment.ProcessId, appId);
            Guid iid = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
            Marshal.ThrowExceptionForHR(WindowApi.SHGetPropertyStoreForWindow(window, ref iid, out nint pointer));
            try
            {
                var wrappers = new StrategyBasedComWrappers();
                var store = (MinecraftWindowIntegration.IPropertyStore)wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
                MinecraftWindowIntegration.PropertyKey key = new(new Guid("9F4C285F-C90D-11D2-9D8B-2ED9F57BD72F"), 5);
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
                try
                {
                    AssertEqual((short)31, value.ValueType);
                    AssertEqual(appId, Marshal.PtrToStringUni(value.Pointer));
                    AssertEqual(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<MinecraftWindowIntegration.PropVariant>());
                }
                finally
                {
                    _ = WindowApi.PropVariantClear(ref value);
                    MinecraftWindowIntegration.PropVariant empty = default;
                    Marshal.ThrowExceptionForHR(store.SetValue(in key, in empty));
                }
            }
            finally { Marshal.Release(pointer); }
        }
        finally
        {
            if (window != 0) _ = WindowApi.DestroyWindow(window);
            WindowApi.CoUninitialize();
        }
    }

    private static class WindowApi
    {
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved, uint flags);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern nint CreateWindowExW(uint extended, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
        [DllImport("shell32.dll")] internal static extern int SHGetPropertyStoreForWindow(nint window, ref Guid iid, out nint pointer);
        [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref MinecraftWindowIntegration.PropVariant value);
    }
}
