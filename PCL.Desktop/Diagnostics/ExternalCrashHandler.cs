// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Spawns the optional out-of-process C companion <c>pcln-crash-handler</c>.
/// The helper survives host segfaults and writes a watchdog report + native UI
/// when the host exits without creating a clean-exit flag.
/// </summary>
internal static class ExternalCrashHandler
{
    private static int _started;
    private static string? _cleanFlagPath;
    private static int _handlerPid;

    public static string? CleanFlagPath => _cleanFlagPath;

    /// <summary>
    /// Start the companion if a binary is present next to the host or under the
    /// data directory. Safe to call multiple times; no-ops when missing.
    /// </summary>
    public static void TryStart(string? sessionMarkerPath)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        try
        {
            // C bootstrap (pcln-launcher) already owns the crash-handler process.
            // Still adopt its clean-flag path so CompleteSession can silence the watcher.
            string? skip = Environment.GetEnvironmentVariable("PCL_SKIP_EXTERNAL_CRASH_HANDLER");
            if (!string.IsNullOrWhiteSpace(skip) &&
                !string.Equals(skip, "0", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(skip, "false", StringComparison.OrdinalIgnoreCase))
            {
                string? launcherFlag = Environment.GetEnvironmentVariable("PCL_CRASH_CLEAN_FLAG");
                if (!string.IsNullOrWhiteSpace(launcherFlag))
                    _cleanFlagPath = launcherFlag;
                DesktopFileLog.Info(
                    "Crash",
                    "C launcher 已托管崩溃监视器，跳过进程内二次拉起" +
                    (string.IsNullOrWhiteSpace(_cleanFlagPath)
                        ? "。"
                        : "；cleanFlag=" + _cleanFlagPath));
                return;
            }

            string? binary = ResolveHandlerBinary();
            if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
            {
                try
                {
                    DesktopFileLog.Info(
                        "Crash",
                        "未找到进程外崩溃处理器 pcln-crash-handler，跳过（可选组件）。");
                }
                catch
                {
                    // ignore
                }

                return;
            }

            string crashDir = Path.Combine(LauncherPathLayout.ResolveLogDirectory(), "Crashes");
            Directory.CreateDirectory(crashDir);

            string sessionRoot = Path.Combine(
                Path.GetDirectoryName(LauncherPathLayout.OverrideFilePath)
                    ?? LauncherPathLayout.GetDefaultDataDirectory(),
                "CrashSessions");
            Directory.CreateDirectory(sessionRoot);

            string stamp =
                DateTimeOffset.UtcNow.ToString(
                    "yyyyMMdd-HHmmssfff",
                    System.Globalization.CultureInfo.InvariantCulture) +
                "-p" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _cleanFlagPath = Path.Combine(sessionRoot, "session-" + stamp + ".clean");

            string marker = string.IsNullOrWhiteSpace(sessionMarkerPath)
                ? string.Empty
                : sessionMarkerPath;

            ProcessStartInfo start = new()
            {
                FileName = binary,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(binary) ?? AppContext.BaseDirectory
            };
            start.ArgumentList.Add("--parent-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--crash-dir");
            start.ArgumentList.Add(crashDir);
            start.ArgumentList.Add("--clean-flag");
            start.ArgumentList.Add(_cleanFlagPath);
            if (!string.IsNullOrWhiteSpace(marker))
            {
                start.ArgumentList.Add("--marker");
                start.ArgumentList.Add(marker);
            }

            using Process? process = Process.Start(start);
            if (process is null)
                return;

            _handlerPid = process.Id;
            try
            {
                DesktopFileLog.Info(
                    "Crash",
                    $"进程外崩溃处理器已启动；pid={_handlerPid}；binary={binary}；cleanFlag={_cleanFlagPath}");
            }
            catch
            {
                // ignore
            }
        }
        catch (Exception ex)
        {
            try
            {
                DesktopFileLog.Warn("Crash", "启动进程外崩溃处理器失败。", ex);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Signal normal exit so the companion quits silently.
    /// </summary>
    public static void SignalCleanExit()
    {
        string? flag = _cleanFlagPath;
        if (string.IsNullOrWhiteSpace(flag))
            return;

        try
        {
            string? dir = Path.GetDirectoryName(flag);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(flag, "ok\n", new UTF8Encoding(false));
        }
        catch
        {
            // Best-effort; the companion may still raise a false positive if this fails.
        }
    }

    private static string? ResolveHandlerBinary()
    {
        string name = OperatingSystem.IsWindows()
            ? "pcln-crash-handler.exe"
            : "pcln-crash-handler";

        List<string> candidates = [];

        string? processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDir))
            candidates.Add(Path.Combine(processDir, name));

        candidates.Add(Path.Combine(AppContext.BaseDirectory, name));

        try
        {
            string data = LauncherPathLayout.ResolveDataDirectory();
            candidates.Add(Path.Combine(data, "CrashHandler", name));
            candidates.Add(Path.Combine(data, name));
        }
        catch
        {
            // ignore
        }

        // Dev layout: native/pcln-crash-handler next to repo when running from bin/
        try
        {
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrWhiteSpace(dir); i++)
            {
                candidates.Add(Path.Combine(dir, "native", "pcln-crash-handler", name));
                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        catch
        {
            // ignore
        }

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
