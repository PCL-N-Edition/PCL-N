// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

public sealed partial class LauncherUpdateInstaller
{
    private const string BlockMapLayout = "pcln-blockmap-v1";
    private const string SingleFileBlockMapLayout = "pcln-blockmap-file-v1";
    private const string BlockMapLayoutV2 = "pcln-blockmap-v2";
    private const string SingleFileBlockMapLayoutV2 = "pcln-blockmap-file-v2";
    private const string BlockBasePath = "/v1/updates/block";

    private async Task<PreparedBlockPayload?> TryPrepareBlockPayloadAsync(
        LauncherUpdatePackage package,
        string currentExecutablePath,
        string workDirectory,
        string? blockCacheDirectory,
        CancellationToken cancellationToken)
    {
        string mapPath = Path.Combine(workDirectory, "target.blockmap.json");
        if (!await TryDownloadBlockMapAsync(package, mapPath, cancellationToken).ConfigureAwait(false))
            return null;

        LauncherUpdateBlockMap map;
        await using (FileStream stream = new(
                         mapPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            map = await JsonSerializer.DeserializeAsync(
                    stream,
                    LauncherUpdateJsonContext.Default.LauncherUpdateBlockMap,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("无法解析分块更新清单。");
        }

        (Dictionary<string, LauncherUpdateBlockFile> targetFiles, LauncherUpdateChunkProfile profile) =
            ValidateBlockMap(map, package);
        InstallContext install = ResolveInstallContext(currentExecutablePath);
        Dictionary<string, string> exactLocalFiles = await FindExactLocalFilesAsync(
                install,
                targetFiles,
                cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> neededHashes = targetFiles.Values
            .Where(file => !exactLocalFiles.ContainsKey(file.Path!))
            .SelectMany(static file => file.Chunks)
            .Select(static chunk => chunk.Sha256!)
            .ToHashSet(StringComparer.Ordinal);

        Dictionary<string, LocalBlockSource> localBlocks = await IndexLocalBlocksAsync(
                install,
                targetFiles,
                exactLocalFiles,
                neededHashes,
                profile,
                map.Algorithm ?? profile.Algorithm,
                cancellationToken)
            .ConfigureAwait(false);

        string cacheRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(blockCacheDirectory)
            ? Path.Combine(workDirectory, "block-cache")
            : blockCacheDirectory);
        Directory.CreateDirectory(cacheRoot);
        Dictionary<string, LauncherUpdateBlock> uniqueBlocks = targetFiles.Values
            .Where(file => !exactLocalFiles.ContainsKey(file.Path!))
            .SelectMany(static file => file.Chunks)
            .GroupBy(static block => block.Sha256!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        HashSet<string> verifiedCache = new(StringComparer.Ordinal);
        List<LauncherUpdateBlock> missingBlocks = [];
        foreach ((string sha256, LauncherUpdateBlock block) in uniqueBlocks)
        {
            if (localBlocks.ContainsKey(sha256))
                continue;
            string cachedPath = GetBlockCachePath(cacheRoot, sha256);
            if (await IsValidCachedBlockAsync(cachedPath, block, cancellationToken).ConfigureAwait(false))
                verifiedCache.Add(sha256);
            else
                missingBlocks.Add(block);
        }

        int threadLimit = NormalizeDownloadThreadLimit(DownloadThreadLimit);
        int totalBlocks = Math.Max(1, missingBlocks.Count);
        long expectedTransferBytes = missingBlocks.Sum(static block => Math.Max(0, block.CompressedSize));
        BlockTransferProgressTracker transfer = new(
            report: (progress, message, completed, active, speed, bytes) => Report(
                LauncherUpdateStage.DownloadingBlocks,
                progress,
                message,
                completedFiles: completed,
                totalFiles: totalBlocks,
                speedBytesPerSecond: speed,
                activeThreads: active,
                threadLimit: threadLimit,
                bytesReceived: bytes,
                totalBytes: expectedTransferBytes > 0 ? expectedTransferBytes : -1),
            totalItems: totalBlocks,
            threadLimit: threadLimit);

        if (missingBlocks.Count == 0)
        {
            Report(
                LauncherUpdateStage.DownloadingBlocks,
                1d,
                "本地已具备全部更新分块。",
                completedFiles: 0,
                totalFiles: 0,
                speedBytesPerSecond: 0,
                activeThreads: 0,
                threadLimit: threadLimit);
        }
        else
        {
            Report(
                LauncherUpdateStage.DownloadingBlocks,
                0d,
                $"正在下载更新分块（0/{missingBlocks.Count}）…",
                completedFiles: 0,
                totalFiles: totalBlocks,
                speedBytesPerSecond: 0,
                activeThreads: 0,
                threadLimit: threadLimit);

            await Parallel.ForEachAsync(
                missingBlocks,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = threadLimit
                },
                async (block, token) =>
                {
                    transfer.WorkerStarted();
                    try
                    {
                        // Protocol v2: try VCDIFF from local source window first; any failure
                        // falls through to the immutable full gzip block (never fatal alone).
                        long transferred = 0;
                        bool materialised = await TryMaterialiseBlockFromDeltaAsync(
                                package.BlockMapUrl!,
                                cacheRoot,
                                block,
                                localBlocks,
                                bytes =>
                                {
                                    transferred += bytes;
                                    transfer.AddBytes(bytes);
                                },
                                token)
                            .ConfigureAwait(false);
                        if (!materialised)
                        {
                            transferred = 0;
                            await DownloadBlockAsync(
                                    package.BlockMapUrl!,
                                    map.BlockBasePath!,
                                    cacheRoot,
                                    block,
                                    map.Compression,
                                    bytes =>
                                    {
                                        transferred += bytes;
                                        transfer.AddBytes(bytes);
                                    },
                                    token)
                                .ConfigureAwait(false);
                        }

                        // If the codec consumed the stream without per-read callbacks, credit
                        // compressed size once so left-pane speed still moves.
                        if (transferred <= 0 && block.CompressedSize > 0)
                            transfer.AddBytes(block.CompressedSize);

                        lock (verifiedCache)
                            verifiedCache.Add(block.Sha256!);
                        transfer.ItemCompleted();
                    }
                    finally
                    {
                        transfer.WorkerFinished();
                    }
                }).ConfigureAwait(false);

            transfer.FlushComplete();
        }

        string targetRoot = Path.Combine(workDirectory, "tree-blocks");
        if (Directory.Exists(targetRoot))
            Directory.Delete(targetRoot, recursive: true);
        Directory.CreateDirectory(targetRoot);

        List<LauncherUpdateBlockFile> rebuildTargets = targetFiles.Values
            .OrderBy(static file => file.Path, StringComparer.Ordinal)
            .ToList();
        int rebuildThreadLimit = Math.Min(threadLimit, Math.Max(1, Environment.ProcessorCount));
        int rebuilt = 0;
        await Parallel.ForEachAsync(
            rebuildTargets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = rebuildThreadLimit
            },
            async (target, token) =>
            {
                string output = ResolveSafeRelativePath(targetRoot, target.Path!);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                if (exactLocalFiles.TryGetValue(target.Path!, out string? exactSource))
                {
                    File.Copy(exactSource, output, overwrite: false);
                }
                else
                {
                    await using FileStream outputStream = new(
                        output,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    foreach (LauncherUpdateBlock block in target.Chunks)
                    {
                        if (localBlocks.TryGetValue(block.Sha256!, out LocalBlockSource? local))
                        {
                            await CopyLocalBlockAsync(local, outputStream, token).ConfigureAwait(false);
                        }
                        else
                        {
                            string cachedPath = GetBlockCachePath(cacheRoot, block.Sha256!);
                            bool hasCache;
                            lock (verifiedCache)
                                hasCache = verifiedCache.Contains(block.Sha256!);
                            if (!hasCache)
                                throw new InvalidDataException($"更新分块没有通过校验：{block.Sha256}。");
                            await using FileStream cached = new(
                                cachedPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                128 * 1024,
                                FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await cached.CopyToAsync(outputStream, token).ConfigureAwait(false);
                        }
                    }
                }

                RestoreUnixMode(output, target.UnixMode);
                await VerifyFileEntryAsync(output, target, "分块重建的文件校验失败", token)
                    .ConfigureAwait(false);
                int completed = Interlocked.Increment(ref rebuilt);
                Report(
                    LauncherUpdateStage.RebuildingFromBlocks,
                    (double)completed / rebuildTargets.Count,
                    $"正在重组更新文件（{completed}/{rebuildTargets.Count}）…",
                    completedFiles: completed,
                    totalFiles: rebuildTargets.Count,
                    speedBytesPerSecond: 0,
                    activeThreads: Math.Min(rebuildThreadLimit, rebuildTargets.Count - completed + 1),
                    threadLimit: rebuildThreadLimit);
            }).ConfigureAwait(false);

        await VerifyTreeAsync(targetRoot, targetFiles.Values, map.TargetManifestSha256!, cancellationToken)
            .ConfigureAwait(false);
        // Persist the applied map so the next update can resolve source chunks by offset
        // without re-running FastCDC (protocol v2 LocalBlockIndex).
        await LauncherUpdateLocalBlockIndex.SaveInstalledMapAsync(install.Root, map, cancellationToken)
            .ConfigureAwait(false);
        PortableLog.Info(
            "Update",
            $"分块更新重建完成；本地完整文件={exactLocalFiles.Count}；本地分块={localBlocks.Count}；" +
            $"缓存分块={verifiedCache.Count - missingBlocks.Count}；下载分块={missingBlocks.Count}。");
        if (IsSingleFileBlockMapLayout(map.Layout))
        {
            LauncherUpdateBlockFile only = targetFiles.Values.Single();
            string entry = ResolveSafeRelativePath(targetRoot, only.Path!);
            return new PreparedBlockPayload(entry, null);
        }

        PreparedTreePayload tree = await PrepareTreePayloadAsync(
                targetRoot,
                currentExecutablePath,
                workDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        return new PreparedBlockPayload(tree.StagedEntryPath, tree);
    }

    /// <summary>
    /// Prefer primary blockmap URL (v2), then optional v1 fallback when the primary is 404.
    /// </summary>
    private async Task<bool> TryDownloadBlockMapAsync(
        LauncherUpdatePackage package,
        string mapPath,
        CancellationToken cancellationToken)
    {
        (string? url, string? signatureUrl)[] candidates =
        [
            (package.BlockMapUrl, package.BlockMapSignatureUrl),
            (package.BlockMapFallbackUrl, package.BlockMapFallbackSignatureUrl)
        ];

        foreach ((string? url, string? signatureUrl) in candidates)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(signatureUrl))
                continue;

            using HttpResponseMessage response = await GetUpdateResponseAsync(
                    url,
                    retryNotFound: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                PortableLog.Warn("Update", $"分块清单不存在，尝试下一候选：{url}");
                continue;
            }

            EnsureUpdateResponseSuccess(response, url);
            if (response.Content.Headers.ContentLength is > 16 * 1024 * 1024)
                throw new InvalidDataException("分块更新清单异常过大。");

            if (File.Exists(mapPath))
                File.Delete(mapPath);

            // Close the write handle before GPG/signature verification reopens the path.
            {
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using FileStream target = new(
                    mapPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            await VerifyDetachedSignatureAsync(
                    mapPath,
                    signatureUrl,
                    required: true,
                    cancellationToken)
                .ConfigureAwait(false);
            PortableLog.Info("Update", $"已加载分块清单：{url}");
            return true;
        }

        PortableLog.Error(
            "Update",
            $"Cloudflare 未提供此构建所需的签名分块清单；URL={package.BlockMapUrl}。");
        return false;
    }

    private static (Dictionary<string, LauncherUpdateBlockFile> Files, LauncherUpdateChunkProfile Profile)
        ValidateBlockMap(LauncherUpdateBlockMap map, LauncherUpdatePackage package)
    {
        if (!TryResolveBlockMapProfile(map, out LauncherUpdateChunkProfile profile) ||
            !LauncherUpdateBlockCodec.IsSupported(map.Compression) ||
            !string.Equals(map.BlockBasePath, BlockBasePath, StringComparison.Ordinal) ||
            !IsSha256(map.TargetManifestSha256) ||
            map.TargetFiles.Count is 0 or > 32768)
        {
            throw new InvalidDataException("分块更新清单版本或必填字段无效。");
        }

        map.Compression = LauncherUpdateBlockCodec.Normalize(map.Compression);

        if (map.Chunking is { } chunking &&
            (chunking.Min != profile.MinimumSize ||
             chunking.Avg != profile.AverageSize ||
             chunking.Max != profile.MaximumSize))
        {
            throw new InvalidDataException("分块更新清单 chunking 参数与 algorithm 不一致。");
        }

        if (!string.Equals(map.TargetTag, package.TargetTag, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(map.TargetVersion, package.TargetVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(map.RuntimeId, package.RuntimeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                LauncherBuildIdentity.NormalizeRuntimeVariant(map.RuntimeVariant),
                package.RuntimeVariant,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                LauncherBuildIdentity.NormalizeConfiguration(map.Configuration),
                package.Configuration,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(map.TargetAssetName, package.TargetAssetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("分块更新清单与目标发布不匹配。");
        }

        Dictionary<string, LauncherUpdateBlockFile> files = new(StringComparer.OrdinalIgnoreCase);
        long blockReferences = 0;
        long maxCompressed = profile.MaximumSize + 64L * 1024;
        foreach (LauncherUpdateBlockFile file in map.TargetFiles)
        {
            string path = NormalizeRelativePath(file.Path);
            if (!files.TryAdd(path, file) || !IsSha256(file.Sha256) || file.Size < 0 || file.Chunks.Count == 0)
                throw new InvalidDataException($"分块更新文件条目无效或重复：{file.Path}。");
            file.Path = path;
            file.Sha256 = file.Sha256!.ToLowerInvariant();
            long chunkBytes = 0;
            foreach (LauncherUpdateBlock block in file.Chunks)
            {
                long compressedSize = block.ResolveCompressedSize();
                string? fullPath = block.ResolveFullPath();
                if (!IsSha256(block.Sha256) ||
                    block.Size < 0 || block.Size > profile.MaximumSize ||
                    compressedSize <= 0 ||
                    compressedSize > maxCompressed ||
                    string.IsNullOrWhiteSpace(fullPath))
                {
                    throw new InvalidDataException($"分块更新块条目无效：{file.Path}。");
                }
                block.Sha256 = block.Sha256!.ToLowerInvariant();
                // Normalize nested full representation onto flat fields for download path.
                block.CompressedSize = compressedSize;
                block.Path = fullPath;
                if (block.Full is not null)
                {
                    block.Full.Compression = LauncherUpdateBlockCodec.Normalize(
                        block.ResolveCompression(map.Compression));
                }
                string expectedPath = $"block/{block.Sha256[..2]}/{block.Sha256}";
                if (!string.Equals(block.Path, expectedPath, StringComparison.Ordinal))
                    throw new InvalidDataException($"分块索引路径不规范：{block.Path}。");
                if (block.Deltas is { Count: > 0 })
                {
                    if (block.Deltas.Count > 2)
                        throw new InvalidDataException($"分块 delta 数量超过限制：{file.Path}。");
                    foreach (LauncherUpdateBlockDelta delta in block.Deltas)
                    {
                        if (!string.Equals(delta.Algorithm, LauncherUpdateVcdiff.Algorithm, StringComparison.Ordinal) ||
                            string.IsNullOrWhiteSpace(delta.Path) ||
                            delta.Size <= 0 ||
                            delta.SourceChunks.Count is 0 or > 8 ||
                            !IsSha256(delta.SourceSha256) ||
                            delta.SourceChunks.Any(static sha => !IsSha256(sha)))
                        {
                            throw new InvalidDataException($"分块 delta 条目无效：{file.Path}。");
                        }

                        delta.SourceSha256 = delta.SourceSha256!.ToLowerInvariant();
                        for (int i = 0; i < delta.SourceChunks.Count; i++)
                            delta.SourceChunks[i] = delta.SourceChunks[i].ToLowerInvariant();
                    }
                }
                checked { chunkBytes += block.Size; }
                if (++blockReferences > 1_000_000)
                    throw new InvalidDataException("分块更新清单包含过多块引用。");
            }
            if (chunkBytes != file.Size)
                throw new InvalidDataException($"分块大小总和与文件不一致：{file.Path}。");
        }
        if (IsSingleFileBlockMapLayout(map.Layout) &&
            (files.Count != 1 || !files.ContainsKey(package.TargetBinaryName)))
        {
            throw new InvalidDataException("单文件分块清单没有唯一的产品入口。");
        }
        return (files, profile);
    }

    private static bool TryResolveBlockMapProfile(
        LauncherUpdateBlockMap map,
        out LauncherUpdateChunkProfile profile)
    {
        profile = LauncherUpdateChunkProfile.V1;
        if (!LauncherUpdateChunkProfile.TryGet(map.Algorithm, out profile))
            return false;

        if (map.FormatVersion == 1 &&
            map.Layout is BlockMapLayout or SingleFileBlockMapLayout &&
            string.Equals(map.Algorithm, LauncherUpdateChunkProfile.V1.Algorithm, StringComparison.Ordinal))
        {
            return true;
        }

        if (map.FormatVersion == 2 &&
            map.Layout is BlockMapLayoutV2 or SingleFileBlockMapLayoutV2 &&
            string.Equals(map.Algorithm, LauncherUpdateChunkProfile.V2.Algorithm, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsSingleFileBlockMapLayout(string? layout) =>
        string.Equals(layout, SingleFileBlockMapLayout, StringComparison.Ordinal) ||
        string.Equals(layout, SingleFileBlockMapLayoutV2, StringComparison.Ordinal);

    private static async Task<Dictionary<string, string>> FindExactLocalFilesAsync(
        InstallContext install,
        IReadOnlyDictionary<string, LauncherUpdateBlockFile> targetFiles,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> exact = new(StringComparer.OrdinalIgnoreCase);
        int inspected = 0;
        foreach ((string relative, LauncherUpdateBlockFile target) in targetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = ResolveSafeRelativePath(install.Root, relative);
            FileInfo info = new(path);
            if (info.Exists && info.Length == target.Size &&
                string.Equals(
                    await CalculateSha256Async(path, cancellationToken).ConfigureAwait(false),
                    target.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                exact[relative] = path;
            }
            inspected++;
        }
        PortableLog.Debug("Update", $"已校验本地目标文件 {inspected} 个；可直接复用 {exact.Count} 个。");
        return exact;
    }

    private async Task<Dictionary<string, LocalBlockSource>> IndexLocalBlocksAsync(
        InstallContext install,
        IReadOnlyDictionary<string, LauncherUpdateBlockFile> targetFiles,
        Dictionary<string, string> exactLocalFiles,
        HashSet<string> neededHashes,
        LauncherUpdateChunkProfile profile,
        string mapAlgorithm,
        CancellationToken cancellationToken)
    {
        Dictionary<string, LocalBlockSource> result = new(StringComparer.Ordinal);
        if (neededHashes.Count == 0)
            return result;

        // Fast path: installed.blockmap.json → path/offset without re-chunking.
        LauncherUpdateBlockMap? installed = await LauncherUpdateLocalBlockIndex
            .TryLoadInstalledMapAsync(install.Root, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, LocalBlockSource> fromInstalled =
            await LauncherUpdateLocalBlockIndex.TryIndexFromInstalledMapAsync(
                    install.Root,
                    installed,
                    mapAlgorithm,
                    neededHashes,
                    cancellationToken)
                .ConfigureAwait(false);
        foreach ((string sha, LocalBlockSource source) in fromInstalled)
            result[sha] = source;
        if (result.Count >= neededHashes.Count)
            return result;

        List<string> candidates = targetFiles.Keys
            .Where(path => !exactLocalFiles.ContainsKey(path))
            .Select(path => ResolveSafeRelativePath(install.Root, path))
            .Concat(EnumerateManagedFiles(install.Root, install.EntryPath)
                .Select(path => ResolveSafeRelativePath(install.Root, path)))
            .Where(File.Exists)
            .Distinct(FilePathComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToList();

        for (int index = 0; index < candidates.Count && result.Count < neededHashes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = candidates[index];
            try
            {
                // Must match the blockmap algorithm; v2 boundaries differ from v1.
                IReadOnlyList<LauncherUpdateChunkSlice> chunks = await LauncherUpdateChunker.ChunkFileAsync(
                        path,
                        profile,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (LauncherUpdateChunkSlice chunk in chunks)
                {
                    if (neededHashes.Contains(chunk.Sha256))
                        result.TryAdd(chunk.Sha256, new LocalBlockSource(path, chunk.Offset, chunk.Size));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PortableLog.Debug("Update", $"跳过无法建立分块索引的本地文件：{path}；{ex.Message}");
            }
            Report(
                LauncherUpdateStage.IndexingLocalBlocks,
                (double)(index + 1) / Math.Max(1, candidates.Count),
                $"正在查找可复用的本地分块（{index + 1}/{candidates.Count}）…");
        }
        return result;
    }

    private static string GetBlockCachePath(string cacheRoot, string sha256) =>
        Path.Combine(cacheRoot, sha256[..2], sha256);

    private static async Task<bool> IsValidCachedBlockAsync(
        string path,
        LauncherUpdateBlock block,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists)
            return false;
        if (info.Length == block.Size && string.Equals(
                await CalculateSha256Async(path, cancellationToken).ConfigureAwait(false),
                block.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        try { File.Delete(path); } catch (IOException) { }
        return false;
    }

    private async Task<bool> TryMaterialiseBlockFromDeltaAsync(
        string blockMapUrl,
        string cacheRoot,
        LauncherUpdateBlock block,
        IReadOnlyDictionary<string, LocalBlockSource> localBlocks,
        Action<long>? onBytesRead,
        CancellationToken cancellationToken)
    {
        if (block.Deltas is not { Count: > 0 } || string.IsNullOrWhiteSpace(block.Sha256))
            return false;

        foreach (LauncherUpdateBlockDelta delta in block.Deltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(delta.Algorithm, LauncherUpdateVcdiff.Algorithm, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(delta.Path) ||
                string.IsNullOrWhiteSpace(delta.SourceSha256))
            {
                continue;
            }

            try
            {
                byte[]? sourceWindow = await LauncherUpdateLocalBlockIndex.TryReadSourceWindowAsync(
                        delta.SourceChunks,
                        delta.SourceSha256,
                        delta.SourceSize,
                        localBlocks,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (sourceWindow is null)
                    continue;

                byte[] deltaBytes = await DownloadDeltaBytesAsync(
                        blockMapUrl,
                        delta,
                        onBytesRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!LauncherUpdateVcdiff.TryDecode(deltaBytes, sourceWindow, out byte[] target) ||
                    target.LongLength != block.Size)
                {
                    PortableLog.Debug("Update", $"VCDIFF 解码失败，回退 full block：{block.Sha256}");
                    continue;
                }

                string actual = Convert.ToHexStringLower(SHA256.HashData(target));
                if (!string.Equals(actual, block.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    PortableLog.Debug("Update", $"VCDIFF 目标校验失败，回退 full block：{block.Sha256}");
                    continue;
                }

                string destination = GetBlockCachePath(cacheRoot, block.Sha256);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                await File.WriteAllBytesAsync(temporary, target, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
                PortableLog.Info("Update", $"已用 VCDIFF 重建分块：{block.Sha256}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or HttpRequestException or NotSupportedException)
            {
                PortableLog.Debug("Update", $"VCDIFF 路径异常，回退 full block：{block.Sha256}；{ex.Message}");
            }
        }

        return false;
    }

    private async Task<byte[]> DownloadDeltaBytesAsync(
        string blockMapUrl,
        LauncherUpdateBlockDelta delta,
        Action<long>? onBytesRead,
        CancellationToken cancellationToken)
    {
        Uri mapUri = new(blockMapUrl, UriKind.Absolute);
        string relative = delta.Path!.Replace('\\', '/').TrimStart('/');
        UriBuilder builder = new(mapUri.Scheme, mapUri.Host, mapUri.Port)
        {
            Path = "/v1/updates/" + relative
        };
        string deltaUrl = builder.Uri.AbsoluteUri;
        using HttpResponseMessage response = await GetUpdateResponseAsync(
                deltaUrl,
                retryNotFound: true,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureUpdateResponseSuccess(response, deltaUrl);
        if (response.Content.Headers.ContentLength is long length && length != delta.Size)
        {
            // Content-Length describes the HTTP representation selected by the
            // gateway. A CDN/proxy may transparently recode that representation,
            // so it is not an integrity boundary. The bytes read below are still
            // checked against the signed map before they are used.
            PortableLog.Debug(
                "Update",
                $"VCDIFF 响应长度与索引不同；将以实际内容校验为准：{delta.Path}；响应={length}；索引={delta.Size}。");
        }
        await using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using CountingReadStream counted = new(network, onBytesRead);
        using MemoryStream buffer = new(capacity: (int)Math.Min(delta.Size, int.MaxValue));
        await counted.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        byte[] bytes = buffer.ToArray();
        if (bytes.LongLength != delta.Size)
            throw new InvalidDataException($"VCDIFF 实际大小不一致：{delta.Path}");
        return bytes;
    }

    private async Task DownloadBlockAsync(
        string blockMapUrl,
        string blockBasePath,
        string cacheRoot,
        LauncherUpdateBlock block,
        string? mapCompression,
        Action<long>? onBytesRead,
        CancellationToken cancellationToken)
    {
        Uri mapUri = new(blockMapUrl, UriKind.Absolute);
        UriBuilder builder = new(mapUri.Scheme, mapUri.Host, mapUri.Port)
        {
            Path = $"{blockBasePath.TrimEnd('/')}/{block.Sha256![..2]}/{block.Sha256}"
        };
        string blockUrl = builder.Uri.AbsoluteUri;
        using HttpResponseMessage response = await GetUpdateResponseAsync(
                blockUrl,
                retryNotFound: true,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureUpdateResponseSuccess(response, blockUrl);
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != block.CompressedSize)
        {
            // Do not reject a valid block solely because an intermediary changed
            // its transfer representation. DecompressAndVerifyAsync validates the
            // signed uncompressed size and SHA-256 before the cache entry is moved
            // into place.
            PortableLog.Debug(
                "Update",
                $"更新分块响应长度与索引不同；将以解压后 SHA-256 校验为准：{block.Sha256}；响应={contentLength}；索引={block.CompressedSize}。");
        }

        string destination = GetBlockCachePath(cacheRoot, block.Sha256!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        string codec = block.ResolveCompression(mapCompression) ?? LauncherUpdateBlockCodec.Gzip;
        try
        {
            await using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using CountingReadStream counted = new(network, onBytesRead);
            await LauncherUpdateBlockCodec.DecompressAndVerifyAsync(
                    counted,
                    codec,
                    block.Sha256!,
                    block.Size,
                    temporary,
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    /// <summary>Aggregates concurrent block download metrics for task-manager left pane.</summary>
    private sealed class BlockTransferProgressTracker
    {
        private readonly object _sync = new();
        private readonly Action<double, string, int, int, long, long> _report;
        private readonly int _totalItems;
        private readonly int _threadLimit;
        private int _completed;
        private int _active;
        private long _bytesReceived;
        private long _speedWindowBytes;
        private long _speedWindowStart = Stopwatch.GetTimestamp();
        private long _speedBytesPerSecond;
        private long _lastReportTicks;
        private string _lastMessage = string.Empty;

        public BlockTransferProgressTracker(
            Action<double, string, int, int, long, long> report,
            int totalItems,
            int threadLimit)
        {
            _report = report;
            _totalItems = Math.Max(1, totalItems);
            _threadLimit = Math.Max(1, threadLimit);
        }

        public void WorkerStarted()
        {
            lock (_sync)
            {
                _active++;
                EmitLocked(force: true);
            }
        }

        public void WorkerFinished()
        {
            lock (_sync)
            {
                _active = Math.Max(0, _active - 1);
                EmitLocked(force: true);
            }
        }

        public void AddBytes(long bytes)
        {
            if (bytes <= 0)
                return;
            lock (_sync)
            {
                _bytesReceived += bytes;
                _speedWindowBytes += bytes;
                long now = Stopwatch.GetTimestamp();
                double windowSeconds = Stopwatch.GetElapsedTime(_speedWindowStart, now).TotalSeconds;
                if (windowSeconds >= 0.35d)
                {
                    _speedBytesPerSecond = (long)(_speedWindowBytes / Math.Max(windowSeconds, 0.001d));
                    _speedWindowBytes = 0;
                    _speedWindowStart = now;
                }

                EmitLocked(force: false);
            }
        }

        public void ItemCompleted()
        {
            lock (_sync)
            {
                _completed = Math.Min(_totalItems, _completed + 1);
                EmitLocked(force: true);
            }
        }

        public void FlushComplete()
        {
            lock (_sync)
            {
                _completed = _totalItems;
                _active = 0;
                _speedBytesPerSecond = 0;
                EmitLocked(force: true);
            }
        }

        private void EmitLocked(bool force)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force && _lastReportTicks != 0 &&
                Stopwatch.GetElapsedTime(_lastReportTicks, now).TotalMilliseconds < 80)
            {
                return;
            }

            _lastReportTicks = now;
            string message = $"正在下载更新分块（{_completed}/{_totalItems}）…";
            if (!force &&
                string.Equals(message, _lastMessage, StringComparison.Ordinal) &&
                _active == 0)
            {
                return;
            }

            _lastMessage = message;
            double progress = (double)_completed / _totalItems;
            _report(progress, message, _completed, _active, _speedBytesPerSecond, _bytesReceived);
        }
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long>? _onBytesRead;

        public CountingReadStream(Stream inner, Action<long>? onBytesRead)
        {
            _inner = inner;
            _onBytesRead = onBytesRead;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (read > 0)
                _onBytesRead?.Invoke(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            if (read > 0)
                _onBytesRead?.Invoke(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
                _onBytesRead?.Invoke(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CopyLocalBlockAsync(
        LocalBlockSource source,
        Stream output,
        CancellationToken cancellationToken)
    {
        await using FileStream input = new(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        input.Position = source.Offset;
        byte[] buffer = new byte[128 * 1024];
        int remaining = source.Size;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException($"本地分块源文件读取不足：{source.Path}。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }



    private sealed record PreparedBlockPayload(string EntryPath, PreparedTreePayload? Tree);
}
