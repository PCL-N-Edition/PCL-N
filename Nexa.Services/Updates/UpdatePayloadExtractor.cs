using System.Formats.Tar;
using System.IO.Compression;
using Nexa.Services.Files;

namespace Nexa.Services.Updates;

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
        string stagedRoot,
        UpdateArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        limits ??= new();
        limits.Validate(archivePath);
        var budget = new ArchiveReadBudget(limits.MaximumExpandedBytes);
        Directory.CreateDirectory(stagedRoot);
        List<UpdateFileEntry> inventory = [];
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > limits.MaximumEntries) throw new InvalidDataException("更新归档条目过多。");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000)) throw new InvalidDataException("更新归档不允许链接或特殊文件。");
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
            {
                if (!IsRootDirectoryMarker(entry.FullName)) ResolveArchiveEntryPath(stagedRoot, entry.FullName);
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
                unixMode, limits.MaximumFileBytes, budget, cancellationToken).ConfigureAwait(false));
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
        string stagedRoot,
        UpdateArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        limits ??= new();
        limits.Validate(archivePath);
        var budget = new ArchiveReadBudget(limits.MaximumExpandedBytes);
        int count = 0;
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
        while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (++count > limits.MaximumEntries) throw new InvalidDataException("更新归档条目过多。");
            if (entry.EntryType is TarEntryType.Directory)
            {
                if (!IsRootDirectoryMarker(entry.Name)) ResolveArchiveEntryPath(stagedRoot, entry.Name);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                throw new InvalidDataException("更新归档不允许链接或特殊文件。");
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
                unixMode, limits.MaximumFileBytes, budget, cancellationToken).ConfigureAwait(false));
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

    private static bool IsRootDirectoryMarker(string path) =>
        path is "." or "./" || (path.StartsWith("./", StringComparison.Ordinal) && NormalizeArchiveEntryPath(path).Length == 0);

    private static async Task<UpdateFileEntry> ExtractAndHashAsync(
        Stream content,
        string destination,
        long declaredLength,
        string relativePath,
        int? unixMode,
        long maximumFileBytes,
        ArchiveReadBudget budget,
        CancellationToken cancellationToken)
    {
        string digest = await UpdateArchiveEntry.CopyAndHashAsync(content, destination, declaredLength,
            maximumFileBytes, budget, cancellationToken).ConfigureAwait(false);
        return new UpdateFileEntry
        {
            Path = relativePath,
            Sha256 = digest,
            Size = declaredLength,
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
