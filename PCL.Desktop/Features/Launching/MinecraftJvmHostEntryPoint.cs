// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Jvm.NET;
using Jvm.NET.Abstractions;
using PCL.Application.Launching;

namespace PCL.Desktop.Features.Launching;

internal static class MinecraftJvmHostEntryPoint
{
    public static int Run(string requestPath)
    {
        MinecraftJvmHostRequest request;
        try
        {
            string json = File.ReadAllText(requestPath, Encoding.UTF8);
            request = JsonSerializer.Deserialize(json, MinecraftJvmHostJsonContext.Default.MinecraftJvmHostRequest)
                      ?? throw new InvalidDataException("Jvm.NET Host 请求为空。");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Unable to read the Jvm.NET host request: " + ex.Message);
            return 2;
        }
        finally
        {
            TryDelete(requestPath);
        }

        using JvmHostLifecycleWriter lifecycle = new(request.PipeName);
        string originalWorkingDirectory = Environment.CurrentDirectory;
        try
        {
            lifecycle.Send("HostStarting", $"Java {request.JavaMajorVersion} · {request.MainClass}");
            ValidateRequest(request);
            Environment.CurrentDirectory = request.WorkingDirectory;

            using MinecraftSessionBridge? bridge = request.IdentityMode == MinecraftJvmHostIdentityMode.Official
                ? null
                : MinecraftSessionBridge.Start(request, lifecycle);
            List<string> vmArguments = [.. request.VmArguments];
            if (bridge is not null)
            {
                bridge.AppendJvmProperties(vmArguments);
                lifecycle.Send("BridgeReady", bridge.BaseUrl);
            }

            RegisterJdkImplementation(request.JavaMajorVersion);
            lifecycle.Send("JvmStarting", Path.GetDirectoryName(request.JavaExecutablePath) ?? request.JavaExecutablePath);
            string jdkBinPath = ResolveJdkBinPath(request.JavaExecutablePath);
            JvmInitializationOptions options = new()
            {
                JdkBinPath = jdkBinPath,
                Version = request.JavaMajorVersion,
                VmArguments = vmArguments,
                Classpath = request.ClasspathEntries,
                EnableBytecodeModification = bridge is not null,
                EnableEventListening = true,
                RequireJvmti = true,
                Interop = new JvmInteropOptions { Mode = InteropMode.NativeOnly }
            };

            using IJvmRuntime runtime = JvmInitializer.Initialize(options);
            lifecycle.Send("JvmRunning", $"JVM {runtime.Version} 已初始化");
            using IDisposable classEvents = runtime.EventListener.SubscribeClassPrepare(data =>
                PublishClassLifecycle(data.Class.Name, lifecycle));
            using IDisposable? transformer = bridge is null
                ? null
                : runtime.BytecodeModifier.RegisterTransformer(new MinecraftAuthlibBytecodeTransformer(bridge.BaseUrl));

            lifecycle.Send("MainInvoking", request.MainClass);
            using JvmClass mainClass = runtime.Invoker.LoadClass(request.MainClass);
            JvmValue arguments = runtime.Invoker.NewStringArray(request.GameArguments);
            runtime.Invoker.InvokeStatic(mainClass, "main", "([Ljava/lang/String;)V", arguments);
            lifecycle.Send("MainReturned", request.MainClass);
            runtime.Shutdown();
            lifecycle.Send("VmStopped", "Minecraft JVM 已结束");
            return 0;
        }
        catch (Exception ex)
        {
            MinecraftLaunchFaultReport report = MinecraftLaunchFaultAnalyzer.Analyze(
                ex,
                lifecycle.LastStage,
                lifecycle.LastClassName);
            lifecycle.SendFault(report);
            lifecycle.Send("Faulted", report.Code + ": " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (Directory.Exists(originalWorkingDirectory))
                Environment.CurrentDirectory = originalWorkingDirectory;
        }
    }

    private static void ValidateRequest(MinecraftJvmHostRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.JavaExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MainClass);
        if (!File.Exists(request.JavaExecutablePath))
            throw new FileNotFoundException("找不到 Java 可执行文件。", request.JavaExecutablePath);
        if (!Directory.Exists(request.WorkingDirectory))
            throw new DirectoryNotFoundException("找不到游戏工作目录：" + request.WorkingDirectory);
        if (request.JavaMajorVersion < 8 || request.JavaMajorVersion > 30)
            throw new NotSupportedException("Jvm.NET Host 仅接受 Java 8 至 30：" + request.JavaMajorVersion);
        if (request.ClasspathEntries.Length == 0)
            throw new InvalidDataException("Jvm.NET Host classpath 为空。");
    }

    private static void RegisterJdkImplementation(int majorVersion)
    {
        if (JdkImplementationRegistry.Resolve(majorVersion) is null)
            JdkImplementationRegistry.Register(new MinecraftJdkImplementation(majorVersion));
    }

    internal static string ResolveJdkBinPath(string javaExecutablePath)
    {
        string executable = Path.GetFullPath(javaExecutablePath);
        string directBin = Path.GetDirectoryName(executable)
                           ?? throw new DirectoryNotFoundException("无法确定 Java bin 目录。");
        if (ContainsJvmLibrary(directBin))
            return directBin;

        // Windows javapath and some Linux alternatives are shims rather than links that
        // .NET can resolve. Ask that exact Java executable for java.home before loading JNI.
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-XshowSettings:properties");
        startInfo.ArgumentList.Add("-version");
        using Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("无法查询 Java 安装目录。");
        string output = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("查询 Java 安装目录超时。");
        }

        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("java.home", StringComparison.OrdinalIgnoreCase))
                continue;
            int separator = trimmed.IndexOf('=');
            if (separator < 0)
                continue;
            string candidate = Path.Combine(trimmed[(separator + 1)..].Trim(), "bin");
            if (ContainsJvmLibrary(candidate))
                return candidate;
        }

        throw new DllNotFoundException("无法在所选 Java 中找到 JVM 原生库：" + executable);
    }

    private static bool ContainsJvmLibrary(string binDirectory)
    {
        string libraryName = OperatingSystem.IsWindows()
            ? "jvm.dll"
            : OperatingSystem.IsMacOS() ? "libjvm.dylib" : "libjvm.so";
        return File.Exists(Path.Combine(binDirectory, libraryName)) ||
               File.Exists(Path.Combine(binDirectory, "server", libraryName)) ||
               File.Exists(Path.Combine(Path.GetDirectoryName(binDirectory) ?? string.Empty, "lib", "server", libraryName));
    }

    private static void PublishClassLifecycle(string? className, JvmHostLifecycleWriter lifecycle)
    {
        if (string.IsNullOrWhiteSpace(className))
            return;
        string normalized = className.Replace('/', '.');
        lifecycle.ObserveClass(normalized);
        if (normalized.Contains("net.minecraft.client.main.Main", StringComparison.Ordinal))
            lifecycle.SendOnce("MinecraftBootstrap", normalized);
        else if (normalized.Contains("net.minecraft.client.Minecraft", StringComparison.Ordinal))
            lifecycle.SendOnce("MinecraftClient", normalized);
        else if (normalized.Contains("org.lwjgl.glfw.GLFW", StringComparison.Ordinal))
            lifecycle.SendOnce("WindowRuntime", normalized);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class MinecraftJdkImplementation(int version) : IJdkImplementation
    {
        public int Version { get; } = version;

        public int JniVersion => Version switch
        {
            <= 8 => 0x00010008,
            9 => 0x00090000,
            >= 10 and <= 18 => 0x000A0000,
            19 => 0x00130000,
            20 => 0x00140000,
            >= 21 and <= 23 => 0x00150000,
            _ => 0x00180000
        };

        public int JvmtiVersion => Version <= 8 ? 0x30010200 : 0x30000000 | (Version << 16);

        public IJvmRuntime CreateRuntime(JvmInitializationOptions options) => new JdkRuntimeBase(options, this);
    }
}

internal sealed class JvmHostLifecycleWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<string> _sentOnce = new(StringComparer.Ordinal);
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private string _lastStage = "HostStarting";
    private string? _lastClassName;

    public string LastStage => Volatile.Read(ref _lastStage);

    public string? LastClassName => Volatile.Read(ref _lastClassName);

    public JvmHostLifecycleWriter(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            return;
        try
        {
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            _pipe.Connect(5000);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            _writer = null;
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    public void Send(string stage, string message)
    {
        if (!string.Equals(stage, "FaultReport", StringComparison.Ordinal))
            Volatile.Write(ref _lastStage, stage);
        lock (_gate)
        {
            if (_writer is null)
                return;
            string safeStage = stage.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            string safeMessage = message.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            try
            {
                _writer.WriteLine(safeStage + "\t" + safeMessage);
            }
            catch (IOException)
            {
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    public void ObserveClass(string className)
    {
        if (!string.IsNullOrWhiteSpace(className) && IsDiagnosticClass(className))
            Volatile.Write(ref _lastClassName, className);
    }

    public void SendFault(MinecraftLaunchFaultReport report)
    {
        string json = JsonSerializer.Serialize(
            report,
            MinecraftJvmHostJsonContext.Default.MinecraftLaunchFaultReport);
        Send("FaultReport", Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
    }

    public void SendOnce(string stage, string message)
    {
        lock (_gate)
        {
            if (!_sentOnce.Add(stage))
                return;
        }
        Send(stage, message);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    private static bool IsDiagnosticClass(string className) =>
        className.StartsWith("net.minecraft.", StringComparison.Ordinal) ||
        className.StartsWith("org.lwjgl.", StringComparison.Ordinal) ||
        className.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("quilt", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("mixin", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("authlib", StringComparison.OrdinalIgnoreCase);
}
