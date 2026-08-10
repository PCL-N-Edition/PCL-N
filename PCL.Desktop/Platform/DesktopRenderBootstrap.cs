// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using PCL.Application.Settings;
using PCL.Desktop.Diagnostics;
using PCL.UI.Next;

namespace PCL.Desktop.Platform;

/// <summary>
/// Explicit GPU / Skia platform bootstrap for Avalonia 12 (ANGLE/Vulkan/GL).
/// Separate from the experimental ECS UI architecture in <c>PCL.UI.Next</c>.
/// </summary>
internal static class DesktopRenderBootstrap
{
    /// <summary>Skia GPU resource cache (~256 MB). Larger cache reduces texture thrash during motion.</summary>
    private const long GpuResourceBudgetBytes = 256L * 1024 * 1024;

    public static AppBuilder Configure(AppBuilder builder, LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);

        bool disableGpu = settings.GetBooleanOption(
            "SystemDisableHardwareAcceleration",
            LauncherSettingDefaults.GetBoolean("SystemDisableHardwareAcceleration"));

        // Skia GPU resource pool — applies to all platforms that use Skia.
        builder = builder.With(new SkiaOptions
        {
            MaxGpuResourceSizeBytes = disableGpu ? 32L * 1024 * 1024 : GpuResourceBudgetBytes,
            // Cocoa composites the whole transparent top-level through Skia.
            // Without a save-layer, nested translucent brushes can be flattened
            // as opaque. Other platforms retain the lighter rendering path.
            UseOpacitySaveLayer = OperatingSystem.IsMacOS()
        });

        if (OperatingSystem.IsWindows())
            builder = ConfigureWindows(builder, disableGpu);
        else if (OperatingSystem.IsLinux())
            builder = ConfigureLinux(builder, disableGpu);
        else if (OperatingSystem.IsMacOS())
            builder = ConfigureMac(builder, disableGpu);

        DesktopFileLog.Info(
            "Render",
            disableGpu
                ? "渲染：已按设置关闭硬件加速（Software 优先）。"
                : "渲染：已启用 GPU 优先路径（ANGLE/Vulkan/GL → Software 回退）；Skia GPU 缓存≈256MB。");

        // Experimental ECS UI architecture (PCL.UI.Next) — not GPU backend selection.
        bool wantEcsUi = settings.GetBooleanOption(
            LauncherSettingKeys.ExperimentalNextRenderBackend,
            LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalNextRenderBackend.Value));
        NextUiRenderMode uiMode = NextUiRenderRuntime.Resolve(wantEcsUi);
        DesktopFileLog.Info("Render", "界面架构：" + NextUiRenderRuntime.Describe(uiMode) + "。");
        if (wantEcsUi && !NextRenderAvailability.IsImplemented)
        {
            DesktopFileLog.Info(
                "Render",
                "设置中请求了基于 ECS 的新型 UI 渲染后端，但该实验项尚未实现；本进程仍使用经典控件树。");
        }

        return builder;
    }

    private static AppBuilder ConfigureWindows(AppBuilder builder, bool disableGpu)
    {
        if (disableGpu)
        {
            return builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
                OverlayPopups = true,
                DpiAwareness = Win32DpiAwareness.PerMonitorDpiAware
            });
        }

        // GPU-first: ANGLE (D3D11) is the most reliable Win desktop path for Skia.
        // Composition prefers WinUI / DirectComposition for retained GPU layers (opacity/transform).
        return builder.With(new Win32PlatformOptions
        {
            RenderingMode =
            [
                Win32RenderingMode.AngleEgl,
                Win32RenderingMode.Vulkan,
                Win32RenderingMode.Wgl,
                Win32RenderingMode.Software
            ],
            CompositionMode =
            [
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.LowLatencyDxgiSwapChain,
                Win32CompositionMode.RedirectionSurface
            ],
            OverlayPopups = true,
            DpiAwareness = Win32DpiAwareness.PerMonitorDpiAware,
            // Render off the UI thread when the platform allows — keeps input + animation timers responsive.
            ShouldRenderOnUIThread = false
        });
    }

    private static AppBuilder ConfigureLinux(AppBuilder builder, bool disableGpu)
    {
        if (disableGpu)
        {
            return builder.With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Software],
                OverlayPopups = true,
                ShouldRenderOnUIThread = false
            });
        }

        return builder.With(new X11PlatformOptions
        {
            RenderingMode =
            [
                X11RenderingMode.Glx,
                X11RenderingMode.Egl,
                X11RenderingMode.Vulkan,
                X11RenderingMode.Software
            ],
            OverlayPopups = true,
            ShouldRenderOnUIThread = false
        });
    }

    private static AppBuilder ConfigureMac(AppBuilder builder, bool disableGpu)
    {
        // AvaloniaNative options vary by version; keep Skia GPU budget (already applied) and avoid forcing software.
        _ = disableGpu;
        return builder;
    }

    /// <summary>
    /// Hints for compositor-friendly drawing on animated surfaces (opacity / transform).
    /// Safe no-ops when the platform ignores individual options.
    /// </summary>
    public static void ApplyCompositorHints(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        // Geometry edges only — do NOT force HighQuality bitmap filtering on the whole tree.
        // That inherits into skin heads / pixel icons and softens them.
        // Pixel art controls set BitmapInterpolationMode.None locally.
        RenderOptions.SetEdgeMode(visual, EdgeMode.Antialias);
    }
}
