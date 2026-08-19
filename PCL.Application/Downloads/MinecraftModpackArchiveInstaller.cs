// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Application.Instances;
using PCL.Core.IO.Net;
using PCL.Core.Logging;

namespace PCL.Application.Downloads;

public enum MinecraftModpackFormat
{
    PclN,
    Modrinth,
    CurseForge,
    MultiMc
}

public sealed record MinecraftModpackInspection(
    MinecraftModpackFormat Format,
    string Name,
    string Version,
    string? MinecraftVersion,
    MinecraftLoaderInstallRequest? Loader,
    int ResourceCount,
    long UncompressedSize,
    string SuggestedInstanceId);

public sealed record MinecraftModpackInstallRequest
{
    public required string ArchivePath { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public bool PreferOfficialSource { get; init; } = true;
    public int DownloadThreadLimit { get; init; } = 64;
    public string JavaExecutablePath { get; init; } = "java";
    public ICurseForgeModpackFileResolver? CurseForgeResolver { get; init; }
}

public sealed record MinecraftModpackInstallProgress(
    string Stage,
    string Detail,
    double Progress,
    int CompletedFiles = 0,
    int TotalFiles = 0,
    long SpeedBytesPerSecond = 0);

public sealed record MinecraftModpackInstallResult(
    MinecraftModpackFormat Format,
    string Name,
    string Version,
    string VersionId,
    string MinecraftRootDirectory,
    string InstanceDirectory,
    int InstalledResources);

public sealed record CurseForgeModpackFile(
    string RelativePath,
    IReadOnlyList<Uri> DownloadUris,
    long Size,
    string? HashAlgorithm,
    string? Hash);

public interface ICurseForgeModpackFileResolver
{
    ValueTask<CurseForgeModpackFile> ResolveAsync(
        long projectId,
        long fileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Detects and transactionally installs common local modpack archives. PCL N exports are
/// imported fully offline; Modrinth, CurseForge and MultiMC packs install their declared
/// Minecraft/loader version and place pack files in an isolated instance directory.
/// </summary>
public sealed class MinecraftModpackArchiveInstaller
{
    private const int MaxEntries = 100_000;
    private const long MaxSingleEntrySize = 4L * 1024 * 1024 * 1024;
    private const long MaxTotalUncompressedSize = 16L * 1024 * 1024 * 1024;
    private readonly MinecraftVanillaInstallService _minecraftInstaller;
    private readonly HttpClient _httpClient;

    public MinecraftModpackArchiveInstaller(
        MinecraftVanillaInstallService? minecraftInstaller = null,
        HttpClient? httpClient = null)
    {
        _minecraftInstaller = minecraftInstaller ?? new MinecraftVanillaInstallService(httpClient);
        _httpClient = httpClient ?? PortableHttp.Client;
    }

    public static bool CanInstall(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return false;
        string extension = Path.GetExtension(archivePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            _ = Inspect(archivePath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static MinecraftModpackInspection Inspect(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        archivePath = Path.GetFullPath(archivePath);
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ArchivePlan plan = ReadPlan(archive, archivePath);
        return plan.Inspection;
    }

    /// <param name="installVersionAsync">
    /// Optional host-owned version installer (same path as PageDownloadInstall).
    /// When null, uses the injected <see cref="MinecraftVanillaInstallService"/>.
    /// </param>
    /// <param name="versionInstallProgress">
    /// Progress for the version/loader stage only. When provided, raw
    /// <see cref="MinecraftInstallProgress"/> is forwarded here (task-manager install UI);
    /// <paramref name="progress"/> is used for pack-content stages.
    /// </param>
    public async Task<MinecraftModpackInstallResult> InstallAsync(
        MinecraftModpackInstallRequest request,
        IProgress<MinecraftModpackInstallProgress>? progress = null,
        Func<MinecraftInstallRequest, IProgress<MinecraftInstallProgress>?, CancellationToken, Task<MinecraftInstallResult>>? installVersionAsync = null,
        IProgress<MinecraftInstallProgress>? versionInstallProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftRootDirectory);
        string archivePath = Path.GetFullPath(request.ArchivePath);
        string minecraftRoot = Path.GetFullPath(request.MinecraftRootDirectory);
        string versionsRoot = Path.Combine(minecraftRoot, "versions");
        Directory.CreateDirectory(versionsRoot);

        ArchivePlan plan;
        using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            plan = ReadPlan(archive, archivePath);

        string versionId = CreateUniqueVersionId(versionsRoot, plan.Inspection.SuggestedInstanceId);
        if (plan.Inspection.Loader is not null &&
            string.Equals(versionId, plan.Inspection.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            versionId = CreateUniqueVersionId(versionsRoot, versionId + "-Modpack");
        }
        PortableLog.Info(
            "ModpackInstall",
            $"识别整合包；格式={plan.Inspection.Format}；名称={plan.Inspection.Name}；版本={plan.Inspection.Version}；目标={versionId}。");
        progress?.Report(new MinecraftModpackInstallProgress("已识别整合包", plan.Inspection.Name, 0.02d));

        if (plan.Inspection.Format == MinecraftModpackFormat.PclN)
        {
            return await ImportPclNArchiveAsync(
                    archivePath,
                    minecraftRoot,
                    versionId,
                    plan,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(plan.Inspection.MinecraftVersion))
            throw new InvalidDataException("整合包未声明 Minecraft 版本。");

        IReadOnlyList<MinecraftVersionManifestEntry> versions = await _minecraftInstaller
            .GetVersionManifestAsync(request.PreferOfficialSource, cancellationToken)
            .ConfigureAwait(false);
        MinecraftVersionManifestEntry baseVersion = versions.FirstOrDefault(version =>
                string.Equals(version.Id, plan.Inspection.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Minecraft 版本清单中找不到 {plan.Inspection.MinecraftVersion}。");

        // Pre-create the target instance folder so pack mods/overrides can download in parallel
        // while the shared version installer runs (same controller as PageDownloadInstall).
        string instanceDirectory = Path.Combine(versionsRoot, versionId);
        Directory.CreateDirectory(instanceDirectory);

        using CancellationTokenSource packCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int totalResources = plan.ModrinthFiles.Count + plan.CurseForgeFiles.Count;
        // Mute pack progress until version install finishes so the task UI matches download-page install.
        bool versionStageComplete = false;
        Progress<MinecraftModpackInstallProgress> packProgress = new(value =>
        {
            if (versionStageComplete)
                progress?.Report(value);
        });
        Task<int> packDownloadTask = DownloadPackResourcesAsync(
            plan,
            request,
            instanceDirectory,
            packProgress,
            totalResources,
            packCts.Token);

        MinecraftInstallRequest versionRequest = new()
        {
            VersionId = versionId,
            BaseVersionId = baseVersion.Id,
            VersionJsonUrl = baseVersion.Url,
            MinecraftRootDirectory = minecraftRoot,
            PreferOfficialSource = request.PreferOfficialSource,
            DownloadThreadLimit = request.DownloadThreadLimit,
            Loader = plan.Inspection.Loader,
            ReplaceExistingVersion = false,
            JavaExecutablePath = request.JavaExecutablePath
        };

        IProgress<MinecraftInstallProgress>? minecraftProgress = versionInstallProgress;
        if (minecraftProgress is null)
        {
            minecraftProgress = new Progress<MinecraftInstallProgress>(value =>
            {
                progress?.Report(new MinecraftModpackInstallProgress(
                    value.Stage,
                    value.Detail,
                    0.05d + value.Progress * 0.6d,
                    value.CompletedFiles,
                    value.TotalFiles,
                    value.SpeedBytesPerSecond));
            });
        }

        Func<MinecraftInstallRequest, IProgress<MinecraftInstallProgress>?, CancellationToken, Task<MinecraftInstallResult>>
            versionInstaller = installVersionAsync ??
            ((req, prog, token) => _minecraftInstaller.InstallAsync(req, prog, token));

        MinecraftInstallResult installed;
        try
        {
            installed = await versionInstaller(versionRequest, minecraftProgress, cancellationToken)
                .ConfigureAwait(false);
            versionStageComplete = true;
        }
        catch
        {
            await packCts.CancelAsync().ConfigureAwait(false);
            try
            {
                _ = await packDownloadTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignored: version failure is the primary error.
            }

            TryDeleteDirectory(instanceDirectory);
            throw;
        }

        int installedResources;
        try
        {
            installedResources = await packDownloadTask.ConfigureAwait(false);

            // Prefer the installer-reported instance directory (should match pre-created path).
            string targetDirectory = installed.InstanceDirectory;
            if (!string.Equals(targetDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(instanceDirectory))
            {
                // External loaders always land on versions/{VersionId}; keep files there if paths diverge.
                if (Directory.Exists(targetDirectory) &&
                    !string.Equals(targetDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    MergeDirectoryContents(instanceDirectory, targetDirectory);
                    TryDeleteDirectory(instanceDirectory);
                }
                else
                {
                    targetDirectory = instanceDirectory;
                }
            }

            progress?.Report(new MinecraftModpackInstallProgress(
                "正在释放整合包文件",
                plan.Inspection.Name,
                0.93d,
                installedResources,
                totalResources));
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                ExtractOverrides(archive, plan.OverridePrefixes, targetDirectory, cancellationToken);

            await InstanceMetadataStore.SaveAsync(
                    targetDirectory,
                    new InstanceMetadata
                    {
                        Description = $"{FormatName(plan.Inspection.Format)}整合包",
                        ModpackVersion = plan.Inspection.Version,
                        InstanceIsolation = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new MinecraftModpackInstallProgress(
                "整合包安装完成",
                versionId,
                1d,
                installedResources,
                totalResources));
            PortableLog.Info("ModpackInstall", $"整合包安装完成；目标={targetDirectory}；资源={installedResources}。");
            return new MinecraftModpackInstallResult(
                plan.Inspection.Format,
                plan.Inspection.Name,
                plan.Inspection.Version,
                versionId,
                minecraftRoot,
                targetDirectory,
                installedResources);
        }
        catch
        {
            TryDeleteDirectory(installed.InstanceDirectory);
            TryDeleteDirectory(instanceDirectory);
            throw;
        }
    }

    private async Task<int> DownloadPackResourcesAsync(
        ArchivePlan plan,
        MinecraftModpackInstallRequest request,
        string instanceDirectory,
        IProgress<MinecraftModpackInstallProgress>? progress,
        int totalResources,
        CancellationToken cancellationToken)
    {
        int installedResources = 0;
        if (plan.ModrinthFiles.Count > 0)
        {
            installedResources += await DownloadModrinthFilesAsync(
                    plan.ModrinthFiles,
                    instanceDirectory,
                    progress,
                    totalResources,
                    installedResources,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (plan.CurseForgeFiles.Count > 0)
        {
            ICurseForgeModpackFileResolver resolver = request.CurseForgeResolver ??
                new HttpCurseForgeModpackFileResolver(_httpClient);
            installedResources += await DownloadCurseForgeFilesAsync(
                    plan.CurseForgeFiles,
                    resolver,
                    instanceDirectory,
                    progress,
                    totalResources,
                    installedResources,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return installedResources;
    }

    private static void MergeDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, file);
            string target = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                File.Move(file, target);
        }
    }

    private static async Task<MinecraftModpackInstallResult> ImportPclNArchiveAsync(
        string archivePath,
        string minecraftRoot,
        string versionId,
        ArchivePlan plan,
        IProgress<MinecraftModpackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string versionsRoot = Path.Combine(minecraftRoot, "versions");
        string targetDirectory = Path.Combine(versionsRoot, versionId);
        string stagingDirectory = Path.Combine(versionsRoot, $".pcln-import-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                int completed = 0;
                ZipArchiveEntry[] files = archive.Entries.Where(static entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
                foreach (ZipArchiveEntry entry in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = NormalizeArchivePath(entry.FullName);
                    string mapped = !string.IsNullOrEmpty(plan.PclCorePrefix) &&
                                    path.StartsWith(plan.PclCorePrefix, StringComparison.OrdinalIgnoreCase)
                        ? path[plan.PclCorePrefix.Length..]
                        : path;
                    ExtractEntry(entry, stagingDirectory, mapped, overwrite: false, cancellationToken);
                    completed++;
                    progress?.Report(new MinecraftModpackInstallProgress(
                        "正在导入 PCL N 整合包",
                        mapped,
                        0.05d + 0.85d * completed / Math.Max(1d, files.Length),
                        completed,
                        files.Length));
                }
            }

            string originalBase = plan.PclCoreBaseName
                ?? throw new InvalidDataException("PCL N 整合包缺少版本核心文件名。");
            string originalJson = Path.Combine(stagingDirectory, originalBase + ".json");
            if (!File.Exists(originalJson))
                throw new InvalidDataException("PCL N 整合包缺少版本 JSON。");
            string targetJson = Path.Combine(stagingDirectory, versionId + ".json");
            if (!string.Equals(originalJson, targetJson, StringComparison.OrdinalIgnoreCase))
                File.Move(originalJson, targetJson, overwrite: false);
            string originalJar = Path.Combine(stagingDirectory, originalBase + ".jar");
            string targetJar = Path.Combine(stagingDirectory, versionId + ".jar");
            if (File.Exists(originalJar) && !string.Equals(originalJar, targetJar, StringComparison.OrdinalIgnoreCase))
                File.Move(originalJar, targetJar, overwrite: false);

            await RewriteVersionIdAsync(targetJson, versionId, cancellationToken).ConfigureAwait(false);
            await InstanceMetadataStore.UpdateAsync(
                    stagingDirectory,
                    metadata => metadata with
                    {
                        ModpackVersion = string.IsNullOrWhiteSpace(metadata.ModpackVersion)
                            ? plan.Inspection.Version
                            : metadata.ModpackVersion,
                        InstanceIsolation = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            Directory.Move(stagingDirectory, targetDirectory);
            progress?.Report(new MinecraftModpackInstallProgress("整合包安装完成", versionId, 1d));
            return new MinecraftModpackInstallResult(
                plan.Inspection.Format,
                plan.Inspection.Name,
                plan.Inspection.Version,
                versionId,
                minecraftRoot,
                targetDirectory,
                0);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            TryDeleteDirectory(targetDirectory);
            throw;
        }
    }

    private async Task<int> DownloadModrinthFilesAsync(
        IReadOnlyList<ModrinthPackFile> files,
        string instanceDirectory,
        IProgress<MinecraftModpackInstallProgress>? progress,
        int totalResources,
        int completedBefore,
        CancellationToken cancellationToken)
    {
        int completed = 0;
        foreach (ModrinthPackFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = ResolveSafeTarget(instanceDirectory, file.Path);
            await DownloadFileAsync(
                    file.DownloadUris,
                    target,
                    file.Size,
                    file.HashAlgorithm,
                    file.Hash,
                    cancellationToken)
                .ConfigureAwait(false);
            completed++;
            int current = completedBefore + completed;
            progress?.Report(new MinecraftModpackInstallProgress(
                "正在下载整合包资源",
                file.Path,
                0.65d + 0.25d * current / Math.Max(1d, totalResources),
                current,
                totalResources));
        }
        return completed;
    }

    private async Task<int> DownloadCurseForgeFilesAsync(
        IReadOnlyList<CurseForgePackReference> files,
        ICurseForgeModpackFileResolver resolver,
        string instanceDirectory,
        IProgress<MinecraftModpackInstallProgress>? progress,
        int totalResources,
        int completedBefore,
        CancellationToken cancellationToken)
    {
        int completed = 0;
        foreach (CurseForgePackReference reference in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurseForgeModpackFile file = await resolver
                .ResolveAsync(reference.ProjectId, reference.FileId, cancellationToken)
                .ConfigureAwait(false);
            string target = ResolveSafeTarget(instanceDirectory, file.RelativePath);
            await DownloadFileAsync(
                    file.DownloadUris,
                    target,
                    file.Size,
                    file.HashAlgorithm,
                    file.Hash,
                    cancellationToken)
                .ConfigureAwait(false);
            completed++;
            int current = completedBefore + completed;
            progress?.Report(new MinecraftModpackInstallProgress(
                "正在下载 CurseForge 资源",
                file.RelativePath,
                0.65d + 0.25d * current / Math.Max(1d, totalResources),
                current,
                totalResources));
        }
        return completed;
    }

    private async Task DownloadFileAsync(
        IReadOnlyList<Uri> downloadUris,
        string targetPath,
        long expectedSize,
        string? hashAlgorithm,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (downloadUris.Count == 0)
            throw new InvalidDataException($"整合包资源没有下载地址：{targetPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string temporaryPath = targetPath + $".pcln-download-{Guid.NewGuid():N}";
        Exception? lastError = null;
        try
        {
            foreach (Uri uri in downloadUris)
            {
                try
                {
                    using HttpResponseMessage response = await _httpClient
                        .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using FileStream target = new(
                        temporaryPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    IncrementalHash? hasher = CreateHasher(hashAlgorithm);
                    long written = 0;
                    try
                    {
                        while (true)
                        {
                            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                                break;
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            hasher?.AppendData(buffer, 0, read);
                            written += read;
                        }
                        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (expectedSize > 0 && written != expectedSize)
                            throw new InvalidDataException($"下载文件大小不匹配：期望 {expectedSize}，实际 {written}。");
                        if (hasher is not null && !string.IsNullOrWhiteSpace(expectedHash))
                        {
                            string actual = Convert.ToHexString(hasher.GetHashAndReset());
                            if (!CryptographicOperations.FixedTimeEquals(
                                    Convert.FromHexString(actual),
                                    Convert.FromHexString(expectedHash)))
                            {
                                throw new InvalidDataException($"下载文件哈希不匹配：{Path.GetFileName(targetPath)}");
                            }
                        }
                    }
                    finally
                    {
                        hasher?.Dispose();
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    File.Move(temporaryPath, targetPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or FormatException)
                {
                    lastError = ex;
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        throw new IOException($"无法下载整合包资源：{Path.GetFileName(targetPath)}", lastError);
    }

    private static IncrementalHash? CreateHasher(string? algorithm) => algorithm?.ToLowerInvariant() switch
    {
        "sha1" => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
        "sha256" => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
        "sha512" => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
        null or "" => null,
        _ => throw new InvalidDataException($"不支持的整合包哈希算法：{algorithm}")
    };

    private static ArchivePlan ReadPlan(ZipArchive archive, string archivePath)
    {
        Dictionary<string, ZipArchiveEntry> entries = ValidateArchive(archive);
        long totalSize = entries.Values.Sum(static entry => entry.Length);
        if (TryReadModrinth(entries, archivePath, totalSize, out ArchivePlan? modrinth))
            return modrinth!;
        if (TryReadCurseForge(entries, archivePath, totalSize, out ArchivePlan? curseForge))
            return curseForge!;
        if (TryReadMultiMc(entries, archivePath, totalSize, out ArchivePlan? multiMc))
            return multiMc!;
        if (TryReadPclN(entries, archivePath, totalSize, out ArchivePlan? pclN))
            return pclN!;
        throw new InvalidDataException("无法识别整合包格式。支持 PCL N、Modrinth、CurseForge 与 MultiMC 整合包。");
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException($"整合包文件数量超过限制：{archive.Entries.Count}。");
        Dictionary<string, ZipArchiveEntry> result = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = NormalizeArchivePath(entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            if (entry.Length > MaxSingleEntrySize)
                throw new InvalidDataException($"整合包单文件过大：{path}");
            total = checked(total + entry.Length);
            if (total > MaxTotalUncompressedSize)
                throw new InvalidDataException("整合包解压后大小超过 16 GiB 限制。");
            uint unixMode = unchecked((uint)entry.ExternalAttributes) >> 16;
            if ((unixMode & 0xF000u) == 0xA000u)
                throw new InvalidDataException($"整合包禁止包含符号链接：{path}");
            if (!result.TryAdd(path, entry))
                throw new InvalidDataException($"整合包包含重复路径：{path}");
        }
        return result;
    }

    private static bool TryReadModrinth(
        Dictionary<string, ZipArchiveEntry> entries,
        string archivePath,
        long totalSize,
        out ArchivePlan? plan)
    {
        plan = null;
        if (!entries.TryGetValue("modrinth.index.json", out ZipArchiveEntry? index))
            return false;
        using JsonDocument document = ReadJson(index);
        JsonElement root = document.RootElement;
        string name = ReadString(root, "name") ?? Path.GetFileNameWithoutExtension(archivePath);
        string version = ReadString(root, "versionId") ?? "1.0.0";
        if (!TryGetProperty(root, "dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Modrinth 整合包缺少 dependencies。");
        string? minecraftVersion = ReadString(dependencies, "minecraft");
        MinecraftLoaderInstallRequest? loader = ReadModrinthLoader(dependencies);
        List<ModrinthPackFile> files = [];
        if (TryGetProperty(root, "files", out JsonElement fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement file in fileArray.EnumerateArray())
            {
                if (IsClientUnsupported(file))
                    continue;
                string path = NormalizeArchivePath(ReadString(file, "path")
                    ?? throw new InvalidDataException("Modrinth 文件缺少 path。"));
                List<Uri> downloads = ReadUriArray(file, "downloads");
                (string? algorithm, string? hash) = ReadPreferredHash(file);
                files.Add(new ModrinthPackFile(path, downloads, ReadInt64(file, "fileSize"), algorithm, hash));
            }
        }
        string[] prefixes = ["overrides/", "client-overrides/"];
        MinecraftModpackInspection inspection = new(
            MinecraftModpackFormat.Modrinth,
            name,
            version,
            minecraftVersion,
            loader,
            files.Count,
            totalSize,
            SanitizeVersionId(name, archivePath));
        plan = new ArchivePlan(inspection, prefixes, null, null, files, []);
        return true;
    }

    private static bool TryReadCurseForge(
        Dictionary<string, ZipArchiveEntry> entries,
        string archivePath,
        long totalSize,
        out ArchivePlan? plan)
    {
        plan = null;
        if (!entries.TryGetValue("manifest.json", out ZipArchiveEntry? manifest))
            return false;
        using JsonDocument document = ReadJson(manifest);
        JsonElement root = document.RootElement;
        if (!TryGetProperty(root, "minecraft", out JsonElement minecraft) || minecraft.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, "files", out JsonElement fileArray) || fileArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string name = ReadString(root, "name") ?? Path.GetFileNameWithoutExtension(archivePath);
        string version = ReadString(root, "version") ?? "1.0.0";
        string? minecraftVersion = ReadString(minecraft, "version");
        MinecraftLoaderInstallRequest? loader = ReadCurseForgeLoader(minecraft);
        List<CurseForgePackReference> files = [];
        foreach (JsonElement file in fileArray.EnumerateArray())
        {
            if (TryGetProperty(file, "required", out JsonElement required) && required.ValueKind == JsonValueKind.False)
                continue;
            long projectId = ReadInt64(file, "projectID");
            long fileId = ReadInt64(file, "fileID");
            if (projectId <= 0 || fileId <= 0)
                throw new InvalidDataException("CurseForge 整合包包含无效的 projectID/fileID。");
            files.Add(new CurseForgePackReference(projectId, fileId));
        }
        string overrideName = ReadString(root, "overrides") ?? "overrides";
        string prefix = NormalizeArchivePath(overrideName).TrimEnd('/') + "/";
        MinecraftModpackInspection inspection = new(
            MinecraftModpackFormat.CurseForge,
            name,
            version,
            minecraftVersion,
            loader,
            files.Count,
            totalSize,
            SanitizeVersionId(name, archivePath));
        plan = new ArchivePlan(inspection, [prefix], null, null, [], files);
        return true;
    }

    private static bool TryReadMultiMc(
        Dictionary<string, ZipArchiveEntry> entries,
        string archivePath,
        long totalSize,
        out ArchivePlan? plan)
    {
        plan = null;
        if (!entries.TryGetValue("mmc-pack.json", out ZipArchiveEntry? manifest))
            return false;
        using JsonDocument document = ReadJson(manifest);
        JsonElement root = document.RootElement;
        if (!TryGetProperty(root, "components", out JsonElement components) || components.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MultiMC 整合包缺少 components。");
        string? minecraftVersion = null;
        MinecraftLoaderInstallRequest? loader = null;
        foreach (JsonElement component in components.EnumerateArray())
        {
            string uid = ReadString(component, "uid") ?? string.Empty;
            string componentVersion = ReadString(component, "version") ?? string.Empty;
            if (uid.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase))
                minecraftVersion = componentVersion;
            else if (TryMapMultiMcLoader(uid, componentVersion, out MinecraftLoaderInstallRequest? mapped))
                loader ??= mapped;
        }

        string name = ReadMultiMcName(entries) ?? Path.GetFileNameWithoutExtension(archivePath);
        MinecraftModpackInspection inspection = new(
            MinecraftModpackFormat.MultiMc,
            name,
            "1.0.0",
            minecraftVersion,
            loader,
            0,
            totalSize,
            SanitizeVersionId(name, archivePath));
        plan = new ArchivePlan(inspection, [".minecraft/", "minecraft/"], null, null, [], []);
        return true;
    }

    private static bool TryReadPclN(
        Dictionary<string, ZipArchiveEntry> entries,
        string archivePath,
        long totalSize,
        out ArchivePlan? plan)
    {
        plan = null;
        foreach ((string path, ZipArchiveEntry entry) in entries.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] segments = path.Split('/');
            if (segments.Length > 2)
                continue;
            string baseName = Path.GetFileNameWithoutExtension(segments[^1]);
            if (segments.Length == 2 && !segments[0].Equals(baseName, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using JsonDocument document = ReadJson(entry);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    (!TryGetProperty(root, "libraries", out _) &&
                     !TryGetProperty(root, "downloads", out _) &&
                     !TryGetProperty(root, "inheritsFrom", out _) &&
                     !TryGetProperty(root, "mainClass", out _)))
                {
                    continue;
                }
                string name = segments.Length == 2 ? segments[0] : baseName;
                string version = ReadString(root, "inheritsFrom") ?? ReadString(root, "id") ?? name;
                string prefix = segments.Length == 2 ? segments[0] + "/" : string.Empty;
                MinecraftModpackInspection inspection = new(
                    MinecraftModpackFormat.PclN,
                    name,
                    version,
                    version,
                    null,
                    0,
                    totalSize,
                    SanitizeVersionId(name, archivePath));
                plan = new ArchivePlan(inspection, [], prefix, baseName, [], []);
                return true;
            }
            catch (JsonException)
            {
            }
        }
        return false;
    }

    private static MinecraftLoaderInstallRequest? ReadModrinthLoader(JsonElement dependencies)
    {
        (string Key, MinecraftLoaderKind Kind)[] mappings =
        [
            ("neoforge", MinecraftLoaderKind.NeoForge),
            ("forge", MinecraftLoaderKind.Forge),
            ("fabric-loader", MinecraftLoaderKind.Fabric),
            ("quilt-loader", MinecraftLoaderKind.Quilt)
        ];
        foreach ((string key, MinecraftLoaderKind kind) in mappings)
        {
            string? version = ReadString(dependencies, key);
            if (!string.IsNullOrWhiteSpace(version))
                return new MinecraftLoaderInstallRequest(kind, version);
        }
        return null;
    }

    private static MinecraftLoaderInstallRequest? ReadCurseForgeLoader(JsonElement minecraft)
    {
        if (!TryGetProperty(minecraft, "modLoaders", out JsonElement loaders) || loaders.ValueKind != JsonValueKind.Array)
            return null;
        JsonElement loader = default;
        bool found = false;
        foreach (JsonElement candidate in loaders.EnumerateArray())
        {
            if (!found)
            {
                loader = candidate;
                found = true;
            }
            if (TryGetProperty(candidate, "primary", out JsonElement primary) && primary.ValueKind == JsonValueKind.True)
            {
                loader = candidate;
                break;
            }
        }
        if (!found)
            return null;
        string id = ReadString(loader, "id") ?? string.Empty;
        int separator = id.IndexOf('-');
        if (separator <= 0 || separator == id.Length - 1)
            return null;
        string kind = id[..separator].ToLowerInvariant();
        string version = id[(separator + 1)..];
        MinecraftLoaderKind? mapped = kind switch
        {
            "forge" or "minecraftforge" => MinecraftLoaderKind.Forge,
            "neoforge" => MinecraftLoaderKind.NeoForge,
            "fabric" => MinecraftLoaderKind.Fabric,
            "quilt" => MinecraftLoaderKind.Quilt,
            "liteloader" => MinecraftLoaderKind.LiteLoader,
            _ => null
        };
        return mapped is { } value ? new MinecraftLoaderInstallRequest(value, version) : null;
    }

    private static bool TryMapMultiMcLoader(
        string uid,
        string version,
        out MinecraftLoaderInstallRequest? loader)
    {
        MinecraftLoaderKind? kind = uid.ToLowerInvariant() switch
        {
            "net.minecraftforge" => MinecraftLoaderKind.Forge,
            "net.neoforged" or "net.neoforged.neoforge" => MinecraftLoaderKind.NeoForge,
            "net.fabricmc.fabric-loader" => MinecraftLoaderKind.Fabric,
            "org.quiltmc.quilt-loader" => MinecraftLoaderKind.Quilt,
            "com.mumfrey.liteloader" => MinecraftLoaderKind.LiteLoader,
            _ => null
        };
        loader = kind is { } value && !string.IsNullOrWhiteSpace(version)
            ? new MinecraftLoaderInstallRequest(value, version)
            : null;
        return loader is not null;
    }

    private static string? ReadMultiMcName(Dictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.TryGetValue("instance.cfg", out ZipArchiveEntry? config))
            return null;
        using StreamReader reader = new(config.Open());
        while (reader.ReadLine() is { } line)
        {
            int separator = line.IndexOf('=');
            if (separator > 0 && line[..separator].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
                return line[(separator + 1)..].Trim();
        }
        return null;
    }

    private static void ExtractOverrides(
        ZipArchive archive,
        IReadOnlyList<string> prefixes,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        foreach (string prefix in prefixes)
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                string path = NormalizeArchivePath(entry.FullName);
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string relative = path[prefix.Length..];
                if (string.IsNullOrWhiteSpace(relative))
                    continue;
                ExtractEntry(entry, targetDirectory, relative, overwrite: true, cancellationToken);
            }
        }
    }

    private static void ExtractEntry(
        ZipArchiveEntry entry,
        string targetDirectory,
        string relativePath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string target = ResolveSafeTarget(targetDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using Stream source = entry.Open();
        using FileStream destination = new(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task RewriteVersionIdAsync(
        string versionJsonPath,
        string versionId,
        CancellationToken cancellationToken)
    {
        JsonNode node = JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException("版本 JSON 为空。");
        if (node is not JsonObject root)
            throw new InvalidDataException("版本 JSON 根节点不是对象。");
        root["id"] = versionId;
        string temporary = versionJsonPath + ".tmp";
        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true))
        {
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
            root.WriteTo(writer);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, versionJsonPath, overwrite: true);
    }

    private static string ResolveSafeTarget(string rootDirectory, string relativePath)
    {
        string normalized = NormalizeArchivePath(relativePath);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        string target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"整合包路径越界：{relativePath}");
        return target;
    }

    private static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("整合包包含空路径。");
        string normalized = path.Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(path) || normalized.Contains('\0'))
            throw new InvalidDataException($"整合包包含非法路径：{path}");
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".." || segment.Contains(':')))
            throw new InvalidDataException($"整合包包含非法路径：{path}");
        return string.Join('/', segments);
    }

    private static string CreateUniqueVersionId(string versionsRoot, string suggested)
    {
        string baseId = string.IsNullOrWhiteSpace(suggested) ? "Imported-Modpack" : suggested;
        string candidate = baseId;
        for (int suffix = 2; Directory.Exists(Path.Combine(versionsRoot, candidate)); suffix++)
            candidate = baseId + "-" + suffix.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static string SanitizeVersionId(string? name, string archivePath)
    {
        string value = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(archivePath) : name.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character =>
            invalid.Contains(character) || char.IsControl(character) || character is '/' or '\\' ? '_' : character).ToArray());
        result = result.Trim().Trim('.');
        if (result.Length > 80)
            result = result[..80].Trim();
        return string.IsNullOrWhiteSpace(result) ? "Imported-Modpack" : result;
    }

    private static string FormatName(MinecraftModpackFormat format) => format switch
    {
        MinecraftModpackFormat.Modrinth => "Modrinth ",
        MinecraftModpackFormat.CurseForge => "CurseForge ",
        MinecraftModpackFormat.MultiMc => "MultiMC ",
        _ => "PCL N "
    };

    private static bool IsClientUnsupported(JsonElement file) =>
        TryGetProperty(file, "env", out JsonElement environment) &&
        ReadString(environment, "client")?.Equals("unsupported", StringComparison.OrdinalIgnoreCase) == true;

    private static (string? Algorithm, string? Hash) ReadPreferredHash(JsonElement file)
    {
        if (!TryGetProperty(file, "hashes", out JsonElement hashes) || hashes.ValueKind != JsonValueKind.Object)
            return (null, null);
        foreach (string algorithm in new[] { "sha512", "sha256", "sha1" })
        {
            string? hash = ReadString(hashes, algorithm);
            if (!string.IsNullOrWhiteSpace(hash))
                return (algorithm, hash);
        }
        return (null, null);
    }

    private static List<Uri> ReadUriArray(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
            return [];
        List<Uri> result = [];
        foreach (JsonElement value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? uri) &&
                uri.Scheme is "http" or "https")
            {
                result.Add(uri);
            }
        }
        return result;
    }

    private static JsonDocument ReadJson(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadInt64(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.TryGetInt64(out long parsed)
            ? parsed
            : 0L;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
            return true;
        value = default;
        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PortableLog.Warn(ex, "ModpackInstall", $"清理未完成的整合包目录失败：{path}");
        }
    }

    private sealed record ArchivePlan(
        MinecraftModpackInspection Inspection,
        IReadOnlyList<string> OverridePrefixes,
        string? PclCorePrefix,
        string? PclCoreBaseName,
        IReadOnlyList<ModrinthPackFile> ModrinthFiles,
        IReadOnlyList<CurseForgePackReference> CurseForgeFiles);

    private sealed record ModrinthPackFile(
        string Path,
        IReadOnlyList<Uri> DownloadUris,
        long Size,
        string? HashAlgorithm,
        string? Hash);

    private sealed record CurseForgePackReference(long ProjectId, long FileId);
}

public sealed class HttpCurseForgeModpackFileResolver : ICurseForgeModpackFileResolver
{
    private const string ApiRoot = "https://api.curseforge.com/v1";
    private const string McimCurseForgeApiRoot = "https://mod.mcimirror.top/curseforge/v1";
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;

    public HttpCurseForgeModpackFileResolver(HttpClient? httpClient = null, string? apiKey = null)
    {
        _httpClient = httpClient ?? PortableHttp.Client;
        _apiKey = string.IsNullOrWhiteSpace(apiKey)
            ? Environment.GetEnvironmentVariable("PCL_CURSEFORGE_API_KEY") ??
              Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY")
            : apiKey.Trim();
    }

    public async ValueTask<CurseForgeModpackFile> ResolveAsync(
        long projectId,
        long fileId,
        CancellationToken cancellationToken = default)
    {
        if (projectId <= 0 || fileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(projectId));

        // Prefer official API when a key is present; otherwise fall back to MCIM mirror + CDN
        // (aligns with CE allowMirror / DlSourceModDownloadGet behavior).
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            try
            {
                return await ResolveViaApiAsync(projectId, fileId, ApiRoot, requireApiKey: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PortableLog.Warn(
                    "ModpackInstall",
                    $"CurseForge API 解析失败，尝试镜像：project={projectId} file={fileId}；{ex.Message}");
            }
        }

        try
        {
            return await ResolveViaApiAsync(
                    projectId,
                    fileId,
                    McimCurseForgeApiRoot,
                    requireApiKey: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PortableLog.Warn(
                "ModpackInstall",
                $"CurseForge 镜像解析失败，回退 CDN：project={projectId} file={fileId}；{ex.Message}");
        }

        // Last resort: forgecdn path with a synthetic file name (works for many packs).
        string fallbackName = fileId.ToString(CultureInfo.InvariantCulture) + ".jar";
        Uri cdn = CreateCdnUri(fileId, fallbackName);
        return new CurseForgeModpackFile(
            "mods/" + fallbackName,
            [cdn, CreateMcimCdnUri(cdn)],
            0L,
            null,
            null);
    }

    private async Task<CurseForgeModpackFile> ResolveViaApiAsync(
        long projectId,
        long fileId,
        string apiRoot,
        bool requireApiKey,
        CancellationToken cancellationToken)
    {
        if (requireApiKey && string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("安装 CurseForge 整合包需要配置 PCL_CURSEFORGE_API_KEY。");

        JsonElement project = await GetDataAsync(apiRoot, $"/mods/{projectId}", requireApiKey, cancellationToken)
            .ConfigureAwait(false);
        JsonElement file = await GetDataAsync(apiRoot, $"/mods/{projectId}/files/{fileId}", requireApiKey, cancellationToken)
            .ConfigureAwait(false);
        int classId = TryGetProperty(project, "classId", out JsonElement classValue) && classValue.TryGetInt32(out int parsedClass)
            ? parsedClass
            : 6;
        string fileName = ReadString(file, "fileName")
            ?? throw new InvalidDataException("CurseForge 文件响应缺少 fileName。");
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException("CurseForge 返回了无效文件名。");

        string? downloadUrl = ReadString(file, "downloadUrl");
        Uri primary = !string.IsNullOrWhiteSpace(downloadUrl) && Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? parsedUri)
            ? parsedUri
            : CreateCdnUri(fileId, fileName);
        List<Uri> uris = [primary, CreateMcimCdnUri(primary)];
        if (!string.Equals(primary.Host, "edge.forgecdn.net", StringComparison.OrdinalIgnoreCase))
            uris.Add(CreateCdnUri(fileId, fileName));

        (string? algorithm, string? hash) = ReadCurseForgeHash(file);
        string directory = classId switch
        {
            12 => "resourcepacks",
            6552 => "shaderpacks",
            6945 => "datapacks",
            _ => "mods"
        };
        long size = TryGetProperty(file, "fileLength", out JsonElement length) && length.TryGetInt64(out long parsedLength)
            ? parsedLength
            : 0L;
        return new CurseForgeModpackFile(
            directory + "/" + fileName,
            uris.Distinct().ToArray(),
            size,
            algorithm,
            hash);
    }

    private async Task<JsonElement> GetDataAsync(
        string apiRoot,
        string path,
        bool includeApiKey,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, apiRoot.TrimEnd('/') + path);
        if (includeApiKey && !string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PCL-N", "1.0"));
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!TryGetProperty(document.RootElement, "data", out JsonElement data))
            throw new InvalidDataException("CurseForge 响应缺少 data。");
        return data.Clone();
    }

    private static (string? Algorithm, string? Hash) ReadCurseForgeHash(JsonElement file)
    {
        if (!TryGetProperty(file, "hashes", out JsonElement hashes) || hashes.ValueKind != JsonValueKind.Array)
            return (null, null);
        foreach (int algorithmId in new[] { 3, 1 })
        {
            foreach (JsonElement hash in hashes.EnumerateArray())
            {
                if (!TryGetProperty(hash, "algo", out JsonElement algo) || !algo.TryGetInt32(out int parsed) || parsed != algorithmId)
                    continue;
                string? value = ReadString(hash, "value");
                if (!string.IsNullOrWhiteSpace(value))
                    return (algorithmId == 3 ? "sha256" : "sha1", value);
            }
        }
        return (null, null);
    }

    private static Uri CreateCdnUri(long fileId, string fileName)
    {
        long group = fileId / 1000L;
        long suffix = fileId % 1000L;
        return new Uri(
            $"https://edge.forgecdn.net/files/{group.ToString(CultureInfo.InvariantCulture)}/{suffix.ToString("000", CultureInfo.InvariantCulture)}/{Uri.EscapeDataString(fileName)}");
    }

    private static Uri CreateMcimCdnUri(Uri officialOrCdn)
    {
        string text = officialOrCdn.AbsoluteUri;
        foreach (string root in new[]
                 {
                     "https://edge.forgecdn.net",
                     "https://media.forgecdn.net",
                     "https://mediafilez.forgecdn.net"
                 })
        {
            if (text.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return new Uri("https://mod.mcimirror.top" + text[root.Length..]);
        }

        if (text.StartsWith("https://api.curseforge.com", StringComparison.OrdinalIgnoreCase))
            return new Uri("https://mod.mcimirror.top/curseforge" + text["https://api.curseforge.com".Length..]);

        return officialOrCdn;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
            return true;
        value = default;
        return false;
    }
}
