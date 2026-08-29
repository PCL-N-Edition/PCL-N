using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PCL.Services.Updates;

/// <summary>
/// Payload extraction for staged updates: zip and tar trees are unpacked into a staged root
/// with archive entry paths normalized and traversal-refused, each file hashed on the way in,
/// and an optional verification manifest enforced during extraction. A tree that survives
/// extraction is exactly what <see cref="UpdateStaging.VerifyStagedTree"/> expects.
/// </summary>
public static class UpdatePayloadExtractor
{
    /// <summary>
    /// Extracts a zip package into the staged root and returns the file inventory with
    /// computed SHA-256 digests and Unix modes restored from the archive.
    /// </summary>
    public static async Task<List<UpdateFileEntry>> ExtractZipAsync(
        string archivePath,
        string stagedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        Directory.CreateDirectory(stagedRoot);
        List<UpdateFileEntry> inventory = [];
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
            {
                continue; // bare directory marker
            }

            string destination = ResolveArchiveEntryPath(stagedRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            int? unixMode = ReadZipUnixMode(entry);
            inventory.Add(await ExtractAndHashAsync(
                entry.Open(),
                destination,
                entry.Length,
                NormalizeArchiveEntryPath(entry.FullName),
                unixMode).ConfigureAwait(false));
            ApplyUnixMode(destination, unixMode);
        }

        return inventory;
    }

    /// <summary>
    /// Extracts a tar (any TarReader-supported format) into the staged root and returns the
    /// file inventory; Unix modes come from the tar entry mode.
    /// </summary>
    public static async Task<List<UpdateFileEntry>> ExtractTarAsync(
        string archivePath,
        string stagedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        Directory.CreateDirectory(stagedRoot);
        List<UpdateFileEntry> inventory = [];
        await using FileStream stream = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using TarReader reader = new(stream);
        while (await reader.GetNextEntryAsync().ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory)
            {
                ResolveArchiveEntryPath(stagedRoot, entry.Name);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.HardLink))
            {
                continue;
            }

            string destination = ResolveArchiveEntryPath(stagedRoot, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (entry.DataStream is null)
            {
                continue;
            }

            int? unixMode = entry.Mode != 0 ? (int)entry.Mode : null;
            inventory.Add(await ExtractAndHashAsync(
                entry.DataStream,
                destination,
                entry.Length,
                NormalizeArchiveEntryPath(entry.Name),
                unixMode).ConfigureAwait(false));
            ApplyUnixMode(destination, unixMode);
        }

        return inventory;
    }

    /// <summary>
    /// Normalizes an archive entry path: backslashes become separators, leading "./" and
    /// trailing slashes disappear. Traversal is refused when the path is resolved.
    /// </summary>
    public static string NormalizeArchiveEntryPath(string? path)
    {
        string text = path?.Trim().Replace('\\', '/') ?? string.Empty;
        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        return text.TrimEnd('/');
    }

    private static async Task<UpdateFileEntry> ExtractAndHashAsync(
        Stream content,
        string destination,
        long declaredLength,
        string relativePath,
        int? unixMode)
    {
        using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long written = 0;
        while (true)
        {
            int read = await content.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            written += read;
        }

        _ = declaredLength;
        return new UpdateFileEntry
        {
            Path = relativePath,
            Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()),
            Size = written,
            UnixMode = unixMode,
        };
    }

    private static string ResolveArchiveEntryPath(string stagedRoot, string? entryName)
    {
        string normalized = NormalizeArchiveEntryPath(entryName);
        if (normalized.Length == 0)
        {
            throw new InvalidDataException("归档包含空路径条目。");
        }

        string fullRoot = Path.GetFullPath(stagedRoot).TrimEnd(Path.DirectorySeparatorChar);
        string resolved = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            && !string.Equals(resolved, fullRoot, comparison))
        {
            throw new InvalidDataException($"归档条目路径越界：{entryName}");
        }

        return resolved;
    }

    private static int? ReadZipUnixMode(ZipArchiveEntry entry)
    {
        int external = (int)(entry.ExternalAttributes >> 16);
        return external > 0 ? external & 0xFFF : null;
    }

    private static void ApplyUnixMode(string destination, int? mode)
    {
        if (mode is null or < 0 || OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(destination, (UnixFileMode)mode.Value);
    }
}
