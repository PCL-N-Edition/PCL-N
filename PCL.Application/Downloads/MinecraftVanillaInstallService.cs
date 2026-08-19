// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Application.Launching;
using PCL.Application.Minecraft.Assets;
using PCL.Application.Minecraft.Downloads;
using PCL.Application.Minecraft.Launch.Libraries;
using PCL.Core.IO.Download;
using PCL.Core.IO.Net;
using PCL.Core.Utils.Hash;
using PCL.Core.Logging;

namespace PCL.Application.Downloads;

public sealed record MinecraftVersionManifestEntry(
    string Id,
    string Type,
    string Url,
    DateTimeOffset? ReleaseTime);

public sealed record MinecraftInstallRequest
{
    public required string VersionId { get; init; }
    public string? BaseVersionId { get; init; }
    public required string VersionJsonUrl { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public bool PreferOfficialSource { get; init; } = true;
    public int DownloadThreadLimit { get; init; } = 64;
    public MinecraftLoaderInstallRequest? Loader { get; init; }
    public IReadOnlyList<MinecraftInstallAddonRequest> Addons { get; init; } = [];
    public bool ReplaceExistingVersion { get; init; }
    public string JavaExecutablePath { get; init; } = "java";
    /// <summary>Extra JVM flags for Forge/NeoForge/OptiFine installers (e.g. HTTP proxy).</summary>
    public string? LoaderExtraJvmArguments { get; init; }
}

public sealed record MinecraftInstallProgress
{
    public required string Stage { get; init; }
    public string Detail { get; init; } = string.Empty;
    public double Progress { get; init; }
    public int CompletedFiles { get; init; }
    public int TotalFiles { get; init; }
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; } = -1;
    public long SpeedBytesPerSecond { get; init; }
    public int ActiveThreads { get; init; }
    public int ThreadLimit { get; init; } = 1;
    public IReadOnlyList<MinecraftInstallStepProgress> Steps { get; init; } = [];
}

public enum MinecraftInstallStepState
{
    Waiting,
    Running,
    Finished,
    Failed
}

public sealed record MinecraftInstallStepProgress(
    string Name,
    string Detail,
    double Progress,
    MinecraftInstallStepState State);

public sealed record MinecraftInstallResult(
    string VersionId,
    string MinecraftRootDirectory,
    string InstanceDirectory,
    string VersionJsonPath);

public sealed class MinecraftVanillaInstallService
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const int DefaultDownloadThreadLimit = 64;
    private const int MaxDownloadThreadLimit = 256;
    private static readonly SearchValues<char> InvalidVersionIdCharacters =
        SearchValues.Create("<>:\"/\\|?*");
    private readonly HttpClient _httpClient;
    private readonly IMinecraftLoaderMetadataService _loaderMetadataService;
    private readonly IMinecraftExternalLoaderInstaller _externalLoaderInstaller;
    private readonly DownloadService _downloadService = new();

    public MinecraftVanillaInstallService(
        HttpClient? httpClient = null,
        IMinecraftLoaderMetadataService? loaderMetadataService = null,
        IMinecraftExternalLoaderInstaller? externalLoaderInstaller = null)
    {
        _httpClient = httpClient ?? PortableHttp.Client;
        _loaderMetadataService = loaderMetadataService ?? new MinecraftLoaderMetadataService(_httpClient);
        _externalLoaderInstaller = externalLoaderInstaller ?? new MinecraftExternalLoaderInstaller();
    }

    public async Task<IReadOnlyList<MinecraftVersionManifestEntry>> GetVersionManifestAsync(
        bool preferOfficialSource = true,
        CancellationToken cancellationToken = default)
    {
        PortableLog.Info("MinecraftMetadata", $"开始获取版本清单；优先官方源={preferOfficialSource}。");
        string manifestJson = await GetStringWithFailoverAsync(
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(VersionManifestUrl, preferOfficialSource),
                cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(manifestJson);
        if (!document.RootElement.TryGetProperty("versions", out JsonElement versions) ||
            versions.ValueKind != JsonValueKind.Array)
            return [];

        List<MinecraftVersionManifestEntry> result = [];
        foreach (JsonElement version in versions.EnumerateArray())
        {
            string? id = TryReadString(version, "id");
            string? type = TryReadString(version, "type");
            string? url = TryReadString(version, "url");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(type) ||
                string.IsNullOrWhiteSpace(url))
                continue;

            result.Add(new MinecraftVersionManifestEntry(
                id,
                type,
                url,
                TryReadDate(version, "releaseTime")));
        }

        PortableLog.Info("MinecraftMetadata", $"版本清单获取完成；有效版本数={result.Count}。");
        return result;
    }

    public async Task<MinecraftInstallResult> InstallAsync(
        MinecraftInstallRequest request,
        IProgress<MinecraftInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionJsonUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftRootDirectory);
        ValidateVersionId(request.VersionId, nameof(request.VersionId));

        string minecraftRoot = Path.GetFullPath(request.MinecraftRootDirectory);
        string baseVersionId = string.IsNullOrWhiteSpace(request.BaseVersionId)
            ? request.VersionId
            : request.BaseVersionId.Trim();
        ValidateVersionId(baseVersionId, nameof(request.BaseVersionId));
        bool installsLoader = request.Loader is not null;
        if (installsLoader && string.Equals(request.VersionId, baseVersionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("加载器实例名称不能与原版版本名称相同。");
        }

        string vanillaInstallId = installsLoader ? baseVersionId : request.VersionId;
        string vanillaInstanceDirectory = Path.Combine(minecraftRoot, "versions", vanillaInstallId);
        string vanillaVersionJsonPath = Path.Combine(vanillaInstanceDirectory, vanillaInstallId + ".json");
        VersionCoreBackup? coreBackup = request.ReplaceExistingVersion
            ? VersionCoreBackup.Create(minecraftRoot, request.VersionId)
            : null;
        bool installCompleted = false;
        PortableLog.Info(
            "MinecraftInstall",
            $"开始安装版本 {request.VersionId}；基础版本={baseVersionId}；加载器={request.Loader?.Kind.ToString() ?? "无"}；附加组件={request.Addons.Count}。");
        PortableLog.Debug(
            "MinecraftInstall",
            $"安装参数：Root={minecraftRoot}；目标实例={vanillaInstanceDirectory}；线程={request.DownloadThreadLimit}；" +
            $"优先官方源={request.PreferOfficialSource}；替换现有版本={request.ReplaceExistingVersion}。");
        try
        {
            Directory.CreateDirectory(vanillaInstanceDirectory);
            int downloadThreadLimit = NormalizeDownloadThreadLimit(request.DownloadThreadLimit);

            progress?.Report(CreateProgress("准备安装", request.VersionId, 0d, 0, 1, 0, downloadThreadLimit));
            await DownloadIfNeededAsync(
                    MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(request.VersionJsonUrl, request.PreferOfficialSource),
                    vanillaVersionJsonPath,
                    expectedSize: -1,
                    expectedSha1: null,
                    "下载版本描述",
                    0,
                    1,
                    progress,
                    activeThreads: 1,
                    threadLimit: downloadThreadLimit,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            JsonObject versionJson = await ReadJsonObjectAsync(vanillaVersionJsonPath, cancellationToken).ConfigureAwait(false);
            await NormalizeVersionIdAsync(versionJson, vanillaInstallId, vanillaVersionJsonPath, cancellationToken).ConfigureAwait(false);
            await DownloadVersionFilesAsync(
                    vanillaInstallId,
                    versionJson,
                    minecraftRoot,
                    vanillaInstanceDirectory,
                    request.PreferOfficialSource,
                    downloadThreadLimit,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            MinecraftInstallResult result = request.Loader is { } loaderRequest
                ? await InstallLoaderAsync(
                        request,
                        loaderRequest,
                        baseVersionId,
                        versionJson,
                        minecraftRoot,
                        request.PreferOfficialSource,
                        downloadThreadLimit,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false)
                : new MinecraftInstallResult(
                    request.VersionId,
                    minecraftRoot,
                    vanillaInstanceDirectory,
                    vanillaVersionJsonPath);

            await InstallAddonsAsync(
                    request.Addons,
                    result.InstanceDirectory,
                    downloadThreadLimit,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(CreateProgress("安装完成", request.VersionId, 1d, 1, 1, 0, downloadThreadLimit));
            installCompleted = true;
            PortableLog.Info("MinecraftInstall", $"版本 {request.VersionId} 安装完成；目录={result.InstanceDirectory}。");
            return result;
        }
        catch (OperationCanceledException)
        {
            PortableLog.Warn("MinecraftInstall", $"版本 {request.VersionId} 的安装已取消，将回滚未完成的核心文件。");
            throw;
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "MinecraftInstall", $"版本 {request.VersionId} 安装失败，将回滚未完成的核心文件。");
            throw;
        }
        finally
        {
            if (coreBackup is not null)
            {
                if (installCompleted)
                    coreBackup.Commit();
                else
                    coreBackup.Restore();
            }
        }
    }

    private async Task InstallAddonsAsync(
        IReadOnlyList<MinecraftInstallAddonRequest> addons,
        string instanceDirectory,
        int downloadThreadLimit,
        IProgress<MinecraftInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (addons.Count == 0)
            return;

        string modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        int completed = 0;
        foreach (MinecraftInstallAddonRequest addon in addons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string safeFileName = Path.GetFileName(addon.FileName);
            if (string.IsNullOrWhiteSpace(safeFileName))
                throw new InvalidOperationException($"{addon.Kind} 下载文件名无效。");

            progress?.Report(CreateProgress(
                "安装附加组件",
                $"{addon.Kind} {addon.Version}",
                completed / (double)addons.Count,
                completed,
                addons.Count,
                0,
                downloadThreadLimit));
            await DownloadIfNeededAsync(
                    [addon.Url],
                    Path.Combine(modsDirectory, safeFileName),
                    addon.Size,
                    addon.Sha1,
                    "安装附加组件",
                    completed,
                    addons.Count,
                    progress,
                    activeThreads: 1,
                    threadLimit: downloadThreadLimit,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            completed++;
        }
    }

    public async Task<MinecraftInstallResult> RepairAsync(
        MinecraftRepairRequest request,
        IProgress<MinecraftInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceDirectory);
        ValidateVersionId(request.VersionId, nameof(request.VersionId));

        string minecraftRoot = Path.GetFullPath(request.MinecraftRootDirectory);
        string instanceDirectory = Path.GetFullPath(request.InstanceDirectory);
        PortableLog.Info("MinecraftRepair", $"开始检查并补全版本 {request.VersionId}；目录={instanceDirectory}。");
        JsonObject versionJson = await ReadJsonObjectAsync(request.VersionJsonPath, cancellationToken).ConfigureAwait(false);
        int downloadThreadLimit = DefaultDownloadThreadLimit;
        progress?.Report(CreateProgress("准备修复", request.VersionId, 0d, 0, 1, 0, downloadThreadLimit));
        await DownloadVersionFilesAsync(
                request.VersionId,
                versionJson,
                minecraftRoot,
                instanceDirectory,
                request.PreferOfficialSource,
                downloadThreadLimit,
                progress,
                cancellationToken,
                request.BeforeFileChangeAsync,
                request.FileChanged,
                request.VersionJsonPath)
            .ConfigureAwait(false);

        progress?.Report(CreateProgress("修复完成", request.VersionId, 1d, 1, 1, 0, downloadThreadLimit));
        PortableLog.Info("MinecraftRepair", $"版本 {request.VersionId} 文件检查与补全完成。");
        return new MinecraftInstallResult(request.VersionId, minecraftRoot, instanceDirectory, request.VersionJsonPath);
    }

    private static void ValidateVersionId(string versionId, string parameterName)
    {
        string value = versionId.Trim();
        if (!string.Equals(value, versionId, StringComparison.Ordinal) ||
            value.Length > 180 ||
            value is "." or ".." ||
            Path.IsPathRooted(value) ||
            value.AsSpan().IndexOfAny(InvalidVersionIdCharacters) >= 0 ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Minecraft 版本名称包含非法路径字符。", parameterName);
        }

        if (OperatingSystem.IsWindows())
        {
            if (value.EndsWith(' ') || value.EndsWith('.'))
                throw new ArgumentException("Minecraft 版本名称不能以空格或句点结尾。", parameterName);

            string deviceName = value.Split('.', 2)[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                IsNumberedDeviceName(deviceName, "COM") ||
                IsNumberedDeviceName(deviceName, "LPT"))
            {
                throw new ArgumentException("Minecraft 版本名称不能使用系统保留设备名。", parameterName);
            }
        }
    }

    private static bool IsNumberedDeviceName(string value, string prefix) =>
        value.Length == prefix.Length + 1 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[^1] is >= '1' and <= '9';

    /// <summary>
    /// Returns true when every downloadable (non-local) classpath library for the version
    /// inheritance chain already exists on disk. Used to skip full repair when only jar+json exist.
    /// </summary>
    public static async Task<bool> AreDownloadableLibrariesPresentAsync(
        string versionJsonPath,
        string minecraftRootDirectory,
        string instanceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);

        string minecraftRoot = Path.GetFullPath(minecraftRootDirectory);
        string instance = Path.GetFullPath(instanceDirectory);
        string jsonPath = Path.GetFullPath(versionJsonPath);
        if (!File.Exists(jsonPath))
            return false;

        JsonObject versionJson = await ReadJsonObjectAsync(jsonPath, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonObject> chain = await CollectInheritedVersionJsonsAsync(
                versionJson,
                jsonPath,
                minecraftRoot,
                cancellationToken)
            .ConfigureAwait(false);

        List<MinecraftLibraryToken> libraries = [];
        HashSet<string> seen = new(GetPathComparer());
        foreach (JsonObject json in chain)
        {
            foreach (MinecraftLibraryToken token in ResolveLibraries(json, minecraftRoot, instance))
            {
                if (!seen.Add(token.LocalPath))
                    continue;
                libraries.Add(token);
            }
        }

        MinecraftLibraryDownloadPlan plan = MinecraftLibraryDownloadPlanner.CreatePlan(
            new MinecraftLibraryDownloadPlanRequest
            {
                Libraries = libraries,
                MinecraftRootDirectory = minecraftRoot,
                PreferOfficialSource = true
            });

        foreach (MinecraftLibraryDownloadFile file in plan.DownloadFiles)
        {
            if (!File.Exists(file.LocalPath))
                return false;
        }

        foreach (MinecraftBundledLibraryFile bundled in plan.BundledFiles)
        {
            if (!File.Exists(bundled.LocalPath))
                return false;
        }

        return true;
    }

    private async Task DownloadVersionFilesAsync(
        string versionId,
        JsonObject versionJson,
        string minecraftRoot,
        string instanceDirectory,
        bool preferOfficialSource,
        int downloadThreadLimit,
        IProgress<MinecraftInstallProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, ValueTask>? beforeFileChangeAsync = null,
        Action<string>? fileChanged = null,
        string? versionJsonPath = null)
    {
        List<PlannedDownload> files = [];
        AddClientJarDownload(files, versionId, versionJson, instanceDirectory, preferOfficialSource);

        string resolvedJsonPath = string.IsNullOrWhiteSpace(versionJsonPath)
            ? Path.Combine(instanceDirectory, versionId + ".json")
            : Path.GetFullPath(versionJsonPath);
        IReadOnlyList<JsonObject> versionChain = await CollectInheritedVersionJsonsAsync(
                versionJson,
                resolvedJsonPath,
                minecraftRoot,
                cancellationToken)
            .ConfigureAwait(false);
        AddLibraryDownloads(files, versionChain, minecraftRoot, instanceDirectory, preferOfficialSource);

        // Asset index lives on the nearest ancestor that declares assetIndex / assets.
        JsonObject assetSourceJson = versionChain.FirstOrDefault(static json =>
            json["assetIndex"] is not null || json["assets"] is not null) ?? versionJson;
        await AddAssetDownloadsAsync(
                files,
                assetSourceJson,
                minecraftRoot,
                instanceDirectory,
                preferOfficialSource,
                downloadThreadLimit,
                progress,
                cancellationToken,
                beforeFileChangeAsync,
                fileChanged)
            .ConfigureAwait(false);

        int total = Math.Max(files.Count, 1);
        PortableLog.Debug(
            "MinecraftDownload",
            $"版本 {versionId} 文件计划生成完成；待处理={files.Count}；线程上限={downloadThreadLimit}；实例={instanceDirectory}。");
        progress?.Report(CreateProgress("准备下载文件", $"{files.Count} 个文件", 0.02d, 0, total, 0, downloadThreadLimit));
        if (files.Count == 0)
        {
            progress?.Report(CreateProgress("文件检查完成", versionId, 1d, total, total, 0, downloadThreadLimit));
            return;
        }

        FileDownloadProgressReporter reporter = new(progress, files, downloadThreadLimit);
        ParallelOptions parallelOptions = new()
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = downloadThreadLimit
        };
        await Parallel.ForEachAsync(
                files.Select(static (file, index) => new PlannedDownloadWorkItem(file, index)),
                parallelOptions,
                async (item, token) =>
                {
                    PlannedDownload file = item.File;
                    string fileName = Path.GetFileName(file.LocalPath);
                    reporter.WorkerStarted(item.Index, fileName);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (await IsExistingFileUsableAsync(
                                file.LocalPath,
                                file.ExpectedSize,
                                file.ExpectedSha1,
                                token).ConfigureAwait(false))
                        {
                            reporter.MarkComplete(item.Index, fileName);
                            return;
                        }

                        await DownloadIfNeededAsync(
                                file.Urls,
                                file.LocalPath,
                                file.ExpectedSize,
                                file.ExpectedSha1,
                                file.Stage,
                                0,
                                1,
                                new DelegateProgress<MinecraftInstallProgress>(
                                    update => reporter.ReportFileProgress(item.Index, update)),
                                activeThreads: 1,
                                threadLimit: 1,
                                cancellationToken: token,
                                beforeFileChangeAsync: beforeFileChangeAsync,
                                fileChanged: fileChanged)
                            .ConfigureAwait(false);
                        reporter.MarkComplete(item.Index, fileName);
                    }
                    finally
                    {
                        reporter.WorkerFinished(item.Index, fileName);
                    }
                })
            .ConfigureAwait(false);

        progress?.Report(CreateProgress("文件检查完成", versionId, 1d, total, total, 0, downloadThreadLimit));
    }

    private async Task<MinecraftInstallResult> InstallLoaderAsync(
        MinecraftInstallRequest request,
        MinecraftLoaderInstallRequest loaderRequest,
        string baseVersionId,
        JsonObject baseVersionJson,
        string minecraftRoot,
        bool preferOfficialSource,
        int downloadThreadLimit,
        IProgress<MinecraftInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (loaderRequest.Kind is MinecraftLoaderKind.Forge or
            MinecraftLoaderKind.NeoForge or
            MinecraftLoaderKind.Cleanroom or
            MinecraftLoaderKind.OptiFine)
        {
            return await InstallExternalLoaderAsync(
                    request,
                    loaderRequest,
                    baseVersionId,
                    minecraftRoot,
                    preferOfficialSource,
                    downloadThreadLimit,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (loaderRequest.Kind is MinecraftLoaderKind.LabyMod or MinecraftLoaderKind.LiteLoader)
        {
            return await InstallProfileLoaderAsync(
                    request,
                    loaderRequest,
                    baseVersionId,
                    minecraftRoot,
                    preferOfficialSource,
                    downloadThreadLimit,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string loaderName = loaderRequest.Kind.ToString();
        progress?.Report(CreateProgress("准备安装加载器", $"{loaderName} {loaderRequest.LoaderVersion}", 0d, 0, 1, 0, downloadThreadLimit));

        MinecraftLoaderInstallMetadata metadata = await _loaderMetadataService.GetLoaderInstallMetadataAsync(
                loaderRequest,
                baseVersionId,
                cancellationToken)
            .ConfigureAwait(false);
        JsonObject loaderVersionJson = MinecraftLoaderVersionJsonBuilder.Create(
            new MinecraftLoaderVersionJsonRequest
            {
                VersionId = request.VersionId,
                MinecraftVersionId = baseVersionId,
                Loader = metadata,
                Type = baseVersionJson["type"]?.ToString() ?? "release",
                Time = TryReadDateTimeOffset(baseVersionJson["releaseTime"] ?? baseVersionJson["time"])
            });

        string instanceDirectory = Path.Combine(minecraftRoot, "versions", request.VersionId);
        string versionJsonPath = Path.Combine(instanceDirectory, request.VersionId + ".json");
        Directory.CreateDirectory(instanceDirectory);
        await WriteJsonObjectAsync(loaderVersionJson, versionJsonPath, cancellationToken).ConfigureAwait(false);

        await DownloadVersionFilesAsync(
                request.VersionId,
                loaderVersionJson,
                minecraftRoot,
                instanceDirectory,
                preferOfficialSource,
                downloadThreadLimit,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        return new MinecraftInstallResult(request.VersionId, minecraftRoot, instanceDirectory, versionJsonPath);
    }

    private async Task<MinecraftInstallResult> InstallExternalLoaderAsync(
        MinecraftInstallRequest request,
        MinecraftLoaderInstallRequest loaderRequest,
        string baseVersionId,
        string minecraftRoot,
        bool preferOfficialSource,
        int downloadThreadLimit,
        IProgress<MinecraftInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        MinecraftLoaderInstallerArtifact artifact = MinecraftLoaderInstallerArtifactResolver.Resolve(
            loaderRequest.Kind,
            baseVersionId,
            loaderRequest.LoaderVersion);
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "PCLN", "loader-" + Guid.NewGuid().ToString("N"));
        string workingRoot = loaderRequest.Kind == MinecraftLoaderKind.OptiFine
            ? Path.Combine(temporaryDirectory, ".minecraft")
            : temporaryDirectory;
        string installerPath = Path.Combine(temporaryDirectory, artifact.FileName);
        try
        {
            Directory.CreateDirectory(workingRoot);
            progress?.Report(CreateProgress(
                "下载加载器安装器",
                $"{loaderRequest.Kind} {loaderRequest.LoaderVersion}",
                0d,
                0,
                1,
                0,
                downloadThreadLimit));
            await DownloadIfNeededAsync(
                    artifact.Sources,
                    installerPath,
                    expectedSize: -1,
                    expectedSha1: null,
                    "下载加载器安装器",
                    0,
                    1,
                    progress,
                    activeThreads: 1,
                    threadLimit: downloadThreadLimit,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            PrepareExternalInstallerRoot(workingRoot, minecraftRoot, baseVersionId);
            // Prefer launcher HTTP (proxy/DoH/mirrors) over the Java installer's own downloads.
            await PrefetchExternalInstallerLibrariesAsync(
                    installerPath,
                    workingRoot,
                    minecraftRoot,
                    preferOfficialSource,
                    downloadThreadLimit,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(CreateProgress(
                "运行加载器安装器",
                artifact.FileName,
                0.35d,
                0,
                1,
                0,
                downloadThreadLimit));
            Progress<string> installerOutput = new(line => progress?.Report(CreateProgress(
                "运行加载器安装器",
                line,
                0.55d,
                0,
                1,
                0,
                downloadThreadLimit)));
            await _externalLoaderInstaller.RunAsync(
                    new MinecraftExternalLoaderInstallRequest(
                        loaderRequest.Kind,
                        loaderRequest.LoaderVersion,
                        baseVersionId,
                        string.IsNullOrWhiteSpace(request.JavaExecutablePath) ? "java" : request.JavaExecutablePath,
                        installerPath,
                        workingRoot,
                        request.LoaderExtraJvmArguments),
                    installerOutput,
                    cancellationToken)
                .ConfigureAwait(false);

            string generatedJsonPath = FindGeneratedLoaderJson(
                workingRoot,
                baseVersionId,
                loaderRequest.Kind,
                loaderRequest.LoaderVersion);
            JsonObject loaderVersionJson = await ReadJsonObjectAsync(generatedJsonPath, cancellationToken).ConfigureAwait(false);
            loaderVersionJson["id"] = request.VersionId;
            if (loaderVersionJson["inheritsFrom"] is null && loaderRequest.Kind != MinecraftLoaderKind.OptiFine)
                loaderVersionJson["inheritsFrom"] = baseVersionId;

            string instanceDirectory = Path.Combine(minecraftRoot, "versions", request.VersionId);
            string versionJsonPath = Path.Combine(instanceDirectory, request.VersionId + ".json");
            Directory.CreateDirectory(instanceDirectory);
            CopyDirectoryIfPresent(Path.Combine(workingRoot, "libraries"), Path.Combine(minecraftRoot, "libraries"));
            CopyGeneratedVersionJar(generatedJsonPath, instanceDirectory, request.VersionId);
            await WriteJsonObjectAsync(loaderVersionJson, versionJsonPath, cancellationToken).ConfigureAwait(false);

            await DownloadVersionFilesAsync(
                    request.VersionId,
                    loaderVersionJson,
                    minecraftRoot,
                    instanceDirectory,
                    preferOfficialSource,
                    downloadThreadLimit,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            return new MinecraftInstallResult(request.VersionId, minecraftRoot, instanceDirectory, versionJsonPath);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static void PrepareExternalInstallerRoot(string workingRoot, string minecraftRoot, string baseVersionId)
    {
        string sourceDirectory = Path.Combine(minecraftRoot, "versions", baseVersionId);
        string targetDirectory = Path.Combine(workingRoot, "versions", baseVersionId);
        Directory.CreateDirectory(targetDirectory);
        foreach (string extension in new[] { ".json", ".jar" })
        {
            string source = Path.Combine(sourceDirectory, baseVersionId + extension);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(targetDirectory, baseVersionId + extension), overwrite: true);
        }

        // Seed libraries so NeoForge/Forge installers can skip Java HTTPS downloads when possible.
        CopyDirectoryIfPresent(
            Path.Combine(minecraftRoot, "libraries"),
            Path.Combine(workingRoot, "libraries"));

        File.WriteAllText(Path.Combine(workingRoot, "launcher_profiles.json"), "{\"profiles\":{}}");
    }

    /// <summary>
    /// Download libraries declared by the Forge/NeoForge installer profile using the launcher
    /// HTTP stack (proxy / DoH / BMCLAPI) into the installer working root before Java runs.
    /// </summary>
    private async Task PrefetchExternalInstallerLibrariesAsync(
        string installerPath,
        string workingRoot,
        string minecraftRoot,
        bool preferOfficialSource,
        int downloadThreadLimit,
        IProgress<MinecraftInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<InstallerLibraryDownload> libraries;
        try
        {
            libraries = ReadInstallerLibraryDownloads(installerPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
        {
            PortableLog.Warn(ex, "MinecraftInstall", "无法解析加载器安装器依赖清单，将交给 Java 安装器自行下载。");
            return;
        }

        if (libraries.Count == 0)
            return;

        string workingLibraries = Path.Combine(workingRoot, "libraries");
        string sharedLibraries = Path.Combine(minecraftRoot, "libraries");
        Directory.CreateDirectory(workingLibraries);
        Directory.CreateDirectory(sharedLibraries);

        progress?.Report(CreateProgress(
            "预下载加载器依赖",
            $"{libraries.Count} 个文件",
            0.2d,
            0,
            libraries.Count,
            0,
            downloadThreadLimit));

        int completed = 0;
        foreach (InstallerLibraryDownload library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = library.Path.Replace('/', Path.DirectorySeparatorChar);
            string workingTarget = Path.Combine(workingLibraries, relative);
            string sharedTarget = Path.Combine(sharedLibraries, relative);

            if (!File.Exists(workingTarget) && File.Exists(sharedTarget))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(workingTarget)!);
                File.Copy(sharedTarget, workingTarget, overwrite: true);
            }

            if (!File.Exists(workingTarget) ||
                (library.ExpectedSize > 0 && new FileInfo(workingTarget).Length != library.ExpectedSize))
            {
                List<string> sources = [];
                if (!string.IsNullOrWhiteSpace(library.Url))
                {
                    if (preferOfficialSource)
                        sources.Add(library.Url);
                    sources.Add(ToBmclApiMavenUrl(library.Path));
                    if (!preferOfficialSource)
                        sources.Add(library.Url);
                }
                else
                {
                    sources.Add(ToBmclApiMavenUrl(library.Path));
                    sources.Add("https://maven.neoforged.net/releases/" + library.Path.TrimStart('/'));
                    sources.Add("https://maven.minecraftforge.net/" + library.Path.TrimStart('/'));
                    sources.Add("https://libraries.minecraft.net/" + library.Path.TrimStart('/'));
                }

                await DownloadIfNeededAsync(
                        sources,
                        workingTarget,
                        library.ExpectedSize,
                        library.Sha1,
                        "预下载加载器依赖",
                        completed,
                        libraries.Count,
                        progress,
                        activeThreads: 1,
                        threadLimit: downloadThreadLimit,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (File.Exists(workingTarget) && !File.Exists(sharedTarget))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sharedTarget)!);
                File.Copy(workingTarget, sharedTarget, overwrite: true);
            }

            completed++;
            progress?.Report(CreateProgress(
                "预下载加载器依赖",
                Path.GetFileName(relative),
                0.2d + 0.15d * completed / libraries.Count,
                completed,
                libraries.Count,
                0,
                downloadThreadLimit));
        }

        PortableLog.Info("MinecraftInstall", $"已为加载器安装器预下载/复用 {completed}/{libraries.Count} 个依赖。");
    }

    private static List<InstallerLibraryDownload> ReadInstallerLibraryDownloads(string installerPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(installerPath);
        List<InstallerLibraryDownload> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entryName in new[] { "install_profile.json", "version.json" })
        {
            ZipArchiveEntry? entry = archive.GetEntry(entryName);
            if (entry is null)
                continue;

            using Stream stream = entry.Open();
            using JsonDocument document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries) ||
                libraries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement library in libraries.EnumerateArray())
            {
                if (TryReadInstallerLibrary(library, out InstallerLibraryDownload download) &&
                    seen.Add(download.Path))
                {
                    results.Add(download);
                }
            }
        }

        return results;
    }

    private static bool TryReadInstallerLibrary(JsonElement library, out InstallerLibraryDownload download)
    {
        download = default!;
        string? path = null;
        string? url = null;
        string? sha1 = null;
        long size = -1;

        if (library.TryGetProperty("downloads", out JsonElement downloads) &&
            downloads.TryGetProperty("artifact", out JsonElement artifact))
        {
            path = artifact.TryGetProperty("path", out JsonElement pathNode) ? pathNode.GetString() : null;
            url = artifact.TryGetProperty("url", out JsonElement urlNode) ? urlNode.GetString() : null;
            sha1 = artifact.TryGetProperty("sha1", out JsonElement shaNode) ? shaNode.GetString() : null;
            if (artifact.TryGetProperty("size", out JsonElement sizeNode) &&
                sizeNode.TryGetInt64(out long parsedSize))
            {
                size = parsedSize;
            }
        }

        if (string.IsNullOrWhiteSpace(path) &&
            library.TryGetProperty("name", out JsonElement nameNode))
        {
            path = MavenNameToPath(nameNode.GetString());
        }

        if (string.IsNullOrWhiteSpace(path))
            return false;

        download = new InstallerLibraryDownload(path.Replace('\\', '/'), url, sha1, size);
        return true;
    }

    private static string? MavenNameToPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // group:artifact:version[:classifier][@ext]
        string[] atParts = name.Split('@', 2);
        string ext = atParts.Length > 1 ? atParts[1] : "jar";
        string[] parts = atParts[0].Split(':');
        if (parts.Length < 3)
            return null;

        string group = parts[0].Replace('.', '/');
        string artifact = parts[1];
        string version = parts[2];
        string fileName = parts.Length >= 4
            ? $"{artifact}-{version}-{parts[3]}.{ext}"
            : $"{artifact}-{version}.{ext}";
        return $"{group}/{artifact}/{version}/{fileName}";
    }

    private static string ToBmclApiMavenUrl(string relativePath) =>
        "https://bmclapi2.bangbang93.com/maven/" + relativePath.TrimStart('/');

    private sealed record InstallerLibraryDownload(
        string Path,
        string? Url,
        string? Sha1,
        long ExpectedSize);

    private static string FindGeneratedLoaderJson(
        string workingRoot,
        string baseVersionId,
        MinecraftLoaderKind kind,
        string loaderVersion)
    {
        string versionsRoot = Path.Combine(workingRoot, "versions");
        string[] candidates = Directory.Exists(versionsRoot)
            ? Directory.GetFiles(versionsRoot, "*.json", SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    baseVersionId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        if (candidates.Length == 0)
            throw new InvalidOperationException($"{kind} 安装器没有生成版本描述文件。");

        string normalizedLoader = loaderVersion.Replace("+", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return candidates
            .OrderByDescending(path => NormalizeFileName(path).Contains(normalizedLoader, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private static string NormalizeFileName(string path) =>
        Path.GetFileNameWithoutExtension(path)
            .Replace("+", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

    private static void CopyGeneratedVersionJar(string generatedJsonPath, string targetDirectory, string targetVersionId)
    {
        string sourceDirectory = Path.GetDirectoryName(generatedJsonPath)!;
        string? jar = Directory.GetFiles(sourceDirectory, "*.jar", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (jar is not null)
            File.Copy(jar, Path.Combine(targetDirectory, targetVersionId + ".jar"), overwrite: true);
    }

    private static void CopyDirectoryIfPresent(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            return;

        foreach (string sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            string targetFile = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<MinecraftInstallResult> InstallProfileLoaderAsync(
        MinecraftInstallRequest request,
        MinecraftLoaderInstallRequest loaderRequest,
        string baseVersionId,
        string minecraftRoot,
        bool preferOfficialSource,
        int downloadThreadLimit,
        IProgress<MinecraftInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string loaderName = loaderRequest.Kind.ToString();
        progress?.Report(CreateProgress(
            "获取加载器版本描述",
            $"{loaderName} {loaderRequest.LoaderVersion}",
            0d,
            0,
            1,
            0,
            downloadThreadLimit));

        JsonObject loaderVersionJson = await _loaderMetadataService.GetLoaderVersionProfileAsync(
                loaderRequest,
                baseVersionId,
                cancellationToken)
            .ConfigureAwait(false);
        loaderVersionJson["id"] = request.VersionId;
        if (loaderRequest.Kind == MinecraftLoaderKind.LabyMod)
            loaderVersionJson["clientVersion"] = baseVersionId;

        string instanceDirectory = Path.Combine(minecraftRoot, "versions", request.VersionId);
        string versionJsonPath = Path.Combine(instanceDirectory, request.VersionId + ".json");
        Directory.CreateDirectory(instanceDirectory);
        await WriteJsonObjectAsync(loaderVersionJson, versionJsonPath, cancellationToken).ConfigureAwait(false);

        await DownloadVersionFilesAsync(
                request.VersionId,
                loaderVersionJson,
                minecraftRoot,
                instanceDirectory,
                preferOfficialSource,
                downloadThreadLimit,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        return new MinecraftInstallResult(request.VersionId, minecraftRoot, instanceDirectory, versionJsonPath);
    }

    private async Task DownloadIfNeededAsync(
        IReadOnlyList<string> urls,
        string localPath,
        long expectedSize,
        string? expectedSha1,
        string stage,
        int completedFiles,
        int totalFiles,
        IProgress<MinecraftInstallProgress>? progress,
        int activeThreads,
        int threadLimit,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, ValueTask>? beforeFileChangeAsync = null,
        Action<string>? fileChanged = null)
    {
        if (await IsExistingFileUsableAsync(localPath, expectedSize, expectedSha1, cancellationToken).ConfigureAwait(false))
        {
            PortableLog.Debug("MinecraftDownload", $"复用已通过校验的文件：{localPath}");
            return;
        }

        if (beforeFileChangeAsync is not null)
            await beforeFileChangeAsync(localPath, cancellationToken).ConfigureAwait(false);

        ProgressThrottle progressThrottle = new();
        List<Exception> failures = [];
        foreach (string source in urls)
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            PortableLog.Debug("MinecraftDownload", $"开始下载：{source} -> {localPath}");
            DownloadTransferResult result = await _downloadService.DownloadAsync(
                new DownloadRequest
                {
                    Sources = [source],
                    DestinationPath = localPath,
                    ConnectionFactory = url => new HttpDlConnection(_httpClient, url, ConfigureRequest)
                },
                downloadProgress =>
                {
                    bool force = downloadProgress.Stage is
                        DownloadStage.Completed or
                        DownloadStage.Committing or
                        DownloadStage.Failed;
                    if (!progressThrottle.ShouldReport(force))
                        return;

                    PortableLog.RealTime(
                        "MinecraftDownload",
                        $"文件进度：{Path.GetFileName(localPath)}；阶段={downloadProgress.Stage}；" +
                        $"字节={downloadProgress.DownloadedBytes}/{downloadProgress.TotalBytes}；速度={downloadProgress.BytesPerSecond}B/s。");

                    double fileRatio = downloadProgress.TotalBytes <= 0
                        ? 0d
                        : Math.Clamp(downloadProgress.DownloadedBytes / (double)downloadProgress.TotalBytes, 0d, 1d);
                    double progressValue = totalFiles <= 0
                        ? fileRatio
                        : Math.Clamp((completedFiles + fileRatio) / totalFiles, 0d, 1d);
                    progress?.Report(new MinecraftInstallProgress
                    {
                        Stage = stage,
                        Detail = Path.GetFileName(localPath),
                        Progress = progressValue,
                        CompletedFiles = completedFiles,
                        TotalFiles = totalFiles,
                        BytesReceived = downloadProgress.DownloadedBytes,
                        TotalBytes = downloadProgress.TotalBytes,
                        SpeedBytesPerSecond = downloadProgress.BytesPerSecond,
                        ActiveThreads = activeThreads,
                        ThreadLimit = threadLimit,
                        Steps =
                        [
                            new MinecraftInstallStepProgress(
                                stage,
                                Path.GetFileName(localPath),
                                progressValue,
                                progressValue >= 1d ? MinecraftInstallStepState.Finished : MinecraftInstallStepState.Running)
                        ]
                    });
                },
                cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                PortableLog.Warn(
                    "MinecraftDownload",
                    $"下载源失败，将尝试下一来源：{source}；文件={Path.GetFileName(localPath)}；错误数={result.Errors.Count}。");
                failures.AddRange(result.Errors.Select(static error =>
                    error.Exception ?? new IOException(error.Message)));
                continue;
            }

            if (await IsExistingFileUsableAsync(localPath, expectedSize, expectedSha1, cancellationToken).ConfigureAwait(false))
            {
                PortableLog.Debug("MinecraftDownload", $"下载并校验完成：{localPath}");
                fileChanged?.Invoke(localPath);
                return;
            }

            DeleteInvalidDownload(localPath);
            PortableLog.Warn("MinecraftDownload", $"下载完成但校验失败，已删除无效文件：{localPath}");
            failures.Add(new IOException("文件校验失败：" + localPath));
        }

        throw new IOException("下载失败或文件校验失败：" + localPath, new AggregateException(failures));
    }

    private static void AddClientJarDownload(
        List<PlannedDownload> files,
        string versionId,
        JsonObject versionJson,
        string instanceDirectory,
        bool preferOfficialSource)
    {
        MinecraftClientJarDownloadPlan plan = MinecraftClientDownloadPlanner.CreateClientJarPlan(
            new MinecraftClientJarDownloadPlanRequest
            {
                VersionJson = versionJson,
                InstanceDirectory = instanceDirectory,
                VersionName = versionId
            });
        if (plan.File is null)
            return;

        files.Add(new PlannedDownload(
            MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(plan.File.Url, preferOfficialSource),
            plan.File.LocalPath,
            plan.File.ActualSize,
            plan.File.Sha1,
            "下载客户端"));
    }

    private static void AddLibraryDownloads(
        List<PlannedDownload> files,
        IReadOnlyList<JsonObject> versionJsonChain,
        string minecraftRoot,
        string instanceDirectory,
        bool preferOfficialSource)
    {
        List<MinecraftLibraryToken> libraries = [];
        HashSet<string> seen = new(GetPathComparer());
        foreach (JsonObject versionJson in versionJsonChain)
        {
            foreach (MinecraftLibraryToken token in ResolveLibraries(versionJson, minecraftRoot, instanceDirectory))
            {
                if (!seen.Add(token.LocalPath))
                    continue;
                libraries.Add(token);
            }
        }

        MinecraftLibraryDownloadPlan plan = MinecraftLibraryDownloadPlanner.CreatePlan(
            new MinecraftLibraryDownloadPlanRequest
            {
                Libraries = libraries,
                MinecraftRootDirectory = minecraftRoot,
                PreferOfficialSource = preferOfficialSource
            });
        foreach (MinecraftLibraryDownloadFile library in plan.DownloadFiles)
            files.Add(new PlannedDownload(
                library.Urls,
                library.LocalPath,
                library.ActualSize,
                library.Sha1,
                "下载运行库"));
    }

    private static IReadOnlyList<MinecraftLibraryToken> ResolveLibraries(
        JsonObject versionJson,
        string minecraftRoot,
        string instanceDirectory) =>
        MinecraftLibraryResolver.Resolve(
            new MinecraftLibraryResolutionRequest
            {
                VersionJson = versionJson,
                MinecraftRootDirectory = minecraftRoot,
                TargetInstanceDirectory = instanceDirectory,
                OperatingSystem = GetCurrentLibraryOperatingSystem(),
                Is64BitArchitecture = Environment.Is64BitOperatingSystem,
                IsArm64Architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64,
                OperatingSystemVersion = Environment.OSVersion.VersionString
            });

    /// <summary>
    /// Leaf version JSON first, then each <c>inheritsFrom</c> ancestor (same order as launch classpath merge).
    /// </summary>
    private static async Task<IReadOnlyList<JsonObject>> CollectInheritedVersionJsonsAsync(
        JsonObject versionJson,
        string versionJsonPath,
        string minecraftRoot,
        CancellationToken cancellationToken)
    {
        List<JsonObject> result = [versionJson];
        HashSet<string> seen = new(GetPathComparer())
        {
            Path.GetFullPath(versionJsonPath)
        };

        JsonObject current = versionJson;
        string currentJsonPath = versionJsonPath;
        for (int depth = 0; depth < 32; depth++)
        {
            string? inheritedId = current["inheritsFrom"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(inheritedId))
                return result;

            string? inheritedJsonPath = MinecraftVersionFileResolver.ResolveJsonPath(
                minecraftRoot,
                Path.GetDirectoryName(currentJsonPath),
                inheritedId);
            if (inheritedJsonPath is null || !File.Exists(inheritedJsonPath))
            {
                PortableLog.Warn(
                    "MinecraftDownload",
                    $"继承版本描述缺失，跳过后续祖先库补全：{inheritedId}");
                return result;
            }

            string normalizedPath = Path.GetFullPath(inheritedJsonPath);
            if (!seen.Add(normalizedPath))
            {
                PortableLog.Warn("MinecraftDownload", $"version.json 存在循环继承：{inheritedId}");
                return result;
            }

            JsonObject inheritedJson = await ReadJsonObjectAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
            result.Add(inheritedJson);
            current = inheritedJson;
            currentJsonPath = normalizedPath;
        }

        PortableLog.Warn("MinecraftDownload", "version.json 继承层数超过 32 层，已截断库补全链。");
        return result;
    }

    private async Task AddAssetDownloadsAsync(
        List<PlannedDownload> files,
        JsonObject versionJson,
        string minecraftRoot,
        string instanceDirectory,
        bool preferOfficialSource,
        int downloadThreadLimit,
        IProgress<MinecraftInstallProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, ValueTask>? beforeFileChangeAsync = null,
        Action<string>? fileChanged = null)
    {
        MinecraftAssetIndexDownloadPlan indexPlan = MinecraftClientDownloadPlanner.CreateAssetIndexPlan(
            new MinecraftAssetIndexDownloadPlanRequest
            {
                VersionJson = versionJson,
                MinecraftRootDirectory = minecraftRoot
            });
        if (!indexPlan.HasDownload)
            return;

        await DownloadIfNeededAsync(
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(indexPlan.Url!, preferOfficialSource),
                indexPlan.LocalPath!,
                expectedSize: -1,
                expectedSha1: null,
                "下载资源索引",
                0,
                1,
                progress,
                activeThreads: 1,
                threadLimit: downloadThreadLimit,
                cancellationToken: cancellationToken,
                beforeFileChangeAsync: beforeFileChangeAsync,
                fileChanged: fileChanged)
            .ConfigureAwait(false);

        progress?.Report(CreateProgress(
            "解析资源索引",
            Path.GetFileName(indexPlan.LocalPath!),
            0d,
            0,
            1,
            activeThreads: 1,
            threadLimit: downloadThreadLimit));
        JsonObject indexJson = await ReadJsonObjectAsync(indexPlan.LocalPath!, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MinecraftAssetToken> assets = MinecraftAssetListResolver.GetAssetList(
            new MinecraftAssetListRequest
            {
                IndexJson = indexJson,
                MinecraftRootDirectory = minecraftRoot,
                InstanceDirectory = instanceDirectory
            });
        Dictionary<string, MinecraftAssetFileState> existing = new(GetPathComparer());
        foreach (MinecraftAssetToken asset in assets)
        {
            FileInfo file = new(asset.LocalPath);
            existing[asset.LocalPath] = new MinecraftAssetFileState(file.Exists, file.Exists ? file.Length : 0L);
        }

        MinecraftAssetDownloadPlan plan = MinecraftAssetDownloadPlanner.CreatePlan(
            new MinecraftAssetDownloadPlanRequest
            {
                Assets = assets,
                ExistingFiles = existing
            });
        foreach (MinecraftAssetDownloadFile asset in plan.Files)
        {
            files.Add(new PlannedDownload(
                MinecraftDownloadSourcePlanner.GetAssetSources(asset.Url, preferOfficialSource),
                asset.LocalPath,
                asset.ActualSize,
                asset.Hash,
                "下载资源文件"));
        }
    }

    private async Task<string> GetStringWithFailoverAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken)
    {
        List<Exception> errors = [];
        foreach (string url in urls)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                ConfigureRequest(request);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await PortableHttp.ReadStringAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                PortableLog.Warn(ex, "MinecraftMetadata", $"元数据来源不可用，将尝试下一来源：{url}");
                errors.Add(ex);
            }
        }

        throw new HttpRequestException("无法获取 Minecraft 版本清单。", new AggregateException(errors));
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true);
        JsonNode? node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return node as JsonObject
               ?? throw new FormatException("JSON 根节点不是对象：" + path);
    }

    private static async Task NormalizeVersionIdAsync(
        JsonObject versionJson,
        string versionId,
        string versionJsonPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(versionJson["id"]?.ToString(), versionId, StringComparison.Ordinal))
            return;

        versionJson["id"] = versionId;
        await WriteJsonObjectAsync(versionJson, versionJsonPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonObjectAsync(
        JsonObject versionJson,
        string versionJsonPath,
        CancellationToken cancellationToken)
    {
        string tempPath = versionJsonPath + ".tmp";
        await using (FileStream stream = new(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read,
                         bufferSize: 16 * 1024,
                         useAsync: true))
        {
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
            versionJson.WriteTo(writer);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, versionJsonPath, overwrite: true);
    }

    private static async ValueTask<bool> IsExistingFileUsableAsync(
        string path,
        long expectedSize,
        string? expectedSha1,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        if (!file.Exists)
            return false;
        if (expectedSize > 0 && file.Length != expectedSize)
            return false;
        if (string.IsNullOrWhiteSpace(expectedSha1))
            return true;

        return await IsFileSha1MatchAsync(file.FullName, expectedSha1, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> IsFileSha1MatchAsync(
        string path,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        string normalized = expectedSha1.Trim();
        if (normalized.Length != SHA1Provider.Instance.HashSizeInBytes * 2)
            return false;

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        byte[] hash = await SHA1Provider.Instance.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteInvalidDownload(string localPath)
    {
        try
        {
            File.Delete(localPath);
            File.Delete(localPath + ".PCLDownloading");
        }
        catch (IOException)
        {
            // The next install attempt will revalidate and retry the file.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the original validation failure as the actionable error.
        }
    }

    private sealed class VersionCoreBackup
    {
        private readonly BackupEntry[] _entries;

        private VersionCoreBackup(BackupEntry[] entries)
        {
            _entries = entries;
        }

        public static VersionCoreBackup Create(string minecraftRoot, string versionId)
        {
            string instanceDirectory = Path.Combine(minecraftRoot, "versions", versionId);
            Directory.CreateDirectory(instanceDirectory);
            string backupSuffix = ".pcl-backup-" + Guid.NewGuid().ToString("N");
            BackupEntry[] entries =
            [
                CreateEntry(Path.Combine(instanceDirectory, versionId + ".json"), backupSuffix),
                CreateEntry(Path.Combine(instanceDirectory, versionId + ".jar"), backupSuffix)
            ];

            List<BackupEntry> moved = [];
            try
            {
                foreach (BackupEntry entry in entries)
                {
                    if (entry.BackupPath is null)
                        continue;

                    File.Move(entry.OriginalPath, entry.BackupPath);
                    moved.Add(entry);
                }
            }
            catch
            {
                foreach (BackupEntry entry in moved.AsEnumerable().Reverse())
                {
                    if (entry.BackupPath is not null && File.Exists(entry.BackupPath))
                        File.Move(entry.BackupPath, entry.OriginalPath, overwrite: true);
                }

                throw;
            }

            return new VersionCoreBackup(entries);
        }

        public void Commit()
        {
            foreach (BackupEntry entry in _entries)
            {
                try
                {
                    DeleteFileIfExists(entry.BackupPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The installed files are already committed. A stale backup is safer than failing the install.
                }
            }
        }

        public void Restore()
        {
            foreach (BackupEntry entry in _entries)
            {
                DeleteFileIfExists(entry.OriginalPath + ".PCLDownloading");
                DeleteFileIfExists(entry.OriginalPath);
                if (entry.BackupPath is not null && File.Exists(entry.BackupPath))
                    File.Move(entry.BackupPath, entry.OriginalPath, overwrite: true);
            }
        }

        private static BackupEntry CreateEntry(string originalPath, string backupSuffix) =>
            new(originalPath, File.Exists(originalPath) ? originalPath + backupSuffix : null);

        private static void DeleteFileIfExists(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }

        private sealed record BackupEntry(string OriginalPath, string? BackupPath);
    }

    private static MinecraftInstallProgress CreateProgress(
        string stage,
        string detail,
        double progress,
        int completed,
        int total,
        int activeThreads = 0,
        int threadLimit = 1,
        long speedBytesPerSecond = 0,
        long bytesReceived = 0,
        long totalBytes = -1,
        IReadOnlyList<MinecraftInstallStepProgress>? steps = null) =>
        new()
        {
            Stage = stage,
            Detail = detail,
            Progress = Math.Clamp(progress, 0d, 1d),
            CompletedFiles = completed,
            TotalFiles = total,
            BytesReceived = bytesReceived,
            TotalBytes = totalBytes,
            SpeedBytesPerSecond = speedBytesPerSecond,
            ActiveThreads = activeThreads,
            ThreadLimit = Math.Max(1, threadLimit),
            Steps = steps ?? CreateSingleStepProgress(stage, detail, progress)
        };

    private static IReadOnlyList<MinecraftInstallStepProgress> CreateSingleStepProgress(
        string stage,
        string detail,
        double progress)
    {
        if (string.IsNullOrWhiteSpace(stage))
            return [];

        return
        [
            new MinecraftInstallStepProgress(
                stage,
                detail,
                Math.Clamp(progress, 0d, 1d),
                progress >= 1d ? MinecraftInstallStepState.Finished : MinecraftInstallStepState.Running)
        ];
    }

    private static int NormalizeDownloadThreadLimit(int value) =>
        Math.Clamp(value <= 0 ? DefaultDownloadThreadLimit : value, 1, MaxDownloadThreadLimit);

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? TryReadDate(JsonElement element, string propertyName)
    {
        string? text = TryReadString(element, propertyName);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value
            : null;
    }

    private static DateTimeOffset? TryReadDateTimeOffset(JsonNode? node)
    {
        string? text = node?.ToString();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value
            : null;
    }

    private static MinecraftLibraryOperatingSystem GetCurrentLibraryOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
            return MinecraftLibraryOperatingSystem.Win32;
        if (OperatingSystem.IsLinux())
            return MinecraftLibraryOperatingSystem.Linux;
        if (OperatingSystem.IsMacOS())
            return MinecraftLibraryOperatingSystem.MacOs;
        return MinecraftLibraryOperatingSystem.Unknown;
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void ConfigureRequest(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PCL-N", "1.0"));
        string language = CultureInfo.CurrentUICulture.Name;
        request.Headers.AcceptLanguage.ParseAdd(string.IsNullOrWhiteSpace(language) ? "zh-CN" : language);
    }

    private sealed record PlannedDownload(
        IReadOnlyList<string> Urls,
        string LocalPath,
        long ExpectedSize,
        string? ExpectedSha1,
        string Stage);

    private sealed record PlannedDownloadWorkItem(PlannedDownload File, int Index);

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ProgressThrottle
    {
        private static readonly long MinReportTicks = Stopwatch.Frequency / 10;
        private long _lastReportTimestamp;
        private bool _hasReported;

        public bool ShouldReport(bool force = false)
        {
            long now = Stopwatch.GetTimestamp();
            if (force || !_hasReported || now - _lastReportTimestamp >= MinReportTicks)
            {
                _hasReported = true;
                _lastReportTimestamp = now;
                return true;
            }

            return false;
        }
    }

    private sealed class FileDownloadProgressReporter
    {
        private const int MaxActiveFileRows = 8;

        private readonly object _sync = new();
        private readonly ProgressThrottle _throttle = new();
        private readonly IProgress<MinecraftInstallProgress>? _progress;
        private readonly double[] _fileProgress;
        private readonly long[] _fileSpeeds;
        private readonly bool[] _fileCompleted;
        private readonly bool[] _fileActive;
        private readonly string?[] _fileDetails;
        private readonly int[] _fileStageIndexes;
        private readonly DownloadStageAggregate[] _stages;
        private readonly int _threadLimit;
        private double _progressSum;
        private long _speedSum;
        private int _activeThreads;
        private int _completedFiles;

        public FileDownloadProgressReporter(
            IProgress<MinecraftInstallProgress>? progress,
            IReadOnlyList<PlannedDownload> files,
            int threadLimit)
        {
            _progress = progress;
            _fileProgress = new double[files.Count];
            _fileSpeeds = new long[files.Count];
            _fileCompleted = new bool[files.Count];
            _fileActive = new bool[files.Count];
            _fileDetails = new string?[files.Count];
            _fileStageIndexes = new int[files.Count];
            _stages = CreateStageAggregates(files, _fileStageIndexes);
            _threadLimit = Math.Max(1, threadLimit);
        }

        public void WorkerStarted(int index, string detail)
        {
            lock (_sync)
            {
                _activeThreads++;
                DownloadStageAggregate stage = StageFor(index);
                stage.ActiveFiles++;
                stage.Started = true;
                _fileActive[index] = true;
                _fileDetails[index] = detail;
                ReportLocked(stage.Name, detail);
            }
        }

        public void WorkerFinished(int index, string detail)
        {
            lock (_sync)
            {
                _activeThreads = Math.Max(0, _activeThreads - 1);
                DownloadStageAggregate stage = StageFor(index);
                stage.ActiveFiles = Math.Max(0, stage.ActiveFiles - 1);
                _fileActive[index] = false;
                ReportLocked(stage.Name, detail);
            }
        }

        public void ReportFileProgress(int index, MinecraftInstallProgress update)
        {
            lock (_sync)
            {
                DownloadStageAggregate stage = StageFor(index);
                stage.Started = true;
                double nextProgress = Math.Clamp(update.Progress, 0d, 1d);
                double progressDelta = nextProgress - _fileProgress[index];
                _fileProgress[index] = nextProgress;
                _progressSum += progressDelta;
                stage.ProgressSum += progressDelta;
                if (!string.IsNullOrWhiteSpace(update.Detail))
                    _fileDetails[index] = update.Detail;

                long nextSpeed = Math.Max(0, update.SpeedBytesPerSecond);
                _speedSum += nextSpeed - _fileSpeeds[index];
                _fileSpeeds[index] = nextSpeed;
                ReportLocked(update.Stage, update.Detail, update.BytesReceived, update.TotalBytes);
            }
        }

        public void MarkComplete(int index, string detail)
        {
            lock (_sync)
            {
                DownloadStageAggregate stage = StageFor(index);
                stage.Started = true;
                if (!_fileCompleted[index])
                {
                    _fileCompleted[index] = true;
                    _completedFiles++;
                    stage.CompletedFiles++;
                }

                _fileActive[index] = false;
                _fileDetails[index] = detail;
                double progressDelta = 1d - _fileProgress[index];
                _fileProgress[index] = 1d;
                _progressSum += progressDelta;
                stage.ProgressSum += progressDelta;
                _speedSum -= _fileSpeeds[index];
                _fileSpeeds[index] = 0;
                ReportLocked(stage.Name, detail, force: true);
            }
        }

        private void ReportLocked(
            string stage,
            string detail,
            long bytesReceived = 0,
            long totalBytes = -1,
            bool force = false)
        {
            if (!_throttle.ShouldReport(force))
                return;

            _progress?.Report(CreateProgress(
                stage,
                detail,
                _progressSum / _fileProgress.Length,
                _completedFiles,
                _fileProgress.Length,
                _activeThreads,
                _threadLimit,
                _speedSum,
                bytesReceived,
                totalBytes,
                CreateStepsLocked()));
            PortableLog.RealTime(
                "MinecraftDownload",
                $"聚合进度：阶段={stage}；详情={detail}；文件={_completedFiles}/{_fileProgress.Length}；" +
                $"活动线程={_activeThreads}/{_threadLimit}；速度={_speedSum}B/s；字节={bytesReceived}/{totalBytes}。");
        }

        private DownloadStageAggregate StageFor(int fileIndex) =>
            _stages[_fileStageIndexes[fileIndex]];

        private MinecraftInstallStepProgress[] CreateStepsLocked()
        {
            List<MinecraftInstallStepProgress> steps = new(_stages.Length + Math.Min(MaxActiveFileRows, _activeThreads));
            for (int i = 0; i < _stages.Length; i++)
            {
                DownloadStageAggregate stage = _stages[i];
                double progress = stage.TotalFiles <= 0
                    ? 1d
                    : Math.Clamp(stage.ProgressSum / stage.TotalFiles, 0d, 1d);
                MinecraftInstallStepState state = stage.CompletedFiles >= stage.TotalFiles
                    ? MinecraftInstallStepState.Finished
                    : stage.ActiveFiles > 0 || stage.Started || stage.ProgressSum > 0d
                        ? MinecraftInstallStepState.Running
                        : MinecraftInstallStepState.Waiting;
                steps.Add(new MinecraftInstallStepProgress(
                    stage.Name,
                    $"{stage.CompletedFiles} / {stage.TotalFiles} 个文件",
                    progress,
                    state));
            }

            int activeRows = 0;
            int hiddenActiveRows = 0;
            for (int i = 0; i < _fileActive.Length; i++)
            {
                if (!_fileActive[i] || _fileCompleted[i])
                    continue;

                if (activeRows >= MaxActiveFileRows)
                {
                    hiddenActiveRows++;
                    continue;
                }

                steps.Add(new MinecraftInstallStepProgress(
                    StageFor(i).Name,
                    _fileDetails[i] ?? "正在下载",
                    _fileProgress[i],
                    MinecraftInstallStepState.Running));
                activeRows++;
            }

            if (hiddenActiveRows > 0)
            {
                steps.Add(new MinecraftInstallStepProgress(
                    "其他下载线程",
                    $"还有 {hiddenActiveRows} 个文件正在下载",
                    _progressSum / _fileProgress.Length,
                    MinecraftInstallStepState.Running));
            }

            return steps.ToArray();
        }

        private static DownloadStageAggregate[] CreateStageAggregates(
            IReadOnlyList<PlannedDownload> files,
            int[] fileStageIndexes)
        {
            Dictionary<string, int> stageIndexes = new(StringComparer.Ordinal);
            List<DownloadStageAggregate> stages = [];
            for (int i = 0; i < files.Count; i++)
            {
                string stageName = files[i].Stage;
                if (!stageIndexes.TryGetValue(stageName, out int stageIndex))
                {
                    stageIndex = stages.Count;
                    stageIndexes.Add(stageName, stageIndex);
                    stages.Add(new DownloadStageAggregate(stageName));
                }

                fileStageIndexes[i] = stageIndex;
                stages[stageIndex].TotalFiles++;
            }

            return stages.ToArray();
        }
    }

    private sealed class DownloadStageAggregate(string name)
    {
        public string Name { get; } = name;
        public int TotalFiles { get; set; }
        public int CompletedFiles { get; set; }
        public int ActiveFiles { get; set; }
        public double ProgressSum { get; set; }
        public bool Started { get; set; }
    }
}
