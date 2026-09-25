using System.Security.Cryptography;

namespace Nexa.Services.Updates;

/// <summary>
/// The staged-install core of the update flow: verify a downloaded tree against the target
/// manifest, flatten single-package wrapper roots, build the install plan (target files plus
/// the managed leftovers to delete), and apply that plan — every destination resolved inside
/// its root, every staged file re-verified before it lands, files replaced atomically, and
/// Unix modes restored. Failures throw <see cref="InvalidDataException"/> for verification
/// problems; nothing is placed unless the staged tree matched the manifest.
/// </summary>
public static class UpdateStaging
{
    /// <summary>
    /// Verifies that the staged tree contains exactly the target manifest entries with
    /// matching sizes and SHA-256 digests. Throws <see cref="InvalidDataException"/> on the
    /// first mismatch.
    /// </summary>
    public static void VerifyStagedTree(string stagedRoot, IReadOnlyList<UpdateFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (UpdateFileEntry file in files)
        {
            string path = ResolveSafeRelativePath(stagedRoot, file.Path);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"暂存更新缺少文件：{file.Path}");
            }

            VerifyFileEntry(path, file);
        }
    }

    /// <summary>
    /// Collapses a single-package wrapper root: while the staged root contains exactly one
    /// directory and no files, that directory's contents move up one level. Zip/tar packages
    /// often nest everything under one folder that must not survive installation.
    /// </summary>
    public static void FlattenSingleRoot(string stagedRoot)
    {
        while (true)
        {
            string[] directories = Directory.GetDirectories(stagedRoot);
            if (directories.Length != 1 || Directory.EnumerateFiles(stagedRoot).Any())
            {
                return;
            }

            string wrapper = directories[0];
            foreach (string child in Directory.GetFileSystemEntries(wrapper))
            {
                string destination = Path.Combine(stagedRoot, Path.GetFileName(child));
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    return; // Name collision: keep the wrapper rather than merge.
                }

                Directory.Move(child, destination);
            }

            Directory.Delete(wrapper);
        }
    }

    /// <summary>
    /// Builds the install plan: the staged root must already be verified; the plan lists the
    /// target files and every managed file currently in the install root that the target
    /// manifest no longer contains.
    /// </summary>
    public static UpdateInstallPlan BuildPlan(
        string installRoot,
        string stagedRoot,
        string entryRelativePath,
        IReadOnlyList<UpdateFileEntry> targetFiles,
        VerifiedUpdateInventory? previousInventory = null)
    {
        ArgumentNullException.ThrowIfNull(targetFiles);
        HashSet<string> targetPaths = new(PathComparer);
        foreach (UpdateFileEntry file in targetFiles)
        {
            // Refuse unsafe manifest paths at plan time instead of at apply time.
            ResolveSafeRelativePath(installRoot, file.Path);
            targetPaths.Add(CanonicalRelativePath(installRoot, file.Path));
        }

        ResolveSafeRelativePath(installRoot, entryRelativePath);

        List<string> deletes = [];
        string fullRoot = Path.GetFullPath(installRoot);
        if (previousInventory is not null)
        {
            foreach (var entry in previousInventory.Entries.Values)
            {
                if (!targetPaths.Contains(entry.Path) && !IsUpdaterState(entry.Path)
                    && IsUnmodified(ResolveSafeRelativePath(fullRoot, entry.Path), entry)) deletes.Add(entry.Path);
            }
        }

        return new UpdateInstallPlan
        {
            InstallRoot = fullRoot,
            StagedRoot = Path.GetFullPath(stagedRoot),
            EntryRelativePath = NormalizeRelativePath(entryRelativePath),
            Files = [.. targetFiles],
            DeletePaths = deletes,
        };
    }

    /// <summary>
    /// Refuses automatic replacement until a protected, object-bound installation helper is available.
    /// Signed manifests establish content identity, not authority over path-based filesystem writes.
    /// </summary>
    public static UpdateInstallSummary ApplyPlan(UpdateInstallPlan plan, VerifiedUpdateInventory? previousInventory = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        throw new NotSupportedException("自动替换尚无安全的权限隔离支持。请使用系统安装包并按提示提升权限。");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string CanonicalRelativePath(string root, string? path) =>
        Path.GetRelativePath(Path.GetFullPath(root), ResolveSafeRelativePath(root, path)).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsUpdaterState(string path) => path.Equals("UpdateState", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("UpdateState/", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnmodified(string path, VerifiedUpdateInventory.Entry entry)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != entry.Size) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), entry.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How many files landed and how many managed leftovers were removed.</summary>
    public readonly record struct UpdateInstallSummary(int FilesApplied, int FilesDeleted);

    /// <summary>
    /// Builds the hidden staged path next to the running executable where a verified update
    /// waits for the hand-off. Version characters that are invalid in file names become
    /// underscores.
    /// </summary>
    public static string BuildStagedPath(string currentPath, string version)
    {
        string directory = Path.GetDirectoryName(currentPath) ?? Environment.CurrentDirectory;
        string name = Path.GetFileName(currentPath);
        return Path.Combine(directory, $".{name}.{SanitizeFileName(version)}.update");
    }

    public static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static void VerifyFileEntry(string path, UpdateFileEntry file)
    {
        FileInfo info = new(path);
        if (file.Size >= 0 && info.Length != file.Size)
        {
            throw new InvalidDataException($"更新文件大小不匹配：{file.Path}");
        }

        if (string.IsNullOrWhiteSpace(file.Sha256))
        {
            return;
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"更新文件 SHA-256 校验失败：{file.Path}");
        }
    }

    private static void RestoreUnixMode(string path, int? mode)
    {
        if (mode is null or < 0 || OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, (UnixFileMode)mode.Value);
    }

    /// <summary>
    /// Normalizes a relative path to forward slashes without traversal; null or empty input
    /// normalizes to the empty string.
    /// </summary>
    public static string NormalizeRelativePath(string? path)
    {
        string text = path?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return text.Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// Resolves a manifest-relative path inside a root, refusing absolute paths and any
    /// traversal that escapes the root.
    /// </summary>
    public static string ResolveSafeRelativePath(string root, string? relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        if (normalized.Length == 0 || Path.IsPathRooted(relativePath ?? string.Empty))
        {
            throw new InvalidDataException($"更新文件路径不受信任：{relativePath}");
        }

        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string resolved = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        if (!resolved.StartsWith(fullRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && !string.Equals(resolved, fullRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"更新文件路径越界：{relativePath}");
        }

        return resolved;
    }
}
