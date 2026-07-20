// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using PCL.Core.Logging;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Diagnostics;

public static class DesktopFileLog
{
    private static readonly object WriteLock = new();
    private static readonly string SessionFileName = CreateSessionFileName();
    private static readonly HashSet<string> InitializedFiles = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static bool _subscribed;
    private static StreamWriter? _writer;
    private static string? _writerPath;

    public static string CurrentLogPath => Path.Combine(
        LauncherSettingsPageBinder.CreateDataDirectory(),
        "Logs",
        SessionFileName);

    public static PortableLogLevel Level => PortableLog.MaximumLevel;

    public static PortableLogLevel LevelFromSetting(int value) =>
        Enum.IsDefined((PortableLogLevel)value)
            ? (PortableLogLevel)value
            : PortableLogLevel.Info;

    public static void Initialize(PortableLogLevel level = PortableLogLevel.Info)
    {
        ConfigureLevel(level);
        string path = CurrentLogPath;
        lock (WriteLock)
        {
            if (!_subscribed)
            {
                PortableLog.Written += OnLogWritten;
                _subscribed = true;
            }

            if (!InitializedFiles.Add(path))
                return;

            if (!PortableLog.IsEnabled(PortableLogLevel.Info))
                return;

            string version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";
            WriteCore(path, PortableLogLevel.Info, "Session", $"========== PCL N 会话开始（PID {Environment.ProcessId}） ==========");
            WriteCore(path, PortableLogLevel.Info, "Startup", $"PCL N {version}；进程：{Environment.ProcessPath ?? "unknown"}");
            WriteCore(
                path,
                PortableLogLevel.Info,
                "System",
                $"{RuntimeInformation.OSDescription}；系统架构：{RuntimeInformation.OSArchitecture}；进程架构：{RuntimeInformation.ProcessArchitecture}");
            WriteCore(
                path,
                PortableLogLevel.Info,
                "Runtime",
                $"{RuntimeInformation.FrameworkDescription}；CPU：{Environment.ProcessorCount}；GC：{(GCSettings.IsServerGC ? "Server" : "Workstation")}");
            WriteCore(
                path,
                PortableLogLevel.Info,
                "Locale",
                $"UI={CultureInfo.CurrentUICulture.Name}；区域={CultureInfo.CurrentCulture.Name}；时区={TimeZoneInfo.Local.Id}");
            WriteCore(path, PortableLogLevel.Info, "Display", DescribeDesktopSession());
        }
    }

    public static void ConfigureLevel(PortableLogLevel level)
    {
        PortableLogLevel next = Enum.IsDefined(level) ? level : PortableLogLevel.Info;
        PortableLogLevel previous = PortableLog.MaximumLevel;
        PortableLog.MaximumLevel = next;
        if (_subscribed && previous != next)
            PortableLog.Info("Logging", $"日志级别已从 {previous} 切换为 {next}。");
    }

    public static void Write(string message)
    {
        EnsureSink();
        PortableLog.Info("Desktop", message);
    }

    public static void Error(string module, string message, Exception? exception = null)
    {
        EnsureSink();
        if (exception is null)
            PortableLog.Error(module, message);
        else
            PortableLog.Error(exception, module, message);
    }

    public static void Warn(string module, string message, Exception? exception = null)
    {
        EnsureSink();
        if (exception is null)
            PortableLog.Warn(module, message);
        else
            PortableLog.Warn(exception, module, message);
    }

    public static void Info(string module, string message)
    {
        EnsureSink();
        PortableLog.Info(module, message);
    }

    public static void Debug(string module, string message, Exception? exception = null)
    {
        EnsureSink();
        if (exception is null)
            PortableLog.Debug(module, message);
        else
            PortableLog.Debug(exception, module, message);
    }

    public static void RealTime(string module, string message)
    {
        EnsureSink();
        PortableLog.RealTime(module, message);
    }

    private static void EnsureSink()
    {
        if (_subscribed)
            return;
        Initialize(PortableLog.MaximumLevel);
    }

    private static void OnLogWritten(PortableLogEntry entry)
    {
        string path = CurrentLogPath;
        lock (WriteLock)
        {
            if (InitializedFiles.Add(path) && PortableLog.IsEnabled(PortableLogLevel.Info))
                WriteCore(path, PortableLogLevel.Info, "Session", "PCL N 日志会话已开始。");
            WriteCore(path, entry.Level, entry.Module, entry.Message, entry.Exception, entry.Timestamp);
        }
    }

    private static void WriteCore(
        string path,
        PortableLogLevel level,
        string module,
        string message,
        Exception? exception = null,
        DateTimeOffset timestamp = default)
    {
        try
        {
            StreamWriter writer = EnsureWriter(path);
            DateTimeOffset localTimestamp = timestamp == default ? DateTimeOffset.Now : timestamp.ToLocalTime();
            writer.Write('[');
            writer.Write(localTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            writer.Write("] [");
            writer.Write(level);
            writer.Write("] [");
            writer.Write(module);
            writer.Write("] ");
            writer.WriteLine(PortableLog.Redact(message).ReplaceLineEndings(" | "));
            if (exception is not null)
            {
                writer.Write('[');
                writer.Write(localTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                writer.Write("] [");
                writer.Write(level);
                writer.Write("] [");
                writer.Write(module);
                writer.Write("] Exception: ");
                writer.WriteLine(PortableLog.Redact(exception.ToString()).ReplaceLineEndings(" | "));
            }

            // Keep the file handle open; flush so tails stay readable without open/close thrash.
            writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Log] 写入日志失败：" + ex.Message);
            CloseWriterUnlocked();
        }
    }

    private static StreamWriter EnsureWriter(string path)
    {
        if (_writer is not null &&
            _writerPath is not null &&
            string.Equals(
                _writerPath,
                path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return _writer;
        }

        CloseWriterUnlocked();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };
        _writerPath = path;
        return _writer;
    }

    private static void CloseWriterUnlocked()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // ignore dispose failures on shutdown paths
        }

        _writer = null;
        _writerPath = null;
    }

    private static string DescribeDesktopSession()
    {
        if (OperatingSystem.IsWindows())
            return "Windows 桌面会话";
        if (OperatingSystem.IsMacOS())
            return "macOS 桌面会话";

        string sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
        bool hasWayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        bool hasX11 = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        return $"Linux；会话类型={sessionType}；Wayland={(hasWayland ? "是" : "否")}；X11={(hasX11 ? "是" : "否")}";
    }

    private static string CreateSessionFileName()
    {
        string timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string nonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        return $"PCLN-{timestamp}-p{Environment.ProcessId}-{nonce}.log";
    }
}
