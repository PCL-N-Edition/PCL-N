// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Updates;
using PCL.Core.Logging;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Session helper for independent Sidecar CAS checks/installs. Host updates remain
/// owned by <see cref="LauncherUpdateCoordinator"/>; this sibling never replaces the AOT body.
/// </summary>
internal sealed class PluginSidecarUpdateCoordinator : IDisposable
{
    public static PluginSidecarUpdateCoordinator Current { get; } = new();

    private readonly PluginSidecarUpdateService _service = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public async Task<PluginSidecarUpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PluginSidecarInstallIdentity identity = PluginSidecarUpdateInstaller.ResolveLocalIdentity();
        return await _service.CheckAsync(identity, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> DownloadAndInstallAsync(
        PluginSidecarUpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);
        if (!update.Success || !update.IsUpdateAvailable || string.IsNullOrWhiteSpace(update.PackageUrl))
            throw new InvalidOperationException("没有可安装的 Sidecar 更新包。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string cacheRoot = Path.Combine(
                LauncherPathLayout.ResolveCacheDirectory(),
                "sidecar-updates");
            Directory.CreateDirectory(cacheRoot);
            string zipPath = Path.Combine(
                cacheRoot,
                (update.ReleaseName ?? "sidecar") + "-" +
                Path.GetFileName(new Uri(update.PackageUrl).AbsolutePath));

            PortableLog.Info("PluginSidecarUpdate", "开始下载 Sidecar 更新包：" + update.PackageUrl);
            await _service.DownloadPackageAsync(update.PackageUrl, zipPath, progress, cancellationToken)
                .ConfigureAwait(false);
            string installed = await PluginSidecarUpdateInstaller
                .InstallFromZipAsync(zipPath, update, cancellationToken)
                .ConfigureAwait(false);
            PortableLog.Info("PluginSidecarUpdate", "Sidecar 更新安装完成：" + installed);
            return installed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.Dispose();
        _gate.Dispose();
    }
}
