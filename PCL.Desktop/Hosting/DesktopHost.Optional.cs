// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Host optional runtime: out-of-process plugin sidecar (AOT-safe).
/// Host settings page drives catalog/install over IPC — no plugin independent window.
/// </summary>
internal static partial class DesktopHost
{
    private static IDisposable? _pnpHandlerRegistration;

    static partial void RegisterOptionalModules(PclHostBuilder builder)
    {
        builder.AddSettingsPageGroup(new HostSettingsPageGroupDescriptor(
            "pcl.settings.plugin-sidecar",
            "插件平台",
            "lucide/plug",
            Order: 320,
            Description: "进程外侧车中的第三方 .pnp 与平台状态。")
        {
            LocalizedTitle = HostLocalizedText.FromResource("PluginSidecar.Group.Title", "插件平台")
        });

        builder.AddSettingsPage(new HostSettingsPageDescriptor(
            "pcl.settings.plugin-sidecar.status",
            "侧车与目录",
            "lucide/box",
            "插件侧车",
            "查看 CoreCLR 插件侧车状态，列出已安装 .pnp，并从宿主安装包。",
            [])
        {
            GroupId = "pcl.settings.plugin-sidecar",
            Order = 10,
            PageFactory = static () => new PageSetupPluginSidecar(),
            LocalizedTitle = HostLocalizedText.FromResource("PluginSidecar.Status.Title", "侧车与目录"),
            LocalizedHeading = HostLocalizedText.FromResource("PluginSidecar.Status.Heading", "插件侧车"),
            LocalizedDescription = HostLocalizedText.FromResource(
                "PluginSidecar.Status.Description",
                "查看 CoreCLR 插件侧车状态，列出已安装 .pnp，并从宿主安装包。")
        });
    }

    static partial void InitializeOptionalRuntime(IPclHost host)
    {
        _ = host;
        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(false);
                if (ok)
                {
                    _pnpHandlerRegistration ??= DesktopFileArtifactHost.Instance.Register(
                        new PluginSidecarPnpFileArtifactHandler());
                }

                PortableLog.Info(
                    "DesktopHost",
                    ok ? "Plugin sidecar started; .pnp drop handler registered." : "Plugin sidecar not started.");
            }
            catch (Exception ex)
            {
                PortableLog.Warn("DesktopHost", "Plugin sidecar warm-start failed: " + ex.Message);
            }
        });
    }

    /// <summary>Called on app exit to stop the sidecar cleanly.</summary>
    public static void ShutdownOptionalRuntime()
    {
        try
        {
            _pnpHandlerRegistration?.Dispose();
            _pnpHandlerRegistration = null;
            PluginSidecarSupervisor.Instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            PortableLog.Warn("DesktopHost", "Plugin sidecar shutdown: " + ex.Message);
        }
    }
}
