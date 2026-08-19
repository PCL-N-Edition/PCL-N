// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Downloads;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Core.Logging;
using PCL.Desktop.Features.Downloads;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Single desktop entry point for files dropped into the launcher. Core file types stay
/// in Desktop/Application; privileged runtime modules register their own narrow handlers.
/// </summary>
internal sealed class DesktopFileArtifactHost : IHostFileArtifactRegistry
{
    private readonly object _gate = new();
    private readonly List<IHostFileArtifactHandler> _handlers = [];
    private readonly DesktopModpackFileArtifactHandler _modpackHandler = new();

    public static DesktopFileArtifactHost Instance { get; } = new();

    private DesktopFileArtifactHost()
    {
        _handlers.Add(_modpackHandler);
    }

    /// <summary>
    /// Share MainWindow's <see cref="MinecraftVanillaInstallService"/> with modpack installs
    /// so version/loader installation uses the same installer controller as PageDownloadInstall.
    /// </summary>
    public void UseMinecraftInstallService(MinecraftVanillaInstallService minecraftInstallService)
    {
        ArgumentNullException.ThrowIfNull(minecraftInstallService);
        _modpackHandler.UseMinecraftInstallService(minecraftInstallService);
    }

    public IDisposable Register(IHostFileArtifactHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(handler.Id))
            throw new ArgumentException("文件处理器必须提供稳定 ID。", nameof(handler));
        lock (_gate)
        {
            if (_handlers.Any(existing => string.Equals(existing.Id, handler.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException($"文件处理器已经注册：{handler.Id}");
            _handlers.Add(handler);
        }
        PortableLog.Info("FileArtifactHost", $"已注册文件处理器：{handler.Id}；优先级={handler.Priority}。");
        return new Registration(this, handler);
    }

    public async ValueTask<HostFileArtifactResult> InstallAsync(
        string filePath,
        HostFileArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(context);
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("拖入的文件不存在或已经被移动。", filePath);

        IHostFileArtifactHandler[] snapshot;
        lock (_gate)
        {
            snapshot = _handlers
                .OrderByDescending(static handler => handler.Priority)
                .ThenBy(static handler => handler.Id, StringComparer.Ordinal)
                .ToArray();
        }
        PortableLog.Info("FileArtifactHost", $"开始识别拖入文件：{filePath}；处理器={snapshot.Length}。");
        foreach (IHostFileArtifactHandler handler in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool canHandle;
            try
            {
                canHandle = await handler.CanHandleAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                PortableLog.Debug("FileArtifactHost", $"处理器 {handler.Id} 无法识别 {filePath}：{ex.Message}");
                continue;
            }
            if (!canHandle)
                continue;

            PortableLog.Info("FileArtifactHost", $"文件由 {handler.Id} 接管：{filePath}");
            return await handler.InstallAsync(filePath, context, cancellationToken).ConfigureAwait(false);
        }

        throw new NotSupportedException($"无法识别文件类型：{Path.GetFileName(filePath)}");
    }

    private void Unregister(IHostFileArtifactHandler handler)
    {
        lock (_gate)
            _handlers.Remove(handler);
        PortableLog.Info("FileArtifactHost", $"已注销文件处理器：{handler.Id}。");
    }

    private sealed class Registration(DesktopFileArtifactHost owner, IHostFileArtifactHandler handler) : IDisposable
    {
        private DesktopFileArtifactHost? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unregister(handler);
        }
    }
}

internal sealed class DesktopModpackFileArtifactHandler : IHostFileArtifactHandler
{
    private MinecraftModpackArchiveInstaller _installer = new();

    public string Id => "pcl.desktop.modpack";

    public int Priority => 100;

    public void UseMinecraftInstallService(MinecraftVanillaInstallService minecraftInstallService)
    {
        ArgumentNullException.ThrowIfNull(minecraftInstallService);
        // Recreate so the modpack version/loader stage calls the same service instance
        // as MainWindow.StartInstallAsync / PageDownloadInstall.
        _installer = new MinecraftModpackArchiveInstaller(minecraftInstallService);
        PortableLog.Info(
            "FileArtifactHost",
            "整合包版本安装已复用实例安装的 MinecraftVanillaInstallService。");
    }

    public ValueTask<bool> CanHandleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(filePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(false);
        }

        return new ValueTask<bool>(Task.Run(() => MinecraftModpackArchiveInstaller.CanInstall(filePath), cancellationToken));
    }

    public async ValueTask<HostFileArtifactResult> InstallAsync(
        string filePath,
        HostFileArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        MinecraftModpackInspection inspection = await Task.Run(() => MinecraftModpackArchiveInstaller.Inspect(filePath), cancellationToken)
            .ConfigureAwait(false);
        using IHostBackgroundTask backgroundTask = DesktopHostBackgroundTasks.Instance.Begin(
            "安装整合包 · " + inspection.Name,
            openTaskManager: true);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            backgroundTask.Token);
        Progress<MinecraftModpackInstallProgress> progress = new(value =>
        {
            backgroundTask.Report(new HostBackgroundTaskProgress(
                value.Stage,
                value.Detail,
                value.Progress,
                value.CompletedFiles,
                value.TotalFiles,
                value.SpeedBytesPerSecond));
        });
        try
        {
            // Same Java / root / threads / download-source path as PageDownloadInstall.
            MinecraftModpackInstallRequest installRequest =
                await DesktopMinecraftInstallCoordinator.BuildModpackInstallRequestAsync(
                        filePath,
                        context.MinecraftRootDirectory,
                        getDefaultRoot: LaunchInstanceDiscovery.GetCurrentMinecraftRoot,
                        preferredJavaHint: context.JavaExecutablePath,
                        minecraftVersionHint: inspection.MinecraftVersion,
                        linked.Token)
                    .ConfigureAwait(false);
            MinecraftModpackInstallResult installed = await _installer.InstallAsync(
                    installRequest,
                    progress,
                    linked.Token)
                .ConfigureAwait(false);
            backgroundTask.Complete("整合包安装完成");
            return new HostFileArtifactResult(
                Id,
                "modpack",
                installed.Name,
                $"已安装整合包 {installed.Name}（{installed.Version}）\n实例名称：{installed.VersionId}",
                Installed: true,
                RefreshInstances: true);
        }
        catch (OperationCanceledException)
        {
            backgroundTask.Fail("整合包安装已取消", canceled: true);
            throw;
        }
        catch (Exception ex)
        {
            backgroundTask.Fail("整合包安装失败：" + ex.Message);
            throw;
        }
    }
}
