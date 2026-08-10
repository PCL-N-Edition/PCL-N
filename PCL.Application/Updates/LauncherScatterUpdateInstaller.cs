// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCL.Application.Updates;

public sealed partial class LauncherUpdateInstaller
{
    private static readonly StringComparison FilePathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private async Task<PreparedTreePayload> ApplyScatterPatchChainAsync(
        LauncherUpdatePackage package,
        string currentExecutablePath,
        string hpatchzPath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        InstallContext install = ResolveInstallContext(currentExecutablePath);
        string sourceRoot = install.Root;

        for (int index = 0; index < package.PatchSteps.Count; index++)
        {
            LauncherUpdatePatchStep step = package.PatchSteps[index];
            if (!step.IsScatterBundle)
                throw new InvalidDataException("补丁链混用了单文件和散包协议。");

            string bundlePath = Path.Combine(workDirectory, $"scatter-{index + 1}.patch.zip");
            await DownloadFileAsync(
                    step.DownloadUrl,
                    bundlePath,
                    LauncherUpdateStage.DownloadingPatch,
                    index,
                    package.PatchSteps.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyFileAsync(bundlePath, step.Sha256, "散包补丁 ZIP 校验失败", cancellationToken)
                .ConfigureAwait(false);
            await VerifyDetachedSignatureAsync(
                    bundlePath,
                    step.DownloadUrl + ".asc",
                    required: true,
                    cancellationToken)
                .ConfigureAwait(false);

            string targetRoot = Path.Combine(workDirectory, $"tree-{index + 1}");
            LauncherScatterPatchManifest manifest = await ApplyScatterBundleAsync(
                    bundlePath,
                    sourceRoot,
                    targetRoot,
                    hpatchzPath,
                    step,
                    index,
                    package.PatchSteps.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            sourceRoot = targetRoot;

            if (index == package.PatchSteps.Count - 1 &&
                !string.IsNullOrWhiteSpace(step.TargetManifestSha256) &&
                !string.Equals(
                    manifest.ToManifestSha256,
                    step.TargetManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("补丁内目标文件清单与发布索引不一致。");
            }
        }

        return await PrepareTreePayloadAsync(sourceRoot, currentExecutablePath, workDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LauncherScatterPatchManifest> ApplyScatterBundleAsync(
        string bundlePath,
        string sourceRoot,
        string targetRoot,
        string hpatchzPath,
        LauncherUpdatePatchStep step,
        int stepIndex,
        int stepCount,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        ZipArchiveEntry manifestEntry = archive.GetEntry("files.json")
            ?? throw new InvalidDataException("散包补丁缺少 files.json。");
        LauncherScatterPatchManifest manifest;
        await using (Stream stream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync(
                    stream,
                    LauncherUpdateJsonContext.Default.LauncherScatterPatchManifest,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("无法解析散包补丁 files.json。");
        }

        ValidateScatterManifest(manifest, step);
        Dictionary<string, LauncherUpdateFileEntry> targetFiles = ValidateFileEntries(manifest.TargetFiles);
        Dictionary<string, LauncherScatterPatchOperation> operations = ValidateOperations(manifest.Ops, targetFiles);
        Dictionary<string, LauncherUpdateFileEntry> sourceFiles = BuildSourceFileManifest(targetFiles, operations);

        Report(
            LauncherUpdateStage.Verifying,
            (double)stepIndex / Math.Max(1, stepCount),
            $"正在校验当前散包（{stepIndex + 1}/{stepCount}）…",
            completedFiles: 0,
            totalFiles: targetFiles.Count,
            threadLimit: NormalizeDownloadThreadLimit(DownloadThreadLimit));
        await VerifyTreeAsync(sourceRoot, sourceFiles.Values, manifest.FromManifestSha256!, cancellationToken)
            .ConfigureAwait(false);

        if (Directory.Exists(targetRoot))
            Directory.Delete(targetRoot, recursive: true);
        Directory.CreateDirectory(targetRoot);

        int totalFiles = Math.Max(1, targetFiles.Count);
        int completed = 0;
        foreach (LauncherUpdateFileEntry target in targetFiles.Values.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = target.Path!;
            string output = ResolveSafeRelativePath(targetRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            if (!operations.TryGetValue(relativePath, out LauncherScatterPatchOperation? operation))
            {
                string source = ResolveSafeRelativePath(sourceRoot, relativePath);
                File.Copy(source, output, overwrite: true);
                PreserveExecutableMode(source, output);
            }
            else
            {
                await ApplyFileOperationAsync(
                        archive,
                        sourceRoot,
                        output,
                        operation,
                        hpatchzPath,
                        targetRoot,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            RestoreUnixMode(output, target.UnixMode);

            await VerifyFileEntryAsync(output, target, "补丁生成的文件校验失败", cancellationToken)
                .ConfigureAwait(false);
            completed++;
            Report(
                LauncherUpdateStage.ApplyingPatch,
                (stepIndex + (double)completed / totalFiles) / Math.Max(1, stepCount),
                $"正在重建更新文件（{completed}/{totalFiles}）…",
                completedFiles: completed,
                totalFiles: totalFiles,
                activeThreads: 1,
                threadLimit: 1);
        }

        await VerifyTreeAsync(targetRoot, targetFiles.Values, manifest.ToManifestSha256!, cancellationToken)
            .ConfigureAwait(false);
        return manifest;
    }

    private static async Task ApplyFileOperationAsync(
        ZipArchive archive,
        string sourceRoot,
        string output,
        LauncherScatterPatchOperation operation,
        string hpatchzPath,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        switch (operation.Op)
        {
            case "hdiff":
            {
                string source = ResolveSafeRelativePath(sourceRoot, operation.Path!);
                string payload = Path.Combine(targetRoot, ".patch-payload-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await ExtractAndVerifyMemberAsync(
                            archive,
                            operation.Patch!,
                            payload,
                            operation.PatchSha256!,
                            operation.PatchSize,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await RunPatchToolAsync(hpatchzPath, source, payload, output, cancellationToken).ConfigureAwait(false);
                    PreserveExecutableMode(source, output);
                }
                finally
                {
                    File.Delete(payload);
                }
                break;
            }
            case "add":
            case "replace":
                await ExtractAndVerifyMemberAsync(
                        archive,
                        operation.Blob!,
                        output,
                        operation.BlobSha256!,
                        operation.BlobSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException($"不支持的散包操作：{operation.Op}。");
        }
    }

    private async Task<PreparedFullPayload> DownloadAndExtractFullPayloadAsync(
        LauncherUpdatePackage package,
        string currentExecutablePath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        string archivePath = Path.Combine(workDirectory, package.TargetAssetName);
        await DownloadFileAsync(
                package.FullPackageUrl,
                archivePath,
                LauncherUpdateStage.DownloadingFullPackage,
                0,
                1,
                cancellationToken)
            .ConfigureAwait(false);
        if (package.FullPackageSize is > 0 && new FileInfo(archivePath).Length != package.FullPackageSize.Value)
            throw new InvalidDataException("完整更新包大小校验失败。");
        if (!string.IsNullOrWhiteSpace(package.FullPackageSha256))
        {
            await VerifyFileAsync(
                    archivePath,
                    package.FullPackageSha256,
                    "完整更新包 SHA-256 校验失败",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        await VerifyDetachedSignatureAsync(
                archivePath,
                package.FullPackageSignatureUrl,
                required: true,
                cancellationToken)
            .ConfigureAwait(false);

        Report(LauncherUpdateStage.Extracting, 0, "正在解压完整散包…");
        string targetRoot = Path.Combine(workDirectory, "tree-full");
        if (Directory.Exists(targetRoot))
            Directory.Delete(targetRoot, recursive: true);
        Directory.CreateDirectory(targetRoot);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            await ExtractZipTreeAsync(archivePath, targetRoot, cancellationToken).ConfigureAwait(false);
        else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                 archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            await ExtractTarTreeAsync(archivePath, targetRoot, cancellationToken).ConfigureAwait(false);
        else
            throw new InvalidDataException($"不支持的启动器更新包格式：{package.TargetAssetName}");

        FlattenSinglePackageRoot(targetRoot);
        bool scatterLayout = File.Exists(Path.Combine(targetRoot, "pcln-layout")) ||
                             Directory.Exists(Path.Combine(targetRoot, "host")) ||
                             Directory.Exists(Path.Combine(targetRoot, "Contents"));
        if (scatterLayout)
        {
            PreparedTreePayload tree = await PrepareTreePayloadAsync(
                    targetRoot,
                    currentExecutablePath,
                    workDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            return new PreparedFullPayload(tree.StagedEntryPath, tree);
        }

        string[] candidates = Directory.GetFiles(targetRoot, package.TargetBinaryName, SearchOption.AllDirectories);
        if (candidates.Length != 1)
            throw new InvalidDataException($"完整更新包内无法唯一确定入口 {package.TargetBinaryName}。");
        return new PreparedFullPayload(candidates[0], null);
    }

    private static async Task<PreparedTreePayload> PrepareTreePayloadAsync(
        string stagedRoot,
        string currentExecutablePath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        InstallContext install = ResolveInstallContext(currentExecutablePath);
        string stagedEntry = ResolveSafeRelativePath(stagedRoot, install.EntryRelativePath);
        if (!File.Exists(stagedEntry))
            throw new InvalidDataException($"更新散包缺少产品入口 {install.EntryRelativePath}。");

        string helperName = OperatingSystem.IsWindows() ? "PCL-N-Host.exe" : "PCL-N-Host";
        string entryDirectory = Path.GetDirectoryName(install.EntryRelativePath) ?? string.Empty;
        string stagedHelper = ResolveSafeRelativePath(
            stagedRoot,
            Path.Combine(entryDirectory, "host", helperName));
        if (!File.Exists(stagedHelper))
            stagedHelper = stagedEntry;

        List<LauncherUpdateFileEntry> files = await InventoryTreeAsync(stagedRoot, cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> targetPaths = files.Select(static file => file.Path!)
            .ToHashSet(FilePathComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        List<string> deletePaths = EnumerateManagedFiles(install.Root, install.EntryPath)
            .Where(path => !targetPaths.Contains(path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

        LauncherInstallPlan plan = new()
        {
            FormatVersion = 1,
            InstallRoot = install.Root,
            EntryRelativePath = install.EntryRelativePath,
            StagedRoot = stagedRoot,
            Files = files,
            DeletePaths = deletePaths
        };
        string planPath = Path.Combine(workDirectory, "install-plan.json");
        await using (FileStream stream = new(planPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    plan,
                    LauncherUpdateJsonContext.Default.LauncherInstallPlan,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new PreparedTreePayload(
            stagedRoot,
            stagedEntry,
            stagedHelper,
            install.EntryPath,
            planPath);
    }

    private static void ValidateScatterManifest(
        LauncherScatterPatchManifest manifest,
        LauncherUpdatePatchStep step)
    {
        if (manifest.FormatVersion != 1 ||
            !string.Equals(manifest.Layout, "scatter", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.FromManifestSha256) ||
            string.IsNullOrWhiteSpace(manifest.ToManifestSha256) ||
            manifest.TargetFiles.Count == 0)
        {
            throw new InvalidDataException("散包补丁 files.json 版本或必填字段无效。");
        }

        if (!string.IsNullOrWhiteSpace(step.FromManifestSha256) &&
            !string.Equals(step.FromManifestSha256, manifest.FromManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("散包补丁源清单与发布索引不一致。");
        }
        if (!string.IsNullOrWhiteSpace(step.TargetManifestSha256) &&
            !string.Equals(step.TargetManifestSha256, manifest.ToManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("散包补丁目标清单与发布索引不一致。");
        }
    }

    private static Dictionary<string, LauncherUpdateFileEntry> ValidateFileEntries(
        IEnumerable<LauncherUpdateFileEntry> entries)
    {
        Dictionary<string, LauncherUpdateFileEntry> result = new(StringComparer.Ordinal);
        foreach (LauncherUpdateFileEntry entry in entries)
        {
            string path = NormalizeRelativePath(entry.Path);
            if (!IsSha256(entry.Sha256) || entry.Size < 0 || !result.TryAdd(path, entry))
                throw new InvalidDataException($"散包目标文件条目无效或重复：{entry.Path}。");
            entry.Path = path;
            entry.Sha256 = entry.Sha256!.ToLowerInvariant();
        }
        return result;
    }

    private static Dictionary<string, LauncherScatterPatchOperation> ValidateOperations(
        IEnumerable<LauncherScatterPatchOperation> operations,
        Dictionary<string, LauncherUpdateFileEntry> targetFiles)
    {
        Dictionary<string, LauncherScatterPatchOperation> result = new(StringComparer.Ordinal);
        foreach (LauncherScatterPatchOperation operation in operations)
        {
            string path = NormalizeRelativePath(operation.Path);
            operation.Path = path;
            if (!result.TryAdd(path, operation))
                throw new InvalidDataException($"散包操作路径重复：{path}。");

            bool isDelete = string.Equals(operation.Op, "delete", StringComparison.Ordinal);
            if (isDelete)
            {
                if (targetFiles.ContainsKey(path) || !IsSha256(operation.FromSha256) || operation.FromSize < 0)
                    throw new InvalidDataException($"散包删除操作无效：{path}。");
                continue;
            }

            if (!targetFiles.TryGetValue(path, out LauncherUpdateFileEntry? target) ||
                !IsSha256(operation.ToSha256) || operation.ToSize < 0 ||
                !string.Equals(target.Sha256, operation.ToSha256, StringComparison.OrdinalIgnoreCase) ||
                target.Size != operation.ToSize)
            {
                throw new InvalidDataException($"散包操作目标信息无效：{path}。");
            }

            switch (operation.Op)
            {
                case "hdiff":
                    ValidatePayload(operation.Patch, operation.PatchSha256, operation.PatchSize, path);
                    if (!IsSha256(operation.FromSha256) || operation.FromSize < 0)
                        throw new InvalidDataException($"散包差分源信息无效：{path}。");
                    break;
                case "replace":
                    ValidatePayload(operation.Blob, operation.BlobSha256, operation.BlobSize, path);
                    if (!IsSha256(operation.FromSha256) || operation.FromSize < 0)
                        throw new InvalidDataException($"散包替换源信息无效：{path}。");
                    break;
                case "add":
                    ValidatePayload(operation.Blob, operation.BlobSha256, operation.BlobSize, path);
                    break;
                default:
                    throw new InvalidDataException($"未知散包操作：{operation.Op}。");
            }
        }
        return result;
    }

    private static Dictionary<string, LauncherUpdateFileEntry> BuildSourceFileManifest(
        IReadOnlyDictionary<string, LauncherUpdateFileEntry> targetFiles,
        IReadOnlyDictionary<string, LauncherScatterPatchOperation> operations)
    {
        Dictionary<string, LauncherUpdateFileEntry> source = targetFiles.ToDictionary(
            static pair => pair.Key,
            static pair => new LauncherUpdateFileEntry
            {
                Path = pair.Value.Path,
                Sha256 = pair.Value.Sha256,
                Size = pair.Value.Size
            },
            StringComparer.Ordinal);

        foreach ((string path, LauncherScatterPatchOperation operation) in operations)
        {
            switch (operation.Op)
            {
                case "add":
                    source.Remove(path);
                    break;
                case "hdiff":
                case "replace":
                case "delete":
                    source[path] = new LauncherUpdateFileEntry
                    {
                        Path = path,
                        Sha256 = operation.FromSha256,
                        Size = operation.FromSize
                    };
                    break;
            }
        }
        return source;
    }

    private static void ValidatePayload(string? member, string? sha256, long size, string path)
    {
        _ = NormalizeRelativePath(member);
        if (!IsSha256(sha256) || size < 0)
            throw new InvalidDataException($"散包载荷信息无效：{path}。");
    }

    private static async Task ExtractAndVerifyMemberAsync(
        ZipArchive archive,
        string member,
        string output,
        string expectedSha256,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        string normalized = NormalizeRelativePath(member);
        ZipArchiveEntry entry = archive.GetEntry(normalized)
            ?? throw new InvalidDataException($"散包补丁缺少载荷：{normalized}。");
        if (entry.Length != expectedSize)
            throw new InvalidDataException($"散包载荷大小不一致：{normalized}。");
        await using (Stream source = entry.Open())
        await using (FileStream target = new(output, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        await VerifyFileAsync(output, expectedSha256, "散包载荷校验失败", cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyTreeAsync(
        string root,
        IEnumerable<LauncherUpdateFileEntry> entries,
        string expectedManifestSha256,
        CancellationToken cancellationToken)
    {
        List<LauncherUpdateFileEntry> ordered = entries.OrderBy(static item => item.Path, StringComparer.Ordinal).ToList();
        StringBuilder canonical = new();
        foreach (LauncherUpdateFileEntry entry in ordered)
        {
            string path = ResolveSafeRelativePath(root, entry.Path!);
            await VerifyFileEntryAsync(path, entry, "散包文件校验失败", cancellationToken).ConfigureAwait(false);
            canonical.Append(entry.Path).Append('\t').Append(entry.Sha256!.ToLowerInvariant())
                .Append('\t').Append(entry.Size).Append('\n');
        }
        string actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        if (!string.Equals(actual, expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"散包文件清单校验失败（预期 {expectedManifestSha256}，实际 {actual}）。");
    }

    private static async Task VerifyFileEntryAsync(
        string path,
        LauncherUpdateFileEntry entry,
        string message,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != entry.Size)
            throw new InvalidDataException($"{message}：{entry.Path} 大小不一致。");
        await VerifyFileAsync(path, entry.Sha256!, $"{message}：{entry.Path}", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<List<LauncherUpdateFileEntry>> InventoryTreeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        List<LauncherUpdateFileEntry> files = [];
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(static value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeRelativePath(Path.GetRelativePath(root, path));
            FileInfo info = new(path);
            files.Add(new LauncherUpdateFileEntry
            {
                Path = relative,
                Sha256 = await CalculateSha256Async(path, cancellationToken).ConfigureAwait(false),
                Size = info.Length,
                UnixMode = OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path)
            });
        }
        if (files.Count == 0)
            throw new InvalidDataException("更新包没有任何文件。");
        return files;
    }

    private static async Task ExtractZipTreeAsync(
        string archivePath,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string archiveName = NormalizeArchiveEntryPath(entry.FullName);
            if (archiveName.Length == 0)
                continue;
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(ResolveSafeRelativePath(targetRoot, archiveName));
                continue;
            }
            string output = ResolveSafeRelativePath(targetRoot, archiveName);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using Stream source = entry.Open();
            await using FileStream target = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            RestoreZipUnixMode(output, entry);
        }
    }

    private static async Task ExtractTarTreeAsync(
        string archivePath,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        await using FileStream archive = File.OpenRead(archivePath);
        await using GZipStream gzip = new(archive, CompressionMode.Decompress);
        using TarReader reader = new(gzip, leaveOpen: false);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string archiveName = NormalizeArchiveEntryPath(entry.Name);
            if (entry.EntryType is TarEntryType.ExtendedAttributes or TarEntryType.GlobalExtendedAttributes)
                continue;
            if (archiveName.Length == 0 && entry.EntryType is TarEntryType.Directory)
                continue;
            string output = ResolveSafeRelativePath(targetRoot, archiveName);
            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(output);
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                throw new InvalidDataException($"更新包包含不允许的 TAR 条目：{entry.Name} ({entry.EntryType})。");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using FileStream target = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await entry.DataStream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(output, entry.Mode);
        }
    }

    private static void FlattenSinglePackageRoot(string root)
    {
        string[] files = Directory.GetFiles(root);
        string[] directories = Directory.GetDirectories(root);
        if (files.Length != 0 || directories.Length != 1)
            return;
        string only = directories[0];
        string entryName = OperatingSystem.IsWindows() ? "PCL-N-Edition.exe" : "PCL-N-Edition";
        string macEntry = Path.Combine(only, "Contents", "MacOS", "PCL-N-Edition");
        if (!File.Exists(Path.Combine(only, "pcln-layout")) &&
            !Directory.Exists(Path.Combine(only, "host")) &&
            !File.Exists(Path.Combine(only, entryName)) &&
            !File.Exists(macEntry))
        {
            return;
        }
        foreach (string child in Directory.EnumerateFileSystemEntries(only))
        {
            string destination = Path.Combine(root, Path.GetFileName(child));
            if (Directory.Exists(child))
                Directory.Move(child, destination);
            else
                File.Move(child, destination);
        }
        Directory.Delete(only);
    }

    private static InstallContext ResolveInstallContext(string currentExecutablePath)
    {
        string current = Path.GetFullPath(currentExecutablePath);
        string currentDirectory = Path.GetDirectoryName(current)
            ?? throw new InvalidOperationException("无法确定当前启动器目录。");
        string? environmentRoot = Environment.GetEnvironmentVariable("PCL_LAUNCHER_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            string root = Path.GetFullPath(environmentRoot);
            if (TryResolveMacAppContext(root, out InstallContext? macContext) && macContext is not null)
                return macContext;
            string entry = Path.Combine(root, OperatingSystem.IsWindows() ? "PCL-N-Edition.exe" : "PCL-N-Edition");
            if (File.Exists(Path.Combine(root, "pcln-layout")) && File.Exists(entry))
                return new InstallContext(root, entry, Path.GetFileName(entry));
        }

        DirectoryInfo? directory = new(currentDirectory);
        if (string.Equals(directory.Name, "host", FilePathComparison) && directory.Parent is { } parent)
        {
            if (TryResolveMacAppContext(parent.FullName, out InstallContext? macContext) && macContext is not null)
                return macContext;
            string entry = Path.Combine(parent.FullName, OperatingSystem.IsWindows() ? "PCL-N-Edition.exe" : "PCL-N-Edition");
            if (File.Exists(Path.Combine(parent.FullName, "pcln-layout")) && File.Exists(entry))
                return new InstallContext(parent.FullName, entry, Path.GetFileName(entry));
        }

        return new InstallContext(currentDirectory, current, Path.GetFileName(current));
    }

    private static bool TryResolveMacAppContext(string runtimeDirectory, out InstallContext? context)
    {
        context = null;
        if (!OperatingSystem.IsMacOS())
            return false;
        DirectoryInfo? macOs = new(runtimeDirectory);
        if (string.Equals(macOs.Name, "host", StringComparison.Ordinal))
            macOs = macOs.Parent;
        if (macOs is null || !string.Equals(macOs.Name, "MacOS", StringComparison.Ordinal) ||
            macOs.Parent is not { Name: "Contents", Parent: { } app } ||
            !app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string entryRelative = "Contents/MacOS/PCL-N-Edition";
        string entry = ResolveSafeRelativePath(app.FullName, entryRelative);
        if (!File.Exists(entry) || !File.Exists(Path.Combine(macOs.FullName, "pcln-layout")))
            return false;
        context = new InstallContext(app.FullName, entry, entryRelative);
        return true;
    }

    private static HashSet<string> EnumerateManagedFiles(string installRoot, string currentEntry)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (OperatingSystem.IsMacOS() && Directory.Exists(Path.Combine(installRoot, "Contents")))
        {
            foreach (string path in Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories))
                result.Add(NormalizeRelativePath(Path.GetRelativePath(installRoot, path)));
            return result;
        }
        foreach (string directoryName in new[] { "host", "crash", "native", "sidecar" })
        {
            string directory = Path.Combine(installRoot, directoryName);
            if (!Directory.Exists(directory))
                continue;
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                result.Add(NormalizeRelativePath(Path.GetRelativePath(installRoot, path)));
        }

        foreach (string name in new[]
                 {
                     "PCL-N-Edition.exe", "PCL-N-Edition", "pcln-layout", "payload.zip",
                     "native-runtime.zip", "sidecar.zip"
                 })
        {
            if (File.Exists(Path.Combine(installRoot, name)))
                result.Add(name);
        }
        if (File.Exists(currentEntry))
            result.Add(NormalizeRelativePath(Path.GetRelativePath(installRoot, currentEntry)));
        return result;
    }

    private static string ResolveSafeRelativePath(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(rootPrefix, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootPrefix, FilePathComparison))
            throw new InvalidDataException($"更新路径越界：{relativePath}。");
        return candidate;
    }

    private static string NormalizeRelativePath(string? path)
    {
        string normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(static segment => segment is "" or "." or ".." || segment.Contains(':')))
        {
            throw new InvalidDataException($"更新包含无效相对路径：{path}。");
        }
        return normalized;
    }

    private static string NormalizeArchiveEntryPath(string? path)
    {
        string normalized = (path ?? string.Empty).Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimEnd('/');
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void RestoreZipUnixMode(string path, ZipArchiveEntry entry)
    {
        if (OperatingSystem.IsWindows())
            return;
        int mode = (entry.ExternalAttributes >> 16) & 0x1FF;
        RestoreUnixMode(path, mode == 0 ? null : mode);
    }

    private static void RestoreUnixMode(string path, int? mode)
    {
        if (!OperatingSystem.IsWindows() && mode is > 0)
            File.SetUnixFileMode(path, (UnixFileMode)(mode.Value & 0x1FF));
    }

    private sealed record InstallContext(string Root, string EntryPath, string EntryRelativePath);

    private sealed record PreparedTreePayload(
        string StagedRoot,
        string StagedEntryPath,
        string StagedHelperPath,
        string InstalledEntryPath,
        string InstallPlanPath);

    private sealed record PreparedFullPayload(string EntryPath, PreparedTreePayload? Tree);
}
