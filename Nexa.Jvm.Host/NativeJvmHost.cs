using System.Runtime.InteropServices;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Jvm.Host;

/// <summary>Only invoked in an isolated --jvm-host process, before application composition.</summary>
internal static unsafe class NativeJvmHost
{
    public static int Run()
    {
        if (OperatingSystem.IsMacOS()) return RunCore();
        int result = 1;
        Thread worker = new(() => result = RunCore(), 4 * 1024 * 1024);
        worker.Start();
        worker.Join();
        return result;
    }

    private static int RunCore()
    {
        try
        {
            using Stream input = Console.OpenStandardInput();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            JvmHostBootstrapRequest request = JvmHostBootstrap.ReadAsync(input, timeout.Token).AsTask().GetAwaiter().GetResult();
            Console.Error.WriteLine("Nexa JVM Host: bootstrap received");
            if (!OperatingSystem.IsMacOS()) return Execute(request);
            using MacJvmMainThread scope = new();
            return Execute(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Fixed messages preserve useful diagnosis without logging paths, arguments or tokens.
            string reason = exception switch
            {
                FileNotFoundException or DllNotFoundException => "JVM library missing or unloadable (jvm.dll/libjvm.so/libjvm.dylib).",
                BadImageFormatException => "Java runtime architecture is incompatible with the JVM host.",
                UnauthorizedAccessException => "Access is denied when preparing the JVM host.",
                DirectoryNotFoundException => "Game working directory is missing.",
                InvalidDataException or EndOfStreamException => "Invalid or incomplete JVM host bootstrap request.",
                OperationCanceledException => "JVM host bootstrap timed out.",
                _ => "JVM host initialization failed.",
            };
            Console.Error.WriteLine($"Nexa JVM Host Error: {reason} ({exception.GetType().Name})");
            return 1;
        }
    }

    private static int Execute(JvmHostBootstrapRequest request)
    {
        if (!Path.IsPathFullyQualified(request.JavaExecutable) || !File.Exists(request.JavaExecutable))
            throw new FileNotFoundException("A concrete Java runtime is required.");
        string executable = new FileInfo(request.JavaExecutable).ResolveLinkTarget(true)?.FullName ?? request.JavaExecutable;
        string bin = Path.GetDirectoryName(executable)!;
        string home = Path.GetDirectoryName(bin)!;
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(bin, "server", "jvm.dll"), Path.Combine(home, "jre", "bin", "server", "jvm.dll")]
            : OperatingSystem.IsMacOS()
            ? [Path.Combine(home, "lib", "server", "libjvm.dylib"), Path.Combine(home, "jre", "lib", "server", "libjvm.dylib")]
            : [Path.Combine(home, "lib", "server", "libjvm.so"), Path.Combine(home, "lib", "amd64", "server", "libjvm.so"), Path.Combine(home, "jre", "lib", "amd64", "server", "libjvm.so")];
        string library = candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("JVM library missing.");
        // Java 8 Windows may ship its runtime dependencies alongside java.exe.
        if (OperatingSystem.IsWindows())
        {
            if (!SetDefaultDllDirectories(0x1000) || AddDllDirectory(bin) == 0)
                throw new InvalidOperationException("Cannot configure JVM dependency search.");
            string runtimeBin = Path.GetDirectoryName(Path.GetDirectoryName(library))!;
            if (AddDllDirectory(runtimeBin) == 0) throw new InvalidOperationException("Cannot configure runtime dependencies.");
        }
        nint handle = NativeLibrary.Load(library, typeof(NativeJvmHost).Assembly,
            DllImportSearchPath.SafeDirectories | DllImportSearchPath.UseDllDirectoryForDependencies);
        Console.Error.WriteLine("Nexa JVM Host: library loaded");
        Environment.CurrentDirectory = request.WorkingDirectory;
        List<string> options = [];
        for (int index = 0; index < request.JvmArguments.Count; index++)
        {
            string option = request.JvmArguments[index];
            if (OperatingSystem.IsMacOS() && MacJvmMainThread.ConsumeLauncherOption(option)) continue;
            if (option is "-cp" or "-classpath" or "--class-path")
            {
                if (++index == request.JvmArguments.Count) throw new InvalidDataException("Missing classpath.");
                options.Add("-Djava.class.path=" + request.JvmArguments[index]);
            }
            else if (option is "--module-path" or "-p" or "--add-modules" or "--add-exports" or "--add-opens" or "--add-reads" or "--limit-modules" or "--patch-module" or "--upgrade-module-path")
            {
                if (++index == request.JvmArguments.Count) throw new InvalidDataException("Missing module option.");
                options.Add((option == "-p" ? "--module-path" : option) + "=" + request.JvmArguments[index]);
            }
            else if (option.StartsWith("--class-path=", StringComparison.Ordinal)) options.Add("-Djava.class.path=" + option[13..]);
            else options.Add(option);
        }
        VmOption[] nativeOptions = new VmOption[options.Count];
        nint vm = 0, env = 0;
        try
        {
            for (int i = 0; i < options.Count; i++) nativeOptions[i].Value = Marshal.StringToCoTaskMemUTF8(options[i]);
            fixed (VmOption* pointer = nativeOptions)
            {
                VmArguments arguments = new() { Version = 0x00010008, Count = nativeOptions.Length, Options = pointer };
                var create = (delegate* unmanaged<nint*, nint*, VmArguments*, int>)NativeLibrary.GetExport(handle, "JNI_CreateJavaVM");
                int code = create(&vm, &env, &arguments);
                if (code != 0) { Console.Error.WriteLine($"JNI_CreateJavaVM failed: {code}"); return 1; }
                Console.Error.WriteLine("Nexa JVM Host: VM created");
            }
            return InvokeMain(env, request.MainClass, request.GameArguments);
        }
        finally
        {
            foreach (VmOption option in nativeOptions) if (option.Value != 0) Marshal.FreeCoTaskMem(option.Value);
            if (vm != 0) ((delegate* unmanaged<nint, int>)(*(nint**)vm)[3])(vm);
            // HotSpot is not safely unloadable/restartable; the isolated process owns the library.
        }
    }

    private static int InvokeMain(nint env, string mainClass, IReadOnlyList<string> arguments)
    {
        nint* functions = *(nint**)env;
        var find = (delegate* unmanaged<nint, byte*, nint>)functions[6];
        var delete = (delegate* unmanaged<nint, nint, void>)functions[23];
        nint mainName = Marshal.StringToCoTaskMemUTF8(mainClass.Replace('.', '/'));
        nint main;
        try { main = find(env, (byte*)mainName); }
        finally { Marshal.FreeCoTaskMem(mainName); }
        if (Failed(env) || main == 0) return 1;
        fixed (byte* methodName = "main\0"u8, signature = "([Ljava/lang/String;)V\0"u8, stringName = "java/lang/String\0"u8)
        {
            nint method = ((delegate* unmanaged<nint, nint, byte*, byte*, nint>)functions[113])(env, main, methodName, signature);
            if (Failed(env) || method == 0) return 1;
            nint stringClass = find(env, stringName);
            if (Failed(env) || stringClass == 0) return 1;
            nint array = ((delegate* unmanaged<nint, int, nint, nint, nint>)functions[172])(env, arguments.Count, stringClass, 0);
            if (Failed(env) || array == 0) return 1;
            foreach ((string value, int index) in arguments.Select(static (value, index) => (value, index)))
            {
                nint text;
                fixed (char* chars = value) text = ((delegate* unmanaged<nint, char*, int, nint>)functions[163])(env, chars, value.Length);
                if (Failed(env) || text == 0) return 1;
                ((delegate* unmanaged<nint, nint, int, nint, void>)functions[174])(env, array, index, text);
                delete(env, text);
                if (Failed(env)) return 1;
            }
            // jvalue is an 8-byte union, including on 32-bit hosts.
            long argument = array;
            ((delegate* unmanaged<nint, nint, nint, long*, void>)functions[143])(env, main, method, &argument);
            bool failed = Failed(env);
            delete(env, array);
            delete(env, stringClass);
            delete(env, main);
            return failed ? 1 : 0;
        }
    }

    private static bool Failed(nint env)
    {
        nint* functions = *(nint**)env;
        if (((delegate* unmanaged<nint, byte>)functions[228])(env) == 0) return false;
        ((delegate* unmanaged<nint, void>)functions[16])(env); // ExceptionDescribe also clears it.
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VmOption { public nint Value; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct VmArguments { public int Version; public int Count; public VmOption* Options; public byte IgnoreUnknown; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string directory);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint flags);
}
