// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting;
using PCL.Core.Logging;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Host optional runtime: out-of-process plugin sidecar (AOT-safe).
/// Plugin settings pages are NOT hardcoded — they are injected via UI data-chain after sidecar starts.
/// </summary>
internal static partial class DesktopHost
{
    private static IDisposable? _pnpHandlerRegistration;

    static partial void RegisterOptionalModules(PclHostBuilder builder)
    {
        // No host-hardcoded plugin pages. Sidecar ui.manifest injects groups/pages at runtime.
        _ = builder;
    }

    static partial void InitializeOptionalRuntime(IPclHost host)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(false);
                if (!ok)
                {
                    PortableLog.Info("DesktopHost", "Plugin sidecar not started.");
                    return;
                }

                _pnpHandlerRegistration ??= DesktopFileArtifactHost.Instance.Register(
                    new PluginSidecarPnpFileArtifactHandler());
                await PluginSidecarUiInjector.InjectAsync(host).ConfigureAwait(false);
                PortableLog.Info("DesktopHost", "Plugin sidecar started; UI data-chain injected.");
            }
            catch (Exception ex)
            {
                PortableLog.Warn("DesktopHost", "Plugin sidecar warm-start failed: " + ex.Message);
            }
        });
    }

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
