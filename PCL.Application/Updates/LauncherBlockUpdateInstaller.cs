// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

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
    private const string BlockCompression = "gzip";
    private const string BlockBasePath = "/v1/updates/block";

    private async Task<PreparedBlockPayload?> TryPrepareBlockPayloadAsync(
        LauncherUpdatePackage package,
        string currentExecutablePath,
        string workDirectory,
        string? blockCacheDirectory,
        CancellationToken cancellationToken)
    {
        string mapPath = Path.Combine(workDirectory, "target.blockmap.json");
        using (HttpResponseMessage response = await GetUpdateResponseAsync(
                   package.BlockMapUrl!,
                   retryNotFound: true,
                   cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                PortableLog.Error(
                    "Update",
                    $"Cloudflare 未提供此构建所需的签名分块清单；URL={package.BlockMapUrl}。");
                return null;
            }
            EnsureUpdateResponseSuccess(response, package.BlockMapUrl!);
            if (response.Content.Headers.ContentLength is > 16 * 1024 * 1024)
                throw new InvalidDataException("分块更新清单异常过大。");
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
                package.BlockMapSignatureUrl,
                required: true,
                cancellationToken)
            .ConfigureAwait(false);

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

        Dictionary<string, LauncherUpdateBlockFile> targetFiles = ValidateBlockMap(map, package);
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

        int downloaded = 0;
        await Parallel.ForEachAsync(
            missingBlocks,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 6 },
            async (block, token) =>
            {
                await DownloadBlockAsync(
                        package.BlockMapUrl!,
                        map.BlockBasePath!,
                        cacheRoot,
                        block,
                        token)
                    .ConfigureAwait(false);
                lock (verifiedCache)
                    verifiedCache.Add(block.Sha256!);
                int completed = Interlocked.Increment(ref downloaded);
                Report(
                    LauncherUpdateStage.DownloadingBlocks,
                    (double)completed / Math.Max(1, missingBlocks.Count),
                    $"正在下载更新分块（{completed}/{missingBlocks.Count}）…");
            }).ConfigureAwait(false);

        string targetRoot = Path.Combine(workDirectory, "tree-blocks");
        if (Directory.Exists(targetRoot))
            Directory.Delete(targetRoot, recursive: true);
        Directory.CreateDirectory(targetRoot);

        int rebuilt = 0;
        foreach (LauncherUpdateBlockFile target in targetFiles.Values.OrderBy(static file => file.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                        await CopyLocalBlockAsync(local, outputStream, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        string cachedPath = GetBlockCachePath(cacheRoot, block.Sha256!);
                        if (!verifiedCache.Contains(block.Sha256!))
                            throw new InvalidDataException($"更新分块没有通过校验：{block.Sha256}。");
                        await using FileStream cached = new(
                            cachedPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            128 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await cached.CopyToAsync(outputStream, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            RestoreUnixMode(output, target.UnixMode);
            await VerifyFileEntryAsync(output, target, "分块重建的文件校验失败", cancellationToken)
                .ConfigureAwait(false);
            rebuilt++;
            Report(
                LauncherUpdateStage.RebuildingFromBlocks,
                (double)rebuilt / targetFiles.Count,
                $"正在重组更新文件（{rebuilt}/{targetFiles.Count}）…");
        }

        await VerifyTreeAsync(targetRoot, targetFiles.Values, map.TargetManifestSha256!, cancellationToken)
            .ConfigureAwait(false);
        PortableLog.Info(
            "Update",
            $"分块更新重建完成；本地完整文件={exactLocalFiles.Count}；本地分块={localBlocks.Count}；" +
            $"缓存分块={verifiedCache.Count - missingBlocks.Count}；下载分块={missingBlocks.Count}。");
        if (string.Equals(map.Layout, SingleFileBlockMapLayout, StringComparison.Ordinal))
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

    private static Dictionary<string, LauncherUpdateBlockFile> ValidateBlockMap(
        LauncherUpdateBlockMap map,
        LauncherUpdatePackage package)
    {
        if (map.FormatVersion != 1 ||
            map.Layout is not (BlockMapLayout or SingleFileBlockMapLayout) ||
            !string.Equals(map.Algorithm, LauncherUpdateChunker.Algorithm, StringComparison.Ordinal) ||
            !string.Equals(map.Compression, BlockCompression, StringComparison.Ordinal) ||
            !string.Equals(map.BlockBasePath, BlockBasePath, StringComparison.Ordinal) ||
            !IsSha256(map.TargetManifestSha256) ||
            map.TargetFiles.Count is 0 or > 32768)
        {
            throw new InvalidDataException("分块更新清单版本或必填字段无效。");
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
                if (!IsSha256(block.Sha256) ||
                    block.Size < 0 || block.Size > LauncherUpdateChunker.MaximumSize ||
                    block.CompressedSize <= 0 ||
                    block.CompressedSize > LauncherUpdateChunker.MaximumSize + 64 * 1024)
                {
                    throw new InvalidDataException($"分块更新块条目无效：{file.Path}。");
                }
                block.Sha256 = block.Sha256!.ToLowerInvariant();
                string expectedPath = $"block/{block.Sha256[..2]}/{block.Sha256}";
                if (!string.Equals(block.Path, expectedPath, StringComparison.Ordinal))
                    throw new InvalidDataException($"分块索引路径不规范：{block.Path}。");
                checked { chunkBytes += block.Size; }
                if (++blockReferences > 1_000_000)
                    throw new InvalidDataException("分块更新清单包含过多块引用。");
            }
            if (chunkBytes != file.Size)
                throw new InvalidDataException($"分块大小总和与文件不一致：{file.Path}。");
        }
        if (string.Equals(map.Layout, SingleFileBlockMapLayout, StringComparison.Ordinal) &&
            (files.Count != 1 || !files.ContainsKey(package.TargetBinaryName)))
        {
            throw new InvalidDataException("单文件分块清单没有唯一的产品入口。");
        }
        return files;
    }

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
        CancellationToken cancellationToken)
    {
        Dictionary<string, LocalBlockSource> result = new(StringComparer.Ordinal);
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
                IReadOnlyList<LauncherUpdateChunkSlice> chunks = await LauncherUpdateChunker.ChunkFileAsync(
                        path,
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

    private async Task DownloadBlockAsync(
        string blockMapUrl,
        string blockBasePath,
        string cacheRoot,
        LauncherUpdateBlock block,
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
            throw new InvalidDataException($"更新分块压缩大小不一致：{block.Sha256}。");

        string destination = GetBlockCachePath(cacheRoot, block.Sha256!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using GZipStream gzip = new(network, CompressionMode.Decompress, leaveOpen: false);
            await using FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                int read = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                checked { written += read; }
                if (written > block.Size)
                    throw new InvalidDataException($"更新分块解压后大小超限：{block.Sha256}。");
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (written != block.Size || !string.Equals(actual, block.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"更新分块 SHA-256 校验失败：{block.Sha256}。");
            output.Close();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
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

    private sealed record LocalBlockSource(string Path, long Offset, int Size);

    private sealed record PreparedBlockPayload(string EntryPath, PreparedTreePayload? Tree);
}
