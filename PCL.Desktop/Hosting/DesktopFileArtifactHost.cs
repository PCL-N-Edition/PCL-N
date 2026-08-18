// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Downloads;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Application.Minecraft.Launch;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Settings.Views;
using PCL.Domain.Minecraft.Java;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Single desktop entry point for files dropped into the launcher. Core file types stay
/// in Desktop/Application; privileged runtime modules register their own narrow handlers.
/// </summary>
internal sealed class DesktopFileArtifactHost : IHostFileArtifactRegistry
{
    private readonly object _gate = new();
    private readonly List<IHostFileArtifactHandler> _handlers = [];

    public static DesktopFileArtifactHost Instance { get; } = new();

    private DesktopFileArtifactHost()
    {
        _handlers.Add(new DesktopModpackFileArtifactHandler());
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
    private readonly MinecraftModpackArchiveInstaller _installer = new();

    public string Id => "pcl.desktop.modpack";

    public int Priority => 100;

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
            string javaExecutable = await ResolveModpackJavaExecutableAsync(
                    context,
                    inspection.MinecraftVersion,
                    linked.Token)
                .ConfigureAwait(false);
            MinecraftModpackInstallResult installed = await _installer.InstallAsync(
                    new MinecraftModpackInstallRequest
                    {
                        ArchivePath = filePath,
                        MinecraftRootDirectory = context.MinecraftRootDirectory,
                        JavaExecutablePath = javaExecutable
                    },
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

    /// <summary>
    /// Forge/NeoForge installers need a real java.exe path. Bare "java" on PATH often fails on Windows.
    /// Prefer launcher catalog / selected Java, matching CE McInstall Java resolution.
    /// </summary>
    private static async Task<string> ResolveModpackJavaExecutableAsync(
        HostFileArtifactContext context,
        string? minecraftVersion,
        CancellationToken cancellationToken)
    {
        if (TryResolveExistingJava(context.JavaExecutablePath, out string fromContext))
            return MinecraftLaunchCoordinator.PreferJavaExecutable(fromContext, forceConsole: true);

        string preferred = MinecraftLaunchPlanFactory.ResolvePreferredJavaExecutablePath(forceConsole: true);
        if (TryResolveExistingJava(preferred, out string fromSettings))
            return MinecraftLaunchCoordinator.PreferJavaExecutable(fromSettings, forceConsole: true);

        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        IReadOnlyList<JavaRuntimeCandidate> catalog = await JavaRuntimeCatalog
            .LoadAsync(settings, cancellationToken)
            .ConfigureAwait(false);

        JavaVersionRange range = GuessJavaRangeForMinecraft(minecraftVersion);
        JavaRuntimeCandidate? best = JavaRuntimeCatalog.SelectBest(catalog, range);
        if (best is null)
        {
            best = catalog
                .Where(static candidate => candidate.IsAvailable && candidate.IsEnabled)
                .OrderByDescending(static candidate => candidate.Installation.MajorVersion)
                .FirstOrDefault();
        }

        if (best is null)
        {
            throw new InvalidOperationException(
                "安装整合包需要可用的 Java，但启动器没有找到已启用的 Java。" +
                "请先到设置 → 启动中添加或选择 Java，然后再试。");
        }

        PortableLog.Info(
            "ModpackInstall",
            $"整合包安装使用 Java {best.Installation.MajorVersion}：{best.Installation.JavaExecutablePath}");
        return MinecraftLaunchCoordinator.PreferJavaExecutable(
            best.Installation.JavaExecutablePath,
            forceConsole: true);
    }

    private static bool TryResolveExistingJava(string? path, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (path is "java" or "javaw" or "java.exe" or "javaw.exe")
            return false;
        return JavaRuntimeCatalog.TryResolveExistingJavaPath(path, out resolved);
    }

    private static JavaVersionRange GuessJavaRangeForMinecraft(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            return JavaVersionRange.Any;

        // Mirror JavaRuntimeRequirementResolver Minecraft base rules for installer selection.
        if (Version.TryParse(minecraftVersion.Split('-', 2)[0], out Version? version))
        {
            if (version >= new Version(1, 20, 5))
                return new JavaVersionRange(JavaVersionRange.ForMajor(21), JavaVersionRange.Any.Maximum);
            if (version >= new Version(1, 18))
                return new JavaVersionRange(JavaVersionRange.ForMajor(17), JavaVersionRange.Any.Maximum);
            if (version >= new Version(1, 17))
                return new JavaVersionRange(JavaVersionRange.ForMajor(16), JavaVersionRange.Any.Maximum);
            if (version >= new Version(1, 13))
                return new JavaVersionRange(JavaVersionRange.ForMajor(8), JavaVersionRange.Any.Maximum);
        }

        return JavaVersionRange.Any;
    }
}
