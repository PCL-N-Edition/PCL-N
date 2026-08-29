using System.Text;

namespace PCL.Services.Files;

/// <summary>
/// Canonical folder names inside the application data root. Names are the on-disk contract
/// shared by every service: once a release ships with them, they never change.
/// </summary>
public static class FolderNames
{
    /// <summary>Launcher and game logs (<see cref="PCL.Services.Logging.LogService"/> output lives here too).</summary>
    public const string Logs = "logs";

    /// <summary>Updater-owned state: installed block maps and staged packages.</summary>
    public const string UpdateState = "UpdateState";

    /// <summary>The persisted launch profile roster file.</summary>
    public const string Profiles = "profiles";

    /// <summary>Launcher settings files.</summary>
    public const string Settings = "settings";

    /// <summary>Content-addressed downloads and scratch cache.</summary>
    public const string Cache = "cache";
}

/// <summary>
/// The application data directory tree. Every service path resolves inside this root; the
/// safe port refuses anything that escapes it. The root itself is a composition decision —
/// tests inject a temporary directory, the desktop composition injects the real one.
/// </summary>
public sealed class AppFolders
{
    public AppFolders(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The application data root, fully resolved.</summary>
    public string Root { get; }

    /// <summary>
    /// Default root resolution for the desktop composition: the `PCL_NEXA_DATA_DIR`
    /// environment variable when set, otherwise the per-user application data directory.
    /// </summary>
    public static AppFolders ResolveDefault()
    {
        string fromEnvironment = Environment.GetEnvironmentVariable("PCL_NEXA_DATA_DIR")?.Trim() ?? string.Empty;
        if (fromEnvironment.Length > 0)
        {
            return new AppFolders(fromEnvironment);
        }

        string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppFolders(Path.Combine(baseDirectory, "PCL Nexa"));
    }

    /// <summary>Returns the canonical folder path, creating it on first use.</summary>
    public string EnsureFolder(string folderName)
    {
        string path = ResolveSafePath(folderName, string.Empty);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Resolves a relative path inside one canonical folder without creating anything.
    /// Traversal outside the root is refused.
    /// </summary>
    public string ResolveSafePath(string folderName, string relativePath)
    {
        string folder = folderName.Trim().Trim('/');
        if (folder.Length == 0 || folder.EndsWith('/') || folder.Contains('\\')
            || folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 1)
        {
            throw new InvalidDataException($"文件夹名不受信任：{folderName}");
        }

        string normalized = relativePath.Replace('\\', '/').Trim('/');
        string resolved = Path.GetFullPath(Path.Combine(Root, folder, normalized));
        string rootPrefix = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.StartsWith(rootPrefix, comparison)
            && !string.Equals(resolved, Root.TrimEnd(Path.DirectorySeparatorChar), comparison))
        {
            throw new InvalidDataException($"文件路径越界：{relativePath}");
        }

        return resolved;
    }
}

/// <summary>
/// The safe file port over the application data tree: UTF-8 text and bytes with atomic
/// writes (temporary file + replace), a per-file size cap, traversal refusal, and
/// missing-reads-as-null semantics. Ownership stays with the caller of each folder name.
/// </summary>
public sealed class SafeFilePort
{
    /// <summary>Default per-file cap: 64 MiB is far above any launcher document.</summary>
    public const long DefaultMaxBytes = 64 * 1024 * 1024;

    private const int ReplaceAttemptCount = 3;

    private readonly AppFolders _folders;
    private readonly long _maxBytes;

    public SafeFilePort(AppFolders folders, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _folders = folders;
        _maxBytes = maxBytes;
    }

    /// <summary>Whether one file exists in the tree.</summary>
    public bool Exists(string folderName, string relativePath) =>
        File.Exists(_folders.ResolveSafePath(folderName, relativePath));

    /// <summary>Reads one UTF-8 text file; a missing file reads as null.</summary>
    public async Task<string?> TryReadTextAsync(
        string folderName,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        string path = _folders.ResolveSafePath(folderName, relativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one binary file; a missing file reads as null.</summary>
    public async Task<byte[]?> TryReadBytesAsync(
        string folderName,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        string path = _folders.ResolveSafePath(folderName, relativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one UTF-8 text file atomically: the content lands in a temporary file first and
    /// then replaces the destination, so readers never observe a torn file.
    /// </summary>
    public Task WriteTextAsync(
        string folderName,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default) =>
        WriteBytesAsync(folderName, relativePath, Encoding.UTF8.GetBytes(content), cancellationToken);

    /// <summary>Writes one binary file atomically with the size cap enforced.</summary>
    public async Task WriteBytesAsync(
        string folderName,
        string relativePath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.LongLength > _maxBytes)
        {
            throw new InvalidDataException($"文件超过大小上限 {_maxBytes} 字节：{relativePath}");
        }

        string destination = _folders.ResolveSafePath(folderName, relativePath);
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < ReplaceAttemptCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                TryDelete(temporary);
                throw;
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
    }

    /// <summary>Deletes one file when present; returns whether anything was removed.</summary>
    public bool Delete(string folderName, string relativePath)
    {
        string path = _folders.ResolveSafePath(folderName, relativePath);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort cleanup of a failed write.
        }
    }
}
