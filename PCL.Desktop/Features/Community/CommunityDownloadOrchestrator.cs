// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Net.Http;
using PCL.Application.Downloads;
using PCL.Application.Settings;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Features.Shared;

namespace PCL.Desktop.Features.Community;

/// <summary>
/// Pure-ish community resource download pipeline. UI / task manager / storage pickers
/// stay on the host via <see cref="CommunityDownloadHost"/>.
/// </summary>
internal static class CommunityDownloadOrchestrator
{
    public static async Task RunAsync(
        CommunityResourceDownloadRequest request,
        CommunityDownloadHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        LaunchInstanceInfo? instance = host.GetSelectedInstance();
        string taskId = host.CreateTaskId(request.Entry.ProjectId);
        using CancellationTokenSource cancellation = host.RegisterTrackedTask(taskId);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellation.Token,
            cancellationToken);
        CancellationToken token = linked.Token;

        string taskTitle = "下载 " + request.Entry.Title;
        DesktopFileLog.Info(
            "CommunityDownload",
            $"开始下载社区资源 {request.Entry.Title}；分类={request.Category}；来源={request.Entry.Source}；实例={instance?.Name ?? "(桌面下载)"}。");
        DesktopFileLog.Debug(
            "CommunityDownload",
            $"下载请求：ProjectId={request.Entry.ProjectId}；PreferredVersion={request.PreferredVersion?.VersionId ?? "(自动)"}；PreferredFile={request.PreferredFile?.FileName ?? "(自动)"}。");

        host.CloseDetailIfOpen();
        host.TrackTaskBegin(taskId, taskTitle, "解析下载地址");
        host.ShowHint("已开始下载：" + request.Entry.Title, false);

        try
        {
            CommunitySearchOptions downloadOptions = instance is null
                ? request.Options
                : CommunityInstanceCompatibility.Apply(request.Options, request.Category, instance);
            using CompositeCommunityResourceCatalog catalog = new();
            CommunityResourceVersion? selectedVersion = request.PreferredVersion;
            CommunityResourceDownloadFile? file = request.PreferredFile;
            if (selectedVersion is null)
            {
                IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
                        request.Entry,
                        downloadOptions,
                        token)
                    .ConfigureAwait(true);
                selectedVersion = file is null
                    ? versions.OrderByDescending(static version => version.PublishedAt ?? DateTimeOffset.MinValue)
                        .FirstOrDefault()
                    : versions.FirstOrDefault(version =>
                        string.Equals(version.VersionId, file.VersionId, StringComparison.OrdinalIgnoreCase));
            }

            file ??= selectedVersion is { Files.Count: > 0 } ? selectedVersion.Files[0] : null;
            if (file is null)
            {
                DesktopFileLog.Warn("CommunityDownload", $"未找到符合筛选条件的文件：{request.Entry.Title}");
                host.TrackTaskFailed(taskId, taskTitle, "未找到匹配当前筛选条件的版本文件。", false);
                host.ShowHint("下载失败：未找到可下载的文件", true);
                return;
            }

            selectedVersion ??= new CommunityResourceVersion(
                file.VersionId,
                file.VersionName,
                file.VersionName,
                null,
                null,
                [],
                [],
                [file]);

            LauncherSettings downloadSettings = LauncherSettingsPageBinder.LoadSettings();
            bool autoInstallDependencies = downloadSettings.GetBooleanOption(
                "ToolDownloadAutoInstallDependencies",
                LauncherSettingDefaults.GetBoolean("ToolDownloadAutoInstallDependencies", true));

            string baseDirectory;
            string? saveAsPath = null;
            if (request.SaveAs)
            {
                string? picked = await host.PickSaveAsPathAsync(request.Entry.Title, file.FileName).ConfigureAwait(true);
                if (picked is null)
                {
                    host.TrackTaskFailed(taskId, taskTitle, "已取消另存为。", true);
                    host.ShowHint("已取消另存为", false);
                    return;
                }

                saveAsPath = picked;
                baseDirectory = Path.GetDirectoryName(saveAsPath) ??
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "PCL-N Downloads");
            }
            else
            {
                baseDirectory = instance is null
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "PCL-N Downloads")
                    : await InstanceGameDirectory.ResolveAsync(instance, token).ConfigureAwait(true);
            }

            IReadOnlyList<CommunityResourceDownloadPlanItem> plan;
            if (request.Category == CommunityResourceCategory.Mod &&
                !request.SaveAs &&
                autoInstallDependencies)
            {
                host.TrackTaskBegin(taskId, taskTitle, "正在解析必需前置");
                plan = await CommunityResourceDependencyResolver.ResolveRequiredDownloadsAsync(
                        catalog,
                        request.Entry,
                        selectedVersion,
                        file,
                        downloadOptions,
                        token)
                    .ConfigureAwait(true);
            }
            else
            {
                plan = [new CommunityResourceDownloadPlanItem(request.Entry, selectedVersion, file, false)];
            }

            using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/1.0");
            string completedPath = string.Empty;
            int dependencyCount = plan.Count(static item => item.IsDependency);
            DesktopFileLog.Info(
                "CommunityDownload",
                $"下载计划已生成；资源={request.Entry.Title}；项目数={plan.Count}；必需前置={dependencyCount}；目标={baseDirectory}。");
            if (dependencyCount > 0)
            {
                host.AppendLog(
                    $"社区资源：{request.Entry.Title} 需要 {dependencyCount} 个必需前置，将自动下载。");
            }

            foreach (CommunityResourceDownloadPlanItem item in plan)
            {
                CommunityResourceCategory itemCategory = item.IsDependency
                    ? CommunityResourceCategory.Mod
                    : request.Category;
                string path = await DownloadPlanItemAsync(
                        client,
                        item,
                        itemCategory,
                        baseDirectory,
                        taskId,
                        taskTitle,
                        host,
                        token,
                        explicitTargetPath: item.IsDependency ? null : saveAsPath)
                    .ConfigureAwait(true);
                if (item.IsDependency)
                    host.AppendLog($"已安装前置：{item.Entry.Title} → {path}");
                else
                    completedPath = path;
            }

            host.TrackTaskFinished(taskId, taskTitle, "已保存到 " + completedPath);
            DesktopFileLog.Info("CommunityDownload", $"社区资源下载完成：{request.Entry.Title} -> {completedPath}");
            host.AppendLog($"社区资源已下载：{request.Entry.Title} → {completedPath}");
            host.ShowHint(request.SaveAs
                ? "已另存为：" + Path.GetFileName(completedPath)
                : request.Category == CommunityResourceCategory.World
                    ? "世界安装完成：" + Path.GetFileName(completedPath)
                    : "下载完成：" + Path.GetFileName(completedPath), false);
        }
        catch (OperationCanceledException)
        {
            DesktopFileLog.Warn("CommunityDownload", $"社区资源下载已取消：{request.Entry.Title}");
            host.TrackTaskFailed(taskId, taskTitle, "下载已取消。", true);
            host.ShowHint("下载已取消", false);
        }
        catch (Exception ex)
        {
            DesktopFileLog.Error("CommunityDownload", $"社区资源下载失败：{request.Entry.Title}", ex);
            host.TrackTaskFailed(taskId, taskTitle, ex.Message, false);
            host.ShowHint("下载失败：" + host.TruncateHint(ex.Message), true);
        }
        finally
        {
            host.UnregisterTrackedTask(taskId, cancellation);
        }
    }

    private static async Task<string> DownloadPlanItemAsync(
        HttpClient client,
        CommunityResourceDownloadPlanItem item,
        CommunityResourceCategory category,
        string baseDirectory,
        string taskId,
        string taskTitle,
        CommunityDownloadHost host,
        CancellationToken cancellationToken,
        string? explicitTargetPath = null)
    {
        string targetDirectory = CommunityDownloadPaths.ResolveDirectory(category, baseDirectory);
        string targetPath;
        if (!string.IsNullOrWhiteSpace(explicitTargetPath))
        {
            targetPath = Path.GetFullPath(explicitTargetPath);
            string? parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
        }
        else
        {
            Directory.CreateDirectory(targetDirectory);
            targetPath = Path.Combine(targetDirectory, DesktopPathHelpers.SanitizeFileName(item.File.FileName));
        }

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".PCLDownloading";
        string phase = item.IsDependency
            ? "正在下载前置 " + item.Entry.Title
            : "正在下载 " + item.File.FileName;
        host.TrackTaskBegin(taskId, taskTitle, phase);

        try
        {
            Exception? lastDownloadError = null;
            foreach (string candidateUrl in item.File.CandidateUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                    using HttpResponseMessage response = await client.GetAsync(
                            candidateUrl,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(true);
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;
                    await using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
                    await using FileStream output = new(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        useAsync: true);
                    byte[] buffer = new byte[64 * 1024];
                    long written = 0;
                    int read;
                    while ((read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(true)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(true);
                        written += read;
                        double progress = total is > 0 ? written / (double)total.Value : 0d;
                        DesktopFileLog.RealTime(
                            "CommunityDownload",
                            $"下载进度：{item.File.FileName}；字节={written}/{total?.ToString(CultureInfo.InvariantCulture) ?? "?"}；进度={progress:P1}。");
                        host.TrackTaskProgress(
                            taskId,
                            taskTitle,
                            Math.Clamp(progress, 0d, 1d),
                            $"{written.ToString(CultureInfo.InvariantCulture)} / {(total?.ToString(CultureInfo.InvariantCulture) ?? "?")} 字节");
                    }
                    lastDownloadError = null;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    lastDownloadError = ex;
                    DesktopFileLog.Warn(
                        "CommunityDownload",
                        $"下载候选失败，将尝试下一来源：{new Uri(candidateUrl).Host}。",
                        ex);
                }
            }
            if (lastDownloadError is not null || !File.Exists(temporaryPath))
                throw lastDownloadError ?? new HttpRequestException("所有下载候选均失败。");

            if (category == CommunityResourceCategory.Mod)
            {
                targetPath = MinecraftModArchiveInstaller.Install(
                    temporaryPath,
                    targetDirectory,
                    Path.GetFileName(targetPath));
            }
            else
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
            }

            if (category != CommunityResourceCategory.World)
                return targetPath;

            host.TrackTaskBegin(taskId, taskTitle, "正在安装世界");
            string installed = await MinecraftWorldArchiveInstaller
                .InstallAsync(targetPath, targetDirectory, cancellationToken)
                .ConfigureAwait(true);
            File.Delete(targetPath);
            return installed;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DesktopFileLog.Warn("CommunityDownload", $"清理临时下载文件失败：{temporaryPath}", ex);
                }
            }
        }
    }
}

/// <summary>Host UI / task-manager hooks for community downloads.</summary>
internal sealed class CommunityDownloadHost
{
    public required Func<LaunchInstanceInfo?> GetSelectedInstance { get; init; }

    public required Action CloseDetailIfOpen { get; init; }

    public required Func<string, string> CreateTaskId { get; init; }

    public required Func<string, CancellationTokenSource> RegisterTrackedTask { get; init; }

    public required Action<string, CancellationTokenSource> UnregisterTrackedTask { get; init; }

    public required Action<string, string, string> TrackTaskBegin { get; init; }

    public required Action<string, string, double, string> TrackTaskProgress { get; init; }

    public required Action<string, string, string> TrackTaskFinished { get; init; }

    public required Action<string, string, string, bool> TrackTaskFailed { get; init; }

    public required Action<string> AppendLog { get; init; }

    public required Action<string, bool> ShowHint { get; init; }

    public required Func<string, string> TruncateHint { get; init; }

    /// <summary>Returns local path or null if cancelled / unavailable.</summary>
    public required Func<string, string, Task<string?>> PickSaveAsPathAsync { get; init; }
}
