using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace PCL.Services.Tests;

// Xlib calls for the X11 probe ABI smoke test; Linux only, driven under xvfb-run in CI.
internal static partial class X11TestApi
{
    public const int PropModeReplace = 0;
    public const int XaCardinal = 6;

    public static nint XOpenDisplay(int displayName) => XOpenDisplayRaw(displayName);

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplayRaw(int displayName);

    [DllImport("libX11.so.6")]
    public static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    public static extern nuint XDefaultRootWindow(nint display);

    [DllImport("libX11.so.6")]
    public static extern nuint XCreateSimpleWindow(
        nint display, nuint parent, int x, int y, int width, int height, int borderWidth,
        nint border, nint background);

    [DllImport("libX11.so.6")]
    public static extern int XMapWindow(nint display, nuint window);

    [DllImport("libX11.so.6")]
    public static extern int XDestroyWindow(nint display, nuint window);

    [DllImport("libX11.so.6")]
    public static extern int XFlush(nint display);

    [SuppressMessage("Globalization", "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "Fixed ASCII atom name; ANSI charset is the exact wire format.")]
    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U8)]
    public static extern nuint XInternAtom(nint display, string name, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    public static extern int XChangeProperty(
        nint display, nuint window, nuint property, nuint type, int format, int mode,
        int[] data, int elementCount);

    [DllImport("libX11.so.6")]
    public static extern nint XBlackPixel(nint display, int screenNumber);

    [DllImport("libX11.so.6")]
    public static extern nint XWhitePixel(nint display, int screenNumber);
}
