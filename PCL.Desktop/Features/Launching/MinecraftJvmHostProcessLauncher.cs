// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Application.Launching;
using PCL.Core.Logging;

namespace PCL.Desktop.Features.Launching;

internal sealed record MinecraftJvmHostProcessHandle(
    Process Process,
    Task<MinecraftLaunchFaultReport?> FaultReport);

internal static class MinecraftJvmHostProcessLauncher
{
    private const string HostArgument = "--jvm-host";

    public static MinecraftJvmHostProcessHandle Start(MinecraftJvmHostRequest request, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(request);

        string pipeName = "pcln-jvm-" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream pipe = new(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        MinecraftJvmHostRequest prepared = request with { PipeName = pipeName };
        string requestPath = WriteRequest(prepared);
        ProcessStartInfo startInfo = CreateStartInfo(requestPath, prepared.WorkingDirectory);

        try
        {
            Process? process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Jvm.NET Host 进程未能启动。");

            int processId = process.Id;
            TaskCompletionSource<MinecraftLaunchFaultReport?> faultSource =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ObserveLifecycleAsync(pipe, process, processId, log, faultSource);
            _ = MonitorGameLogAsync(prepared.WorkingDirectory, process, processId, log, faultSource);
            PortableLog.Info("JvmHost", $"Jvm.NET Host 已启动；PID={processId}；Pipe={pipeName}。");
            return new MinecraftJvmHostProcessHandle(process, faultSource.Task);
        }
        catch
        {
            pipe.Dispose();
            TryDelete(requestPath);
            throw;
        }
    }

    internal static bool TryGetRequestPath(string[] args, out string requestPath)
    {
        requestPath = string.Empty;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (!string.Equals(args[i], HostArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            requestPath = args[i + 1];
            return !string.IsNullOrWhiteSpace(requestPath);
        }

        return false;
    }

    private static string WriteRequest(MinecraftJvmHostRequest request)
    {
        string directory = Path.Combine(Path.GetTempPath(), "PCL-N", "JvmHost");
        Directory.CreateDirectory(directory);
        CleanupStaleRequests(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        string json = JsonSerializer.Serialize(request, MinecraftJvmHostJsonContext.Default.MinecraftJvmHostRequest);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static void CleanupStaleRequests(string directory)
    {
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(string requestPath, string workingDirectory)
    {
        string executable = Environment.ProcessPath
                            ?? throw new InvalidOperationException("无法确定 PCL N 当前可执行文件路径。");
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string entryAssembly = Path.Combine(AppContext.BaseDirectory, "PCL.Desktop.dll");
            if (!File.Exists(entryAssembly))
                throw new FileNotFoundException("无法确定 PCL N 入口程序集路径。", entryAssembly);
            startInfo.ArgumentList.Add(entryAssembly);
        }

        startInfo.ArgumentList.Add(HostArgument);
        startInfo.ArgumentList.Add(requestPath);
        return startInfo;
    }

    private static async Task ObserveLifecycleAsync(
        NamedPipeServerStream pipe,
        Process process,
        int processId,
        Action<string>? log,
        TaskCompletionSource<MinecraftLaunchFaultReport?> faultSource)
    {
        bool receivedFaultReport = false;
        string lastStage = "HostStarting";
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                using StreamReader reader = new(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 65_536)
                        line = line[..65_536];
                    int separator = line.IndexOf('\t');
                    string stage = separator < 0 ? "Event" : line[..separator];
                    string message = separator < 0 ? line : line[(separator + 1)..];
                    if (!string.Equals(stage, "FaultReport", StringComparison.Ordinal))
                        lastStage = stage;
                    if (string.Equals(stage, "FaultReport", StringComparison.Ordinal))
                    {
                        if (TryParseFaultReport(message, out MinecraftLaunchFaultReport? report) && report is not null)
                        {
                            receivedFaultReport = true;
                            faultSource.TrySetResult(report);
                            string summary = $"Jvm Host [FaultReport] {report.Code} · {report.Stage} · {report.Subsystem}";
                            log?.Invoke(summary);
                            PortableLog.Warn("JvmHost", summary + " · " + report.Message);
                        }
                        else
                        {
                            PortableLog.Warn("JvmHost", "Jvm.NET Host 返回了无法解析的结构化故障报告。");
                        }
                        continue;
                    }
                    string formatted = $"Jvm Host [{stage}] {message}";
                    log?.Invoke(formatted);
                    PortableLog.Info("JvmHost", formatted);
                }
            }
            catch (OperationCanceledException)
            {
                PortableLog.Warn("JvmHost", $"Jvm.NET Host 未在规定时间内连接生命周期管道；PID={processId}。");
            }
            catch (IOException ex)
            {
                PortableLog.Debug("JvmHost", $"Jvm.NET Host 生命周期管道已结束；PID={processId}；{ex.Message}");
            }
            catch (ObjectDisposedException)
            {
                // The game process ended while the listener was being torn down.
            }
            catch (Exception ex)
            {
                PortableLog.Warn(
                    ex,
                    "JvmHost",
                    $"Jvm.NET Host 生命周期观察器异常；PID={processId}。");
            }
            finally
            {
                if (!receivedFaultReport)
                {
                    MinecraftLaunchFaultReport? nativeFailure = await CreateUnexpectedHostExitReportAsync(
                            process,
                            processId,
                            lastStage)
                        .ConfigureAwait(false);
                    if (nativeFailure is not null)
                    {
                        faultSource.TrySetResult(nativeFailure);
                        PortableLog.Warn(
                            "JvmHost",
                            $"Jvm.NET Host 在 {lastStage} 阶段异常退出；PID={processId}；{nativeFailure.Message}");
                    }
                    else
                    {
                        faultSource.TrySetResult(null);
                    }
                }
            }
        }
    }

    internal static MinecraftLaunchFaultReport? AnalyzeNeoForgeLogLines(IEnumerable<string> lines)
    {
        string[] evidence = lines
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(80)
            .ToArray();
        if (!evidence.Any(IsNeoForgeDependencyLine))
            return null;

        MinecraftLaunchFaultReport report = MinecraftLaunchFaultAnalyzer.AnalyzeText(
            evidence,
            "NeoForgeDependencyCheck");
        return report with
        {
            Code = MinecraftLaunchFaultCode.MissingModDependency,
            Stage = "NeoForgeDependencyCheck",
            Subsystem = "ModLoader",
            Message = "NeoForge 检测到缺失或不兼容的必需模组依赖。",
            AllowedActions =
            [
                MinecraftRepairActionKind.InstallMissingModDependencies,
                MinecraftRepairActionKind.DownloadMod,
                MinecraftRepairActionKind.ReadModMetadata
            ]
        };
    }

    private static bool IsNeoForgeDependencyLine(string line) =>
        line.Contains("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Missing mandatory dependencies", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Missing mods", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("requires version", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("requires any version", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Failed to load mod file", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Mod resolution encountered", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("依赖模组", StringComparison.OrdinalIgnoreCase);

    private static async Task MonitorGameLogAsync(
        string workingDirectory,
        Process process,
        int processId,
        Action<string>? log,
        TaskCompletionSource<MinecraftLaunchFaultReport?> faultSource)
    {
        string logPath = Path.Combine(workingDirectory, "logs", "latest.log");
        List<string> window = [];
        long position = 0;
        try
        {
            while (!faultSource.Task.IsCompleted && !process.HasExited)
            {
                if (File.Exists(logPath))
                {
                    await using FileStream stream = new(
                        logPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length < position)
                        position = 0;
                    stream.Position = position;
                    using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        window.Add(line);
                        if (window.Count > 80)
                            window.RemoveAt(0);
                        if (AnalyzeNeoForgeLogLines(window) is { } report)
                        {
                            if (faultSource.TrySetResult(report))
                            {
                                string summary = $"Jvm Host [FaultReport] {report.Code} · {report.Stage} · {report.Subsystem}";
                                log?.Invoke(summary);
                                PortableLog.Warn("JvmHost", summary + " · " + report.Message);
                            }
                            return;
                        }
                    }
                    position = stream.Position;
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PortableLog.Debug(exception, "JvmHost", $"游戏日志监控已结束；PID={processId}。");
        }
    }

    internal static async Task<MinecraftLaunchFaultReport?> CreateUnexpectedHostExitReportAsync(
        Process process,
        int processId,
        string lastStage)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!process.HasExited || process.ExitCode == 0)
                return null;
            int exitCode = process.ExitCode;
            uint unsigned = unchecked((uint)exitCode);
            string message =
                $"Jvm.NET Host 在 JVM 生命周期阶段 {lastStage} 异常退出；" +
                $"ExitCode={exitCode} (0x{unsigned:X8})。";
            MinecraftLaunchFaultCode code = lastStage is "JvmStarting" or "JvmMode" or "HostStarting" or "BridgeReady"
                ? MinecraftLaunchFaultCode.JvmInitializationFailed
                : MinecraftLaunchFaultCode.Unknown;
            return new MinecraftLaunchFaultReport
            {
                Code = code,
                Stage = lastStage,
                Subsystem = "JvmHost",
                ExceptionType = "NativeProcessExit",
                Message = message,
                Evidence =
                [
                    $"Jvm.NET Host PID={processId}",
                    $"LastLifecycleStage={lastStage}",
                    $"ExitCode={exitCode}",
                    $"ExitCodeHex=0x{unsigned:X8}",
                    "若未出现 JvmRunning/MainInvoking，崩溃发生在 Minecraft 主类执行之前。"
                ],
                AllowedActions =
                [
                    MinecraftRepairActionKind.DisableExperimentalJvmHost,
                    MinecraftRepairActionKind.InspectOnly,
                    MinecraftRepairActionKind.SelectCompatibleJava
                ]
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    private static bool TryParseFaultReport(string payload, out MinecraftLaunchFaultReport? report)
    {
        report = null;
        try
        {
            byte[] json = Convert.FromBase64String(payload);
            report = JsonSerializer.Deserialize(
                json,
                MinecraftJvmHostJsonContext.Default.MinecraftLaunchFaultReport);
            return report is not null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NotSupportedException)
        {
            return false;
        }
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
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MinecraftJvmHostRequest))]
[JsonSerializable(typeof(MinecraftLaunchFaultReport))]
internal sealed partial class MinecraftJvmHostJsonContext : JsonSerializerContext;
