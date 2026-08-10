// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text.Json;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

/// <summary>Local raw chunk bytes located inside an installed file.</summary>
internal sealed record LocalBlockSource(string Path, long Offset, int Size);

/// <summary>
/// Protocol v2 LocalBlockIndex: map chunk SHA-256 → installed file path/offset/length
/// without re-running FastCDC when a trusted installed blockmap is available.
/// </summary>
internal static class LauncherUpdateLocalBlockIndex
{
    public const string RelativeDirectory = "UpdateState";
    public const string InstalledMapFileName = "installed.blockmap.json";

    public static string GetInstalledMapPath(string installRoot) =>
        Path.Combine(installRoot, RelativeDirectory, InstalledMapFileName);

    public static async Task SaveInstalledMapAsync(
        string installRoot,
        LauncherUpdateBlockMap map,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(map);

        string path = GetInstalledMapPath(installRoot);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        map,
                        LauncherUpdateJsonContext.Default.LauncherUpdateBlockMap,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
            PortableLog.Info("Update", "已写入 LocalBlockIndex：" + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            PortableLog.Warn("Update", "写入 LocalBlockIndex 失败：" + ex.Message);
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    public static async Task<LauncherUpdateBlockMap?> TryLoadInstalledMapAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        string path = GetInstalledMapPath(installRoot);
        if (!File.Exists(path))
            return null;

        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync(
                    stream,
                    LauncherUpdateJsonContext.Default.LauncherUpdateBlockMap,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            PortableLog.Debug("Update", "读取 LocalBlockIndex 失败，将回退实时分块：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Build a CAS index from a previously installed blockmap when on-disk files still match.
    /// Only chunks listed in <paramref name="neededHashes"/> are retained.
    /// </summary>
    public static async Task<Dictionary<string, LocalBlockSource>> TryIndexFromInstalledMapAsync(
        string installRoot,
        LauncherUpdateBlockMap? installedMap,
        string expectedAlgorithm,
        HashSet<string> neededHashes,
        CancellationToken cancellationToken)
    {
        Dictionary<string, LocalBlockSource> result = new(StringComparer.Ordinal);
        if (installedMap is null ||
            neededHashes.Count == 0 ||
            !string.Equals(installedMap.Algorithm, expectedAlgorithm, StringComparison.Ordinal))
        {
            return result;
        }

        foreach (LauncherUpdateBlockFile file in installedMap.TargetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Path) || file.Chunks.Count == 0)
                continue;

            string absolute;
            try
            {
                absolute = Path.GetFullPath(Path.Combine(installRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!absolute.StartsWith(
                    Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                !string.Equals(
                    Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar),
                    absolute.TrimEnd(Path.DirectorySeparatorChar),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                continue;
            }

            FileInfo info = new(absolute);
            if (!info.Exists || info.Length != file.Size)
                continue;

            if (!string.IsNullOrWhiteSpace(file.Sha256))
            {
                string actual = await CalculateSha256Async(absolute, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            long offset = 0;
            foreach (LauncherUpdateBlock chunk in file.Chunks)
            {
                if (chunk.Size < 0)
                    break;
                if (!string.IsNullOrWhiteSpace(chunk.Sha256) &&
                    neededHashes.Contains(chunk.Sha256) &&
                    !result.ContainsKey(chunk.Sha256))
                {
                    result[chunk.Sha256] = new LocalBlockSource(absolute, offset, checked((int)chunk.Size));
                }

                offset = checked(offset + chunk.Size);
            }
        }

        if (result.Count > 0)
            PortableLog.Info("Update", $"LocalBlockIndex 命中 {result.Count} 个可复用分块。");
        return result;
    }

    /// <summary>
    /// Concatenate raw source chunks (from local index) and verify the window hash.
    /// </summary>
    public static async Task<byte[]?> TryReadSourceWindowAsync(
        IReadOnlyList<string> sourceChunkSha256s,
        string expectedWindowSha256,
        long expectedSize,
        IReadOnlyDictionary<string, LocalBlockSource> localBlocks,
        CancellationToken cancellationToken)
    {
        if (sourceChunkSha256s.Count == 0 || string.IsNullOrWhiteSpace(expectedWindowSha256))
            return null;

        using MemoryStream window = new(capacity: expectedSize > 0 && expectedSize <= int.MaxValue
            ? (int)expectedSize
            : 0);
        foreach (string sha256 in sourceChunkSha256s)
        {
            if (!localBlocks.TryGetValue(sha256, out LocalBlockSource? source))
                return null;

            await using FileStream stream = new(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Seek(source.Offset, SeekOrigin.Begin);
            byte[] buffer = new byte[source.Size];
            int read = await stream.ReadAsync(buffer.AsMemory(0, source.Size), cancellationToken)
                .ConfigureAwait(false);
            if (read != source.Size)
                return null;
            string actual = Convert.ToHexStringLower(SHA256.HashData(buffer));
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                return null;
            await window.WriteAsync(buffer.AsMemory(0, source.Size), cancellationToken).ConfigureAwait(false);
        }

        byte[] bytes = window.ToArray();
        if (expectedSize > 0 && bytes.LongLength != expectedSize)
            return null;
        string windowHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(windowHash, expectedWindowSha256, StringComparison.OrdinalIgnoreCase))
            return null;
        return bytes;
    }

    private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
