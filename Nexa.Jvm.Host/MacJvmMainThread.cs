using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexa.Jvm.Host;

[SupportedOSPlatform("macos")]
internal sealed partial class MacJvmMainThread : IDisposable
{
    private nint _pool;

    public MacJvmMainThread()
    {
        if (pthread_main_np() != 1) throw new InvalidOperationException("JVM must run on the macOS first thread.");
        _ = NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        nint allocated = Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc"));
        _pool = Send(allocated, sel_registerName("init"));
        if (_pool == 0) throw new InvalidOperationException("Cannot create Cocoa autorelease pool.");
        try { SetMarker("JAVA_STARTED_ON_FIRST_THREAD", "1"); }
        catch { Dispose(); throw; }
    }

    public static bool ConsumeLauncherOption(string option)
    {
        if (option == "-XstartOnFirstThread") return true;
        if (option.StartsWith("-Xdock:name=", StringComparison.Ordinal))
        { SetMarker("APP_NAME", option[12..]); return true; }
        if (option.StartsWith("-Xdock:icon=", StringComparison.Ordinal))
        { SetMarker("APP_ICON", option[12..]); return true; }
        return false;
    }

    private static void SetMarker(string prefix, string value)
    {
        string name = prefix + "_" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        if (setenv(name, value, 1) != 0) throw new InvalidOperationException("Cannot set JVM launcher environment.");
    }

    public void Dispose()
    {
        if (_pool == 0) return;
        SendVoid(_pool, sel_registerName("drain"));
        _pool = 0;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int pthread_main_np();
    [LibraryImport("/usr/lib/libSystem.B.dylib", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);
    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);
}
