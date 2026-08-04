// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Best-effort native crash capture for the desktop host.
/// <list type="bullet">
/// <item>Windows: SEH unhandled filter + MiniDumpWriteDump (.dmp)</item>
/// <item>Unix: fatal signal handlers write a small crash note (.txt)</item>
/// </list>
/// Handlers avoid managed calls and keep work async-signal / fail-fast safe.
/// Full managed crash UI still runs on the next process via session markers.
/// </summary>
internal static unsafe class NativeCrashGuard
{
    private const int MiniDumpWithDataSegs = 0x00000001;
    private const int MiniDumpWithHandleData = 0x00000004;
    private const int MiniDumpWithThreadInfo = 0x00001000;
    private const int MiniDumpWithUnloadedModules = 0x00000020;

    // Linux / POSIX signal numbers (also valid on modern macOS for these fatal signals).
    private const int SigIll = 4;
    private const int SigTrap = 5;
    private const int SigAbrt = 6;
    private const int SigFpe = 8;
    private const int SigBus = 10;
    private const int SigSegv = 11;
    private const int SigSys = 12;

    private static int _installed;
    private static int _openWriteCreateTruncFlags;
    private static int _openWriteAppendFlags;
    private static byte[]? _dumpPathUtf8;
    private static byte[]? _notePathUtf8;
    private static byte[]? _sessionMarkerUtf8;
    private static string? _dumpPathManaged;
    private static string? _notePathManaged;

    /// <summary>Last prepared dump path for the current process (managed-safe).</summary>
    public static string? PreparedDumpPath => _dumpPathManaged;

    /// <summary>Last prepared crash-note path for the current process (managed-safe).</summary>
    public static string? PreparedNotePath => _notePathManaged;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;

        try
        {
            bool isMac = OperatingSystem.IsMacOS();
            // open(2) flags differ between Linux and Darwin.
            _openWriteCreateTruncFlags = isMac ? 0x601 : 577; // O_WRONLY|O_CREAT|O_TRUNC
            _openWriteAppendFlags = isMac ? 0x009 : 0x401; // O_WRONLY|O_APPEND

            string directory = Path.Combine(LauncherPathLayout.ResolveLogDirectory(), "Crashes");
            Directory.CreateDirectory(directory);
            string stamp =
                DateTimeOffset.Now.ToString(
                    "yyyyMMdd-HHmmss-fff",
                    System.Globalization.CultureInfo.InvariantCulture) +
                "-p" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (OperatingSystem.IsWindows())
            {
                _dumpPathManaged = Path.Combine(directory, "native-" + stamp + ".dmp");
                _dumpPathUtf8 = Encoding.UTF8.GetBytes(_dumpPathManaged + "\0");
                InstallWindowsFilter();
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                _notePathManaged = Path.Combine(directory, "native-" + stamp + ".txt");
                _notePathUtf8 = Encoding.UTF8.GetBytes(_notePathManaged + "\0");
                InstallUnixHandlers();
            }
        }
        catch
        {
            // Never block startup on crash-guard installation.
        }
    }

    /// <summary>
    /// Associate the managed abnormal-exit session marker so native handlers can
    /// append <c>nativeDump=</c> when they write a dump/note.
    /// </summary>
    public static void AttachSessionMarker(string? markerPath)
    {
        if (string.IsNullOrWhiteSpace(markerPath))
            return;
        try
        {
            _sessionMarkerUtf8 = Encoding.UTF8.GetBytes(markerPath + "\0");
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Find recent native dump/note files left by a prior process for inclusion
    /// in next-launch abnormal-exit reports.
    /// </summary>
    public static IReadOnlyList<string> FindRecentNativeArtifacts(TimeSpan maxAge, int maxCount = 5)
    {
        try
        {
            string directory = Path.Combine(LauncherPathLayout.ResolveLogDirectory(), "Crashes");
            if (!Directory.Exists(directory))
                return [];

            DateTime cutoff = DateTime.UtcNow - maxAge;
            return Directory.EnumerateFiles(directory, "native-*", SearchOption.TopDirectoryOnly)
                .Select(static path => new FileInfo(path))
                .Where(file => file.Exists && file.LastWriteTimeUtc >= cutoff)
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .Take(Math.Clamp(maxCount, 1, 20))
                .Select(static file => file.FullName)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static void InstallWindowsFilter()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _ = SetUnhandledExceptionFilter(&WindowsUnhandledExceptionFilter);
    }

    private static void InstallUnixHandlers()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return;

        nint handler = (nint)(delegate* unmanaged[Cdecl]<int, void>)&UnixSignalHandler;
        _ = signal(SigIll, handler);
        _ = signal(SigAbrt, handler);
        _ = signal(SigFpe, handler);
        _ = signal(SigBus, handler);
        _ = signal(SigSegv, handler);
        _ = signal(SigTrap, handler);
        _ = signal(SigSys, handler);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int WindowsUnhandledExceptionFilter(nint exceptionInfo)
    {
        try
        {
            WriteWindowsMiniDump(exceptionInfo);
            AppendNativePathToSessionMarker(_dumpPathUtf8);
        }
        catch
        {
            // Last-chance path: never throw out of the filter.
        }

        // EXCEPTION_CONTINUE_SEARCH = 0 → keep default crash behavior after dump.
        return 0;
    }

    private static void WriteWindowsMiniDump(nint exceptionInfo)
    {
        if (_dumpPathUtf8 is null || _dumpPathUtf8.Length == 0)
            return;

        fixed (byte* pathPtr = _dumpPathUtf8)
        {
            nint file = CreateFileA(
                pathPtr,
                0x40000000, // GENERIC_WRITE
                0x00000001, // FILE_SHARE_READ
                0,
                2, // CREATE_ALWAYS
                0x00000080, // FILE_ATTRIBUTE_NORMAL
                0);
            if (file is 0 or -1)
                return;

            try
            {
                MiniDumpExceptionInformation info = default;
                info.ThreadId = GetCurrentThreadId();
                info.ExceptionPointers = exceptionInfo;
                info.ClientPointers = 0;

                nint process = GetCurrentProcess();
                uint pid = GetCurrentProcessId();
                int type =
                    MiniDumpWithDataSegs |
                    MiniDumpWithHandleData |
                    MiniDumpWithThreadInfo |
                    MiniDumpWithUnloadedModules;
                _ = MiniDumpWriteDump(
                    process,
                    pid,
                    file,
                    type,
                    exceptionInfo == 0 ? 0 : (nint)(&info),
                    0,
                    0);
            }
            finally
            {
                _ = CloseHandle(file);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void UnixSignalHandler(int signo)
    {
        try
        {
            WriteUnixCrashNote(signo);
            AppendNativePathToSessionMarker(_notePathUtf8);
        }
        catch
        {
            // ignore
        }

        // Restore default disposition and re-raise so the OS can still core-dump.
        _ = signal(signo, 0); // SIG_DFL
        _ = raise(signo);
    }

    private static void WriteUnixCrashNote(int signo)
    {
        if (_notePathUtf8 is null || _notePathUtf8.Length == 0)
            return;

        fixed (byte* pathPtr = _notePathUtf8)
        {
            int fd = open(pathPtr, _openWriteCreateTruncFlags, 0x1A4); // 0644
            if (fd < 0)
                return;

            try
            {
                // Keep the payload ASCII and stack-allocated for signal safety.
                // Avoid managed APIs (Environment.ProcessId) here — use getpid(2).
                Span<byte> buffer = stackalloc byte[256];
                int length = 0;
                length = AppendAscii(buffer, length, "pcln-native-crash-v1\n");
                length = AppendAscii(buffer, length, "signal=");
                length = AppendInt(buffer, length, signo);
                length = AppendAscii(buffer, length, "\npid=");
                length = AppendInt(buffer, length, getpid());
                length = AppendAscii(buffer, length, "\n");
                _ = write(fd, buffer[..length]);
            }
            finally
            {
                _ = close(fd);
            }
        }
    }

    private static void AppendNativePathToSessionMarker(byte[]? artifactPathUtf8)
    {
        if (artifactPathUtf8 is null ||
            artifactPathUtf8.Length == 0 ||
            _sessionMarkerUtf8 is null ||
            _sessionMarkerUtf8.Length == 0)
        {
            return;
        }

        fixed (byte* markerPtr = _sessionMarkerUtf8)
        fixed (byte* artifactPtr = artifactPathUtf8)
        {
            int fd = open(markerPtr, _openWriteAppendFlags, 0);
            if (fd < 0)
                return;
            try
            {
                Span<byte> line = stackalloc byte[560];
                int length = 0;
                length = AppendAscii(line, length, "\nnativeDump=");
                for (int i = 0; i < artifactPathUtf8.Length - 1 && length < line.Length - 2; i++)
                {
                    byte b = artifactPathUtf8[i];
                    if (b == 0)
                        break;
                    line[length++] = b;
                }

                line[length++] = (byte)'\n';
                _ = write(fd, line[..length]);
            }
            finally
            {
                _ = close(fd);
            }
        }
    }

    private static int AppendAscii(Span<byte> buffer, int offset, string text)
    {
        for (int i = 0; i < text.Length && offset < buffer.Length; i++)
        {
            char c = text[i];
            buffer[offset++] = c < 128 ? (byte)c : (byte)'?';
        }

        return offset;
    }

    private static int AppendInt(Span<byte> buffer, int offset, int value)
    {
        if (value == 0)
        {
            if (offset < buffer.Length)
                buffer[offset++] = (byte)'0';
            return offset;
        }

        if (value < 0)
        {
            if (offset < buffer.Length)
                buffer[offset++] = (byte)'-';
            // Avoid overflow for int.MinValue in signal path — clamp.
            if (value == int.MinValue)
                value = int.MaxValue;
            else
                value = -value;
        }

        Span<byte> tmp = stackalloc byte[16];
        int n = 0;
        while (value > 0 && n < tmp.Length)
        {
            tmp[n++] = (byte)('0' + (value % 10));
            value /= 10;
        }

        while (n > 0 && offset < buffer.Length)
            buffer[offset++] = tmp[--n];
        return offset;
    }

    private static int write(int fd, Span<byte> buffer)
    {
        fixed (byte* ptr = buffer)
            return write(fd, ptr, (nuint)buffer.Length);
    }

    // --- Windows ---

    [StructLayout(LayoutKind.Sequential)]
    private struct MiniDumpExceptionInformation
    {
        public uint ThreadId;
        public nint ExceptionPointers;
        public int ClientPointers;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint SetUnhandledExceptionFilter(delegate* unmanaged[Stdcall]<nint, int> filter);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateFileA(
        byte* fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern int MiniDumpWriteDump(
        nint process,
        uint processId,
        nint file,
        int dumpType,
        nint exceptionParam,
        nint userStreamParam,
        nint callbackParam);

    // --- Unix libc (libSystem on macOS resolves "libc") ---

    [DllImport("libc", EntryPoint = "signal", SetLastError = true)]
    private static extern nint signal(int signum, nint handler);

    [DllImport("libc", EntryPoint = "raise", SetLastError = true)]
    private static extern int raise(int sig);

    [DllImport("libc", EntryPoint = "getpid", SetLastError = true)]
    private static extern int getpid();

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(byte* path, int flags, int mode);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern int write(int fd, byte* buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);
}
