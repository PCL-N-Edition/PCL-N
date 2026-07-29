// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PCL.Core.Logging;
using PCL.Desktop.Paths;
using PCL.Desktop.Views;

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Captures every managed unhandled-exception channel available to Avalonia/NativeAOT.
/// Native fail-fast, access violations, signals and forced termination cannot safely run
/// managed handlers, so a process-session marker reports those on the next launch.
/// </summary>
internal static class UnhandledExceptionGuard
{
    public const string IssuesUrl = "https://github.com/MuXue1230-owo/PCL-N/issues/new/choose";
    public const string IssuesNewUrl = "https://github.com/MuXue1230-owo/PCL-N/issues/new";

    private static int _installed;
    private static int _handling;
    private static int _dialogShown;
    private static int _sessionCompleted;
    private static bool _dispatcherAttached;
    private static string? _sessionMarkerPath;
    private static PreviousAbnormalExit? _previousAbnormalExit;
    private static string? _lastFingerprint;
    private static long _lastReportTicks;
    private static int _suppressedCascade;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        StartCrashSession();
    }

    /// <summary>
    /// Mark the current process as cleanly completed. Deliberately not registered on
    /// ProcessExit: fatal managed/native exits may raise ProcessExit even though Main
    /// did not complete successfully.
    /// </summary>
    public static void CompleteSession(bool completedNormally)
    {
        if (!completedNormally || Interlocked.Exchange(ref _sessionCompleted, 1) == 1)
            return;

        string? marker = _sessionMarkerPath;
        if (string.IsNullOrWhiteSpace(marker))
            return;

        try
        {
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("Crash", "清理正常退出标记失败。", ex);
        }
    }

    /// <summary>Call after Avalonia UI thread is available.</summary>
    public static void AttachUiDispatcher()
    {
        if (_dispatcherAttached)
            return;
        _dispatcherAttached = true;

        try
        {
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        }
        catch (Exception ex)
        {
            DesktopFileLog.Error("Crash", "无法挂接 UI 线程异常处理。", ex);
        }
    }

    /// <summary>Call once the Avalonia lifetime and first owner window are initialized.</summary>
    public static void NotifyUiReady()
    {
        PreviousAbnormalExit? previous = Interlocked.Exchange(ref _previousAbnormalExit, null);
        if (previous is null)
            return;

        try
        {
            Dispatcher.UIThread.Post(
                () => ShowCrashUi(
                    previous.Exception,
                    previous.Report,
                    canContinue: true,
                    heading: "检测到上次异常退出",
                    description:
                        "上次启动器进程没有完成正常关闭。可能是原生崩溃、FailFast、系统强制结束或断电；" +
                        "本次启动可以继续，请提交下方报告以便定位。"),
                DispatcherPriority.Background);
        }
        catch
        {
            FallbackConsole(previous.Exception, previous.Report);
        }
    }

    /// <summary>Observe an intentionally fire-and-forget task immediately, without waiting for GC.</summary>
    public static void Observe(Task task, string source)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        _ = ObserveCoreAsync(task, source);
    }

    private static async Task ObserveCoreAsync(Task task, string source)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal task outcome.
        }
        catch (Exception ex)
        {
            Report(ex, source + " (fire-and-forget)", canContinue: true);
        }
    }

    public static string BuildReport(Exception exception, string source)
    {
        ArgumentNullException.ThrowIfNull(exception);

        StringBuilder sb = new();
        sb.AppendLine("### 环境");
        sb.AppendLine("- PCL N: " + GetVersion());
        sb.AppendLine("- OS: " + RuntimeInformation.OSDescription);
        sb.AppendLine("- Architecture: " +
                      RuntimeInformation.OSArchitecture + " / " +
                      RuntimeInformation.ProcessArchitecture);
        sb.AppendLine("- Runtime: " + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("- NativeAOT: " + (!RuntimeFeature.IsDynamicCodeSupported ? "yes" : "no"));
        sb.AppendLine("- Process: " + (Environment.ProcessPath ?? "(unknown)"));
        sb.AppendLine("- PID: " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("- Source: " + source);
        sb.AppendLine("- Time: " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("- Data: " + TryResolveDataDirectory());
        sb.AppendLine("- Log: " + TryResolveLogPath());
        sb.AppendLine();
        sb.AppendLine("### 异常");
        sb.AppendLine("```");
        sb.AppendLine(Flatten(exception));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### 复现步骤");
        sb.AppendLine("1. ");
        sb.AppendLine("2. ");
        sb.AppendLine();
        sb.AppendLine("### 期望行为");
        sb.AppendLine();
        return sb.ToString();
    }

    internal static string BuildAbnormalExitReport(string previousSession)
    {
        StringBuilder sb = new();
        sb.AppendLine("### 上次进程异常退出");
        sb.AppendLine();
        sb.AppendLine(
            "没有捕获到可安全执行的托管异常处理回调。常见原因包括原生库崩溃、" +
            "Environment.FailFast、StackOverflow、操作系统信号、任务管理器强制结束或断电。");
        sb.AppendLine();
        sb.AppendLine("### 上次会话");
        sb.AppendLine("```text");
        sb.AppendLine(previousSession.Trim());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### 本次检测环境");
        sb.AppendLine("- PCL N: " + GetVersion());
        sb.AppendLine("- OS: " + RuntimeInformation.OSDescription);
        sb.AppendLine("- Runtime: " + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("- Detected: " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("- Log: " + TryResolveLogPath());
        return sb.ToString();
    }

    public static void Report(Exception exception, string source, bool canContinue)
    {
        if (exception is null)
            return;

        // Avoid re-entrancy when the dialog or logger itself throws.
        if (Interlocked.Exchange(ref _handling, 1) == 1)
        {
            try
            {
                DesktopFileLog.Error("Crash", "处理崩溃时发生二次未处理异常。", exception);
            }
            catch
            {
                // ignore
            }

            return;
        }

        try
        {
            string fingerprint = exception.GetType().FullName + "|" + exception.Message;
            long now = Environment.TickCount64;
            if (string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal) &&
                now - _lastReportTicks < 5000)
            {
                int suppressed = Interlocked.Increment(ref _suppressedCascade);
                if (suppressed is 1 or 10 or 50)
                {
                    try
                    {
                        DesktopFileLog.Warn(
                            "Crash",
                            $"抑制重复异常 ×{suppressed}；来源={source}；消息={exception.Message}");
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return;
            }

            _lastFingerprint = fingerprint;
            _lastReportTicks = now;
            Interlocked.Exchange(ref _suppressedCascade, 0);

            string report = PortableLog.Redact(BuildReport(exception, source));
            try
            {
                DesktopFileLog.Initialize(DesktopFileLog.Level);
                DesktopFileLog.Error("Crash", $"未处理异常来源：{source}；可恢复={canContinue}。", exception);
                string? crashReportPath = WriteCrashDump(report, "crash");
                if (!canContinue && !string.IsNullOrWhiteSpace(crashReportPath))
                    RecordFatalReportPath(crashReportPath);
            }
            catch
            {
                // Logging must never throw out of the guard.
            }

            ShowCrashUi(exception, report, canContinue);
        }
        finally
        {
            Interlocked.Exchange(ref _handling, 0);
        }
    }

    internal static bool IsRecoverableUiException(Exception exception)
    {
        if (exception is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.All(IsRecoverableUiException);

        return exception is not (
            OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            SEHException);
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Exception ex = e.ExceptionObject as Exception
            ?? new InvalidOperationException("未知未处理异常：" + (e.ExceptionObject?.ToString() ?? "(null)"));
        // AppDomain unhandled is terminating under modern .NET/NativeAOT.
        Report(ex, "AppDomain.UnhandledException; terminating=" + e.IsTerminating, canContinue: false);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Report(
            e.Exception.Flatten().InnerException ?? e.Exception,
            "TaskScheduler.UnobservedTaskException",
            canContinue: true);
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        bool canContinue = IsRecoverableUiException(e.Exception);
        e.Handled = canContinue;
        Report(e.Exception, "Dispatcher.UIThread.UnhandledException", canContinue);
    }

    private static void ShowCrashUi(
        Exception exception,
        string report,
        bool canContinue,
        string? heading = null,
        string? description = null)
    {
        if (Interlocked.Exchange(ref _dialogShown, 1) == 1)
            return;

        void Show()
        {
            try
            {
                CrashReportWindow window = new(
                    exception,
                    report,
                    canContinue,
                    IssuesUrl,
                    IssuesNewUrl,
                    heading,
                    description);
                Window? owner = TryGetMainWindow();
                if (owner is not null)
                {
                    window.ShowDialog(owner).ContinueWith(
                        _ => Interlocked.Exchange(ref _dialogShown, 0),
                        TaskScheduler.Default);
                }
                else
                {
                    window.Closed += (_, _) => Interlocked.Exchange(ref _dialogShown, 0);
                    window.Show();
                }
            }
            catch (Exception showEx)
            {
                Interlocked.Exchange(ref _dialogShown, 0);
                DesktopFileLog.Error("Crash", "无法显示崩溃窗口。", showEx);
                FallbackConsole(exception, report);
            }
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                Show();
            else
                Dispatcher.UIThread.Post(Show, DispatcherPriority.Send);
        }
        catch
        {
            Interlocked.Exchange(ref _dialogShown, 0);
            FallbackConsole(exception, report);
        }
    }

    private static Window? TryGetMainWindow()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void FallbackConsole(Exception exception, string report)
    {
        try
        {
            Console.Error.WriteLine("PCL N 发生未捕获异常。请提交 Issue：");
            Console.Error.WriteLine(IssuesUrl);
            Console.Error.WriteLine(report);
            Debug.WriteLine(exception);
        }
        catch
        {
            // ignore
        }
    }

    private static void StartCrashSession()
    {
        try
        {
            string root = Path.Combine(
                Path.GetDirectoryName(LauncherPathLayout.OverrideFilePath)
                    ?? LauncherPathLayout.GetDefaultDataDirectory(),
                "CrashSessions");
            Directory.CreateDirectory(root);

            foreach (FileInfo marker in new DirectoryInfo(root)
                         .EnumerateFiles("*.active", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(static file => file.LastWriteTimeUtc))
            {
                string session;
                try
                {
                    session = File.ReadAllText(marker.FullName);
                }
                catch
                {
                    continue;
                }

                if (IsSessionStillRunning(session))
                    continue;

                string? managedReportPath = TryReadSessionValue(session, "fatalReport");
                string report;
                string? reportPath;
                if (!string.IsNullOrWhiteSpace(managedReportPath) &&
                    TryReadExistingCrashReport(managedReportPath, out string? managedReport))
                {
                    report = managedReport!;
                    reportPath = managedReportPath;
                }
                else
                {
                    report = BuildAbnormalExitReport(session);
                    reportPath = WriteCrashDumpCore(report, "abnormal-exit");
                }
                _previousAbnormalExit ??= new PreviousAbnormalExit(
                    new PreviousProcessTerminationException(
                        "上次 PCL N 进程未完成正常关闭。" +
                        (string.IsNullOrWhiteSpace(reportPath) ? string.Empty : " 报告：" + reportPath)),
                    report);
                TryDelete(marker.FullName);
            }

            string startedUtcTicks = DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture);
            string markerName =
                $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-p{Environment.ProcessId}-" +
                Guid.NewGuid().ToString("N")[..8] + ".active";
            string markerPath = Path.Combine(root, markerName);
            string markerContent = string.Join(
                Environment.NewLine,
                "format=pcln-crash-session-v1",
                "pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "startedUtcTicks=" + startedUtcTicks,
                "version=" + GetVersion(),
                "process=" + (Environment.ProcessPath ?? "(unknown)"),
                "os=" + RuntimeInformation.OSDescription,
                "architecture=" + RuntimeInformation.ProcessArchitecture,
                "runtime=" + RuntimeInformation.FrameworkDescription,
                "nativeAot=" + (!RuntimeFeature.IsDynamicCodeSupported ? "yes" : "no"));
            File.WriteAllText(markerPath, markerContent, new UTF8Encoding(false));
            _sessionMarkerPath = markerPath;
        }
        catch (Exception ex)
        {
            try
            {
                DesktopFileLog.Warn("Crash", "创建进程异常退出检测标记失败。", ex);
            }
            catch
            {
                // Never block startup.
            }
        }
    }

    private static bool IsSessionStillRunning(string session)
    {
        if (!TryReadSessionInt64(session, "pid", out long pidValue) ||
            pidValue is <= 0 or > int.MaxValue ||
            !TryReadSessionInt64(session, "startedUtcTicks", out long startedUtcTicks))
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)pidValue);
            if (process.HasExited)
                return false;
            long processTicks = process.StartTime.ToUniversalTime().Ticks;
            return Math.Abs(processTicks - startedUtcTicks) <= TimeSpan.FromSeconds(10).Ticks;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadSessionInt64(string session, string key, out long value)
    {
        string? text = TryReadSessionValue(session, key);
        return long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string? TryReadSessionValue(string session, string key)
    {
        string prefix = key + "=";
        foreach (string line in session.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..];
        }

        return null;
    }

    private static bool TryReadExistingCrashReport(string path, out string? report)
    {
        report = null;
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > 2 * 1024 * 1024)
                return false;
            report = File.ReadAllText(file.FullName);
            return !string.IsNullOrWhiteSpace(report);
        }
        catch
        {
            report = null;
            return false;
        }
    }

    private static string? WriteCrashDump(string report, string prefix)
    {
        string? path = WriteCrashDumpCore(report, prefix);
        if (!string.IsNullOrWhiteSpace(path))
            DesktopFileLog.Info("Crash", "崩溃报告已写入 " + path);
        return path;
    }

    private static string? WriteCrashDumpCore(string report, string prefix)
    {
        try
        {
            string directory = Path.Combine(LauncherPathLayout.ResolveLogDirectory(), "Crashes");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                $"{prefix}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}-" +
                Guid.NewGuid().ToString("N")[..8] + ".md");
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.Write(PortableLog.Redact(report));
            return path;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                DesktopFileLog.Error("Crash", "写入崩溃报告失败。", ex);
            }
            catch
            {
                // ignore
            }
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A consumed marker can be retried next launch.
        }
    }

    private static void RecordFatalReportPath(string reportPath)
    {
        string? marker = _sessionMarkerPath;
        if (string.IsNullOrWhiteSpace(marker))
            return;

        try
        {
            using FileStream stream = new(
                marker,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.WriteLine();
            writer.Write("fatalReport=");
            writer.Write(reportPath.ReplaceLineEndings(" "));
        }
        catch
        {
            // The crash report itself has already been persisted.
        }
    }

    private static string GetVersion()
    {
        try
        {
            return Assembly.GetEntryAssembly()
                       ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                       ?.InformationalVersion
                   ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                   ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string TryResolveDataDirectory()
    {
        try
        {
            return LauncherPathLayout.ResolveDataDirectory();
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static string TryResolveLogPath()
    {
        try
        {
            return DesktopFileLog.CurrentLogPath;
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static string Flatten(Exception exception)
    {
        if (exception is AggregateException aggregate)
            exception = aggregate.Flatten();

        StringBuilder sb = new();
        Exception? current = exception;
        int depth = 0;
        while (current is not null && depth < 12)
        {
            if (depth > 0)
                sb.AppendLine("--- Inner ---");
            sb.Append(current.GetType().FullName);
            sb.Append(": ");
            sb.AppendLine(current.Message);
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
                sb.AppendLine(current.StackTrace);
            current = current.InnerException;
            depth++;
        }

        return sb.ToString().TrimEnd();
    }

    private sealed record PreviousAbnormalExit(Exception Exception, string Report);

    private sealed class PreviousProcessTerminationException(string message) : Exception(message);
}
