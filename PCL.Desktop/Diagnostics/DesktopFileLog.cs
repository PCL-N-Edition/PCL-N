// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Diagnostics;

public static class DesktopFileLog
{
    private static readonly object WriteLock = new();
    private static readonly HashSet<string> InitializedFiles = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string CurrentLogPath => Path.Combine(
        LauncherSettingsPageBinder.CreateDataDirectory(),
        "Logs",
        $"PCLN-{DateTime.Now:yyyyMMdd}.log");

    public static void Initialize()
    {
        string path = CurrentLogPath;
        lock (WriteLock)
        {
            if (!InitializedFiles.Add(path))
                return;

            string version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";
            WriteCore(path, $"========== PCL N 会话开始（PID {Environment.ProcessId}） ==========");
            WriteCore(path, $"[Startup] PCL N {version}；进程：{Environment.ProcessPath ?? "unknown"}");
            WriteCore(
                path,
                $"[System] {RuntimeInformation.OSDescription}；系统架构：{RuntimeInformation.OSArchitecture}；进程架构：{RuntimeInformation.ProcessArchitecture}");
            WriteCore(
                path,
                $"[Runtime] {RuntimeInformation.FrameworkDescription}；CPU：{Environment.ProcessorCount}；GC：{(GCSettings.IsServerGC ? "Server" : "Workstation")}");
            WriteCore(
                path,
                $"[Locale] UI={CultureInfo.CurrentUICulture.Name}；区域={CultureInfo.CurrentCulture.Name}；时区={TimeZoneInfo.Local.Id}");
            WriteCore(path, "[Display] " + DescribeDesktopSession());
        }
    }

    public static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        string path = CurrentLogPath;
        lock (WriteLock)
        {
            if (InitializedFiles.Add(path))
                WriteCore(path, "PCL N 日志会话已开始。");
            WriteCore(path, message);
        }
    }

    private static void WriteCore(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
            using FileStream stream = new(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write('[');
            writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write("] ");
            writer.WriteLine(message.ReplaceLineEndings(" "));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Log] 写入日志失败：" + ex.Message);
        }
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
}
