// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Core.Logging;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Host-side .pnp drop handler that forwards install to the CoreCLR sidecar over IPC.
/// </summary>
internal sealed class PluginSidecarPnpFileArtifactHandler : IHostFileArtifactHandler
{
    public string Id => "pcl.plugin.sidecar.pnp";

    public int Priority => 80;

    public ValueTask<bool> CanHandleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            filePath.EndsWith(".pnp", StringComparison.OrdinalIgnoreCase) &&
            PluginSidecarSupervisor.Instance.IsAvailable);
    }

    public async ValueTask<HostFileArtifactResult> InstallAsync(
        string filePath,
        HostFileArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        PluginSidecarClient? client = PluginSidecarSupervisor.Instance.Client;
        if (client is null || !PluginSidecarSupervisor.Instance.IsAvailable)
        {
            return new HostFileArtifactResult(
                Id,
                "pnp",
                Path.GetFileName(filePath),
                "插件侧车未运行，无法安装 .pnp。",
                Installed: false);
        }

        try
        {
            PluginSidecarResult result = await client.InstallPnpAsync(filePath, cancellationToken)
                .ConfigureAwait(false);
            PortableLog.Info("PluginSidecar", "Install .pnp: " + (result.Message ?? result.Ok.ToString()));
            return new HostFileArtifactResult(
                Id,
                "pnp",
                Path.GetFileName(filePath),
                result.Message ?? (result.Ok ? "已安装插件包。" : "安装失败。"),
                Installed: result.Ok);
        }
        catch (Exception ex)
        {
            PortableLog.Warn("PluginSidecar", "Install .pnp failed: " + ex.Message);
            return new HostFileArtifactResult(
                Id,
                "pnp",
                Path.GetFileName(filePath),
                "安装失败：" + ex.Message,
                Installed: false);
        }
    }
}
