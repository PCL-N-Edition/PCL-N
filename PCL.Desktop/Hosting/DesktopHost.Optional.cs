// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting;
using PCL.Core.Logging;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Host optional runtime: starts the out-of-process plugin sidecar when present.
/// Does not load plugin IL into the host (AOT-safe).
/// </summary>
internal static partial class DesktopHost
{
    static partial void RegisterOptionalModules(PclHostBuilder builder)
    {
        // Sidecar owns HostModule registration in-process to itself.
        // Host only keeps a thin IPC client (no plugin module types here).
        _ = builder;
    }

    static partial void InitializeOptionalRuntime(IPclHost host)
    {
        _ = host;
        // Fire-and-forget warm start; shell must not block on missing/failing sidecar.
        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(false);
                PortableLog.Info(
                    "DesktopHost",
                    ok ? "Plugin sidecar started." : "Plugin sidecar not started (missing or failed).");
            }
            catch (Exception ex)
            {
                PortableLog.Warn("DesktopHost", "Plugin sidecar warm-start failed: " + ex.Message);
            }
        });
    }
}
