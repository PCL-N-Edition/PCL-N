using System.Security.Cryptography;
using System.Text.Json;

namespace PCL.Services.Updates;

/// <summary>Local raw chunk bytes located inside an installed file.</summary>
public sealed record LocalBlockSource(string Path, long Offset, int Size);

/// <summary>
/// Protocol v2 LocalBlockIndex: maps chunk SHA-256 → installed file path/offset/length without
/// re-running content-defined chunking when a trusted installed blockmap is available. Files
/// are only reused after their size (and full-file SHA-256 when the map declares one)
/// verifies, and only inside the installation root.
/// </summary>
public static class UpdateLocalBlockIndex
{
    public const string RelativeDirectory = "UpdateState";
    public const string InstalledMapFileName = "installed.blockmap.json";

    public static string GetInstalledMapPath(string installRoot) =>
        System.IO.Path.Combine(installRoot, RelativeDirectory, InstalledMapFileName);

    /// <summary>
    /// Persists the installed blockmap atomically. Storage failures are swallowed: the index
    /// is an optimization and the next update falls back to live chunking.
    /// </summary>
    public static void SaveInstalledMap(string installRoot, UpdateBlockMap map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(map);

        string path = GetInstalledMapPath(installRoot);
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, map, UpdateJsonContext.Default.UpdateBlockMap);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
                // Best effort; the next update falls back to live chunking.
            }
        }
    }

    public static UpdateBlockMap? TryLoadInstalledMap(string installRoot)
    {
        string path = GetInstalledMapPath(installRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            return JsonSerializer.Deserialize(stream, UpdateJsonContext.Default.UpdateBlockMap);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a content-addressed index from a previously installed blockmap when the on-disk
    /// files still match. Only chunks listed in <paramref name="neededHashes"/> are retained.
    /// </summary>
    public static Dictionary<string, LocalBlockSource> TryIndexFromInstalledMap(
        string installRoot,
        UpdateBlockMap? installedMap,
        string expectedAlgorithm,
        HashSet<string> neededHashes)
    {
        Dictionary<string, LocalBlockSource> result = new(StringComparer.Ordinal);
        if (installedMap is null ||
            neededHashes.Count == 0 ||
            !string.Equals(installedMap.Algorithm, expectedAlgorithm, StringComparison.Ordinal))
        {
            return result;
        }

        foreach (UpdateBlockFile file in installedMap.TargetFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || file.Chunks.Count == 0)
            {
                continue;
            }

            string absolute;
            try
            {
                absolute = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(installRoot, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            string fullRoot = System.IO.Path.GetFullPath(installRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool insideRoot = absolute.StartsWith(fullRoot + System.IO.Path.DirectorySeparatorChar, comparison)
                || string.Equals(fullRoot, absolute.TrimEnd(System.IO.Path.DirectorySeparatorChar), comparison);
            if (!insideRoot)
            {
                continue;
            }

            FileInfo info = new(absolute);
            if (!info.Exists || info.Length != file.Size)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(file.Sha256))
            {
                string actual = CalculateSha256(absolute);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            long offset = 0;
            foreach (UpdateBlock chunk in file.Chunks)
            {
                if (chunk.Size < 0)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(chunk.Sha256) &&
                    neededHashes.Contains(chunk.Sha256) &&
                    !result.ContainsKey(chunk.Sha256))
                {
                    result[chunk.Sha256] = new LocalBlockSource(absolute, offset, checked((int)chunk.Size));
                }

                offset = checked(offset + chunk.Size);
            }
        }

        return result;
    }

    /// <summary>
    /// Concatenates raw source chunks (from the local index) and verifies every chunk hash
    /// plus the window hash. Returns null on any mismatch so callers fall back to downloads.
    /// </summary>
    public static byte[]? TryReadSourceWindow(
        IReadOnlyList<string> sourceChunkSha256s,
        string expectedWindowSha256,
        long expectedSize,
        IReadOnlyDictionary<string, LocalBlockSource> localBlocks)
    {
        if (sourceChunkSha256s.Count == 0 || string.IsNullOrWhiteSpace(expectedWindowSha256))
        {
            return null;
        }

        using MemoryStream window = new(capacity: expectedSize > 0 && expectedSize <= int.MaxValue ? (int)expectedSize : 0);
        foreach (string sha256 in sourceChunkSha256s)
        {
            if (!localBlocks.TryGetValue(sha256, out LocalBlockSource? source))
            {
                return null;
            }

            using FileStream stream = new(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            stream.Seek(source.Offset, SeekOrigin.Begin);
            byte[] buffer = new byte[source.Size];
            int read = stream.ReadAtLeast(buffer.AsSpan(0, source.Size), source.Size, throwOnEndOfStream: false);
            if (read != source.Size)
            {
                return null;
            }

            string actual = Convert.ToHexStringLower(SHA256.HashData(buffer));
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            window.Write(buffer, 0, source.Size);
        }

        byte[] bytes = window.ToArray();
        if (expectedSize > 0 && bytes.LongLength != expectedSize)
        {
            return null;
        }

        string windowHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(windowHash, expectedWindowSha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return bytes;
    }

    private static string CalculateSha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
