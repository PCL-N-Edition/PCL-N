// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Hosting;

/// <summary>
/// Scatter product hosts must be started by the C bootstrap (<c>PCL-N-Edition</c>), which
/// sets <c>PCL_LAUNCHER_BOOTSTRAP</c> and points <c>PCL_NATIVE_RUNTIME_DIR</c> at
/// <c>native/</c>. Starting <c>host/PCL-N-Host</c> alone skips that setup and typically
/// fails with missing libSkiaSharp after a reboot or cold PATH.
/// </summary>
internal static class LauncherBootstrapGate
{
    public const string BootstrapEnvironmentVariable = "PCL_LAUNCHER_BOOTSTRAP";
    public const string AllowDirectHostEnvironmentVariable = "PCL_ALLOW_DIRECT_HOST";

    /// <summary>
    /// Returns false when this process must exit and tell the user to start the product
    /// entry (launcher) instead of the host binary.
    /// </summary>
    public static bool TryAllowDirectStart(string[] args, out string userMessage)
    {
        ArgumentNullException.ThrowIfNull(args);
        userMessage = string.Empty;

        if (IsBootstrapPresent())
            return true;

        // Escape hatches for CI, integration tests, and advanced tooling.
        if (IsDirectHostExplicitlyAllowed())
            return true;

        // JVM host child process is not a UI host.
        if (args.Any(static a =>
                a.Contains("jvm-host", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("JvmHost", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // CI / packaging probes must run the AOT host without the C launcher.
        if (args.Contains("--validate-native-runtime", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--validate-secrets", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Single-file / portable hosts embed the native runtime zip and do not use the C launcher.
        if (HasEmbeddedNativeRuntime())
            return true;

        // Framework-dependent / IDE / `dotnet run` layouts.
        if (IsDevelopmentLayout())
            return true;

        userMessage =
            "请使用产品入口启动 PCL N，不要直接运行 host 目录下的 PCL-N-Host。\n\n" +
            "正确方式：\n" +
            "• 双击安装目录中的 PCL-N-Edition（或「PCL N」快捷方式）\n" +
            "• 散包布局下由启动器设置 native 库路径后再拉起 Host\n\n" +
            "直接运行 Host 会导致找不到 libSkiaSharp 等原生库，启动失败。\n" +
            "若为开发调试，可设置环境变量 PCL_ALLOW_DIRECT_HOST=1。";
        return false;
    }

    internal static bool IsBootstrapPresent()
    {
        string? value = Environment.GetEnvironmentVariable(BootstrapEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDirectHostExplicitlyAllowed()
    {
        string? value = Environment.GetEnvironmentVariable(AllowDirectHostEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "0", StringComparison.Ordinal) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasEmbeddedNativeRuntime()
    {
        using Stream? resource = typeof(PclEmbeddedNativeRuntime).Assembly
            .GetManifestResourceStream(PclEmbeddedNativeRuntime.ResourceName);
        return resource is not null;
    }

    internal static bool IsDevelopmentLayout(
        string? processPath = null,
        string? baseDirectory = null)
    {
        processPath ??= Environment.ProcessPath;
        baseDirectory ??= AppContext.BaseDirectory;

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string name = Path.GetFileNameWithoutExtension(processPath);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "testhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string dir = baseDirectory ?? string.Empty;
        if (dir.Length == 0)
            return false;

        // Typical SDK output: .../bin/Debug/net10.0/ or .../bin/Release/net10.0/
        string normalized = dir.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string marker = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        if (normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return true;

        // Scatter product host is named PCL-N-Host and lives under host/.
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string fileName = Path.GetFileNameWithoutExtension(processPath);
            if (string.Equals(fileName, "PCL-N-Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "PCL.Desktop", StringComparison.OrdinalIgnoreCase) &&
                IsUnderHostFolder(processPath))
            {
                // Product host without bootstrap is the case we want to block —
                // only treat as development if under bin/.
                return false;
            }
        }

        return false;
    }

    private static bool IsUnderHostFolder(string processPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(processPath));
            if (string.IsNullOrWhiteSpace(dir))
                return false;
            return string.Equals(
                Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "host",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
