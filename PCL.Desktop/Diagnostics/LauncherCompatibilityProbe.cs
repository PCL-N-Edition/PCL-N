// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.InteropServices;
using Avalonia.Platform;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Hosting;
using PCL.Desktop.Hosting.PluginSidecar;
using PCL.Desktop.Localization;
using PCL.Desktop.Paths;
using PCL.Desktop.Platform;

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Runs a full low-level dependency self-check used by OOBE and Settings → Compatibility.
/// Safe to call after <see cref="PclEmbeddedNativeRuntime.EnsureInstalled"/>.
/// </summary>
internal static class LauncherCompatibilityProbe
{
    private static readonly object Gate = new();
    private static CompatibilityReport? _last;

    public static CompatibilityReport? LastReport
    {
        get
        {
            lock (Gate)
                return _last;
        }
    }

    /// <summary>Execute all probes and cache the report.</summary>
    public static CompatibilityReport Run(LauncherSettings? settings = null)
    {
        settings ??= SafeLoadSettings();
        List<CompatibilityCheckItem> items =
        [
            CheckPlatform(),
            CheckAvaloniaAssets(),
            CheckNativeSkia(),
            CheckNativeHarfBuzz(),
            CheckDataDirectoryWritable(),
            CheckCacheDirectoryWritable(),
            CheckHardwareAcceleration(settings),
            CheckDisplayBackend(),
            CheckPluginSidecar()
        ];

        CompatibilityReport report = new(DateTimeOffset.Now, items);
        lock (Gate)
            _last = report;

        PortableLog.Info(
            "Compat",
            $"自检完成：ok={report.OkCount}；issues={report.IssueCount}；canRun={report.CanRun}。");
        foreach (CompatibilityCheckItem item in items)
        {
            string level = item.Status switch
            {
                CompatibilityStatus.Fatal => "FATAL",
                CompatibilityStatus.Unavailable => "UNAVAIL",
                CompatibilityStatus.Degraded => "DEGRADED",
                _ => "OK"
            };
            PortableLog.Info("Compat", $"[{level}] {item.Id}: {item.Title} — {item.Detail}");
        }

        return report;
    }

    public static string StatusLabel(CompatibilityStatus status) => status switch
    {
        CompatibilityStatus.Ok => AvaloniaLocalizationManager.GetText("Compat.Status.Ok", "正常"),
        CompatibilityStatus.Degraded => AvaloniaLocalizationManager.GetText("Compat.Status.Degraded", "降级可用"),
        CompatibilityStatus.Unavailable => AvaloniaLocalizationManager.GetText("Compat.Status.Unavailable", "不可用"),
        CompatibilityStatus.Fatal => AvaloniaLocalizationManager.GetText("Compat.Status.Fatal", "致命"),
        _ => status.ToString()
    };

    private static LauncherSettings SafeLoadSettings()
    {
        try
        {
            return LauncherSettingsPageBinder.LoadSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    private static CompatibilityCheckItem CheckPlatform()
    {
        if (DesktopPlatformApi.IsSupportedDesktopPlatform)
        {
            return Item(
                "platform",
                "操作系统",
                $"{RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}",
                CompatibilityStatus.Ok,
                required: true);
        }

        return Item(
            "platform",
            "操作系统",
            "当前平台不受支持。PCL N 需要 Windows、Linux 或 macOS 桌面环境。",
            CompatibilityStatus.Fatal,
            required: true);
    }

    private static CompatibilityCheckItem CheckAvaloniaAssets()
    {
        try
        {
            var loader = new StandardAssetLoader(typeof(App).Assembly);
            string[] required =
            [
                "avares://PCL.Desktop/Assets/icon.png",
                "avares://PCL.Desktop/Assets/icon.ico"
            ];
            List<string> missing = [];
            foreach (string path in required)
            {
                if (!loader.Exists(new Uri(path, UriKind.Absolute)))
                    missing.Add(path);
            }

            if (missing.Count == 0)
            {
                return Item(
                    "assets",
                    "内置界面资源",
                    "图标与 Avalonia 资源可加载。",
                    CompatibilityStatus.Ok,
                    required: true);
            }

            return Item(
                "assets",
                "内置界面资源",
                "缺少：" + string.Join(", ", missing),
                CompatibilityStatus.Fatal,
                required: true);
        }
        catch (Exception ex)
        {
            return Item(
                "assets",
                "内置界面资源",
                "探测失败：" + ex.Message,
                CompatibilityStatus.Fatal,
                required: true);
        }
    }

    private static CompatibilityCheckItem CheckNativeSkia()
    {
        try
        {
            _ = SkiaSharp.SKImageInfo.PlatformColorType;
            string? runtimeDir = PclEmbeddedNativeRuntime.InstalledDirectory;
            string detail = string.IsNullOrWhiteSpace(runtimeDir)
                ? "SkiaSharp 平台色型可用（开发/非嵌入运行时布局）。"
                : "SkiaSharp 可用；原生库目录=" + runtimeDir;
            return Item("skia", "Skia 渲染库", detail, CompatibilityStatus.Ok, required: true);
        }
        catch (Exception ex)
        {
            return Item(
                "skia",
                "Skia 渲染库",
                "无法加载 SkiaSharp / libSkiaSharp：" + ex.Message +
                " 界面无法绘制，本软件不可用。",
                CompatibilityStatus.Fatal,
                required: true);
        }
    }

    private static CompatibilityCheckItem CheckNativeHarfBuzz()
    {
        try
        {
            // SKTypeface.Default exercises the native font stack (Skia + HarfBuzz on Avalonia).
            using SkiaSharp.SKTypeface face = SkiaSharp.SKTypeface.Default;
            _ = face.FamilyName;

            string? runtimeDir = PclEmbeddedNativeRuntime.InstalledDirectory;
            if (!string.IsNullOrWhiteSpace(runtimeDir))
            {
                bool hasLib = Directory.EnumerateFiles(
                        runtimeDir,
                        "*HarfBuzz*",
                        SearchOption.AllDirectories)
                    .Any();
                if (!hasLib)
                {
                    return Item(
                        "harfbuzz",
                        "HarfBuzz 字体整形",
                        "字体栈可创建默认字型，但嵌入运行时目录中未找到 HarfBuzz 原生库文件。",
                        CompatibilityStatus.Degraded,
                        required: false);
                }
            }

            return Item(
                "harfbuzz",
                "HarfBuzz 字体整形",
                "字体整形链路可用。",
                CompatibilityStatus.Ok,
                required: true);
        }
        catch (Exception ex)
        {
            return Item(
                "harfbuzz",
                "HarfBuzz 字体整形",
                "无法初始化字体栈：" + ex.Message,
                CompatibilityStatus.Fatal,
                required: true);
        }
    }

    private static CompatibilityCheckItem CheckDataDirectoryWritable()
    {
        string path = LauncherPathLayout.ResolveDataDirectory();
        return ProbeWritableDirectory(
            "data-dir",
            "数据目录可写",
            path,
            required: true);
    }

    private static CompatibilityCheckItem CheckCacheDirectoryWritable()
    {
        string path = LauncherPathLayout.ResolveCacheDirectory();
        return ProbeWritableDirectory(
            "cache-dir",
            "缓存目录可写",
            path,
            required: true);
    }

    private static CompatibilityCheckItem ProbeWritableDirectory(
        string id,
        string title,
        string directory,
        bool required)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".pcln-compat-write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Item(id, title, directory, CompatibilityStatus.Ok, required);
        }
        catch (Exception ex)
        {
            return Item(
                id,
                title,
                $"{directory} — 不可写：{ex.Message}",
                required ? CompatibilityStatus.Fatal : CompatibilityStatus.Unavailable,
                required);
        }
    }

    private static CompatibilityCheckItem CheckHardwareAcceleration(LauncherSettings settings)
    {
        bool userDisabled = settings.GetBooleanOption(
            "SystemDisableHardwareAcceleration",
            LauncherSettingDefaults.GetBoolean("SystemDisableHardwareAcceleration"));

        if (userDisabled)
        {
            return Item(
                "gpu",
                "硬件加速",
                "已按兼容性设置关闭硬件加速，使用软件渲染。",
                CompatibilityStatus.Degraded,
                required: false,
                mitigationKey: "SystemDisableHardwareAcceleration",
                mitigationLabel: AvaloniaLocalizationManager.GetText(
                    "Compat.Mitigation.DisableGpu",
                    "关闭硬件加速（软件渲染）"));
        }

        // Soft probe: we cannot fully open a GPU context before the platform is live,
        // but we surface OS/display hints and always offer software rendering as fallback.
        string detail = OperatingSystem.IsLinux()
            ? "默认尝试 GPU（GL/Vulkan）并回退软件渲染。若出现空白或闪烁界面，请启用软件渲染。"
            : "默认尝试 GPU 加速。若界面异常，可启用软件渲染。";

        return Item(
            "gpu",
            "硬件加速",
            detail,
            CompatibilityStatus.Ok,
            required: false,
            mitigationKey: "SystemDisableHardwareAcceleration",
            mitigationLabel: AvaloniaLocalizationManager.GetText(
                "Compat.Mitigation.DisableGpu",
                "关闭硬件加速（软件渲染）"));
    }

    private static CompatibilityCheckItem CheckDisplayBackend()
    {
        if (!OperatingSystem.IsLinux())
        {
            return Item(
                "display",
                "显示后端",
                OperatingSystem.IsWindows() ? "Win32 / ANGLE 路径" : "平台默认显示后端",
                CompatibilityStatus.Ok,
                required: false);
        }

        string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        string? x11 = Environment.GetEnvironmentVariable("DISPLAY");
        bool preferWayland = DesktopDisplayBackendSelector.ShouldUseWaylandForCurrentProcess();

        if (string.IsNullOrWhiteSpace(wayland) && string.IsNullOrWhiteSpace(x11))
        {
            return Item(
                "display",
                "显示后端",
                "未检测到 WAYLAND_DISPLAY 或 DISPLAY。若在无图形会话中运行，界面无法启动。",
                CompatibilityStatus.Fatal,
                required: true);
        }

        string mode = preferWayland ? "Wayland" : "X11/XWayland";
        return Item(
            "display",
            "显示后端",
            $"会话探测：{(string.IsNullOrWhiteSpace(wayland) ? "无 Wayland" : "Wayland")} / " +
            $"{(string.IsNullOrWhiteSpace(x11) ? "无 X11" : "X11")}；启动器将使用 {mode}。",
            CompatibilityStatus.Ok,
            required: false);
    }

    private static CompatibilityCheckItem CheckPluginSidecar()
    {
        try
        {
            bool available = PluginSidecarSupervisor.Instance.IsAvailable;
            if (available)
            {
                return Item(
                    "sidecar",
                    "插件侧车",
                    "侧车进程可用（在线账户 / 插件平台）。",
                    CompatibilityStatus.Ok,
                    required: false);
            }

            return Item(
                "sidecar",
                "插件侧车",
                "侧车尚未就绪或未嵌入。在线账户与第三方插件可能不可用；启动器核心功能仍可使用。",
                CompatibilityStatus.Unavailable,
                required: false);
        }
        catch (Exception ex)
        {
            return Item(
                "sidecar",
                "插件侧车",
                "探测失败：" + ex.Message,
                CompatibilityStatus.Unavailable,
                required: false);
        }
    }

    private static CompatibilityCheckItem Item(
        string id,
        string title,
        string detail,
        CompatibilityStatus status,
        bool required,
        string? mitigationKey = null,
        string? mitigationLabel = null) =>
        new(id, title, detail, status, required, mitigationKey, mitigationLabel);
}
