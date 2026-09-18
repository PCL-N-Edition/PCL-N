using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Blittable Objective-C calls, compatible with NativeAOT on x64 and arm64.</summary>
internal static partial class AvaloniaMacWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public double X, Y, Width, Height; }

    internal static double ConfigureAndMeasure(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { } handle) return 0;
        nint native = handle.HandleDescriptor == "NSWindow" ? handle.Handle
            : handle.HandleDescriptor == "NSView" ? Send(handle.Handle, Selector("window")) : 0;
        if (native == 0) return 82; // Conservative safe area until a native handle becomes available.
        SendBool(native, Selector("setTitlebarAppearsTransparent:"), 1);
        SendInteger(native, Selector("setTitleVisibility:"), 1);
        SendBool(native, Selector("setMovableByWindowBackground:"), 0);
        double right = 0;
        for (nint kind = 0; kind < 3; kind++)
        {
            nint button = SendIntegerResult(native, Selector("standardWindowButton:"), kind);
            if (button == 0) continue;
            NativeRect frame;
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                RectStret(out frame, button, Selector("frame"));
            else frame = Rect(button, Selector("frame"));
            right = Math.Max(right, frame.X + frame.Width);
        }
        return right > 0 ? right + 12 : 82;
    }
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendBool(nint target, nint selector, byte value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendInteger(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendIntegerResult(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial NativeRect Rect(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    private static partial void RectStret(out NativeRect result, nint target, nint selector);
}
