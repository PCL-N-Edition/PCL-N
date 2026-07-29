// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.Logging;
using PCL.Platform.Paths;

namespace PCL.Desktop.Paths;

/// <summary>
/// Optional portable path overrides for launcher data / cache (OOBE + advanced setups).
/// <c>pcln-paths.json</c> is resolved next to the host executable (not AppContext.BaseDirectory,
/// which may be a temp extract folder on C: for single-file / some host modes).
/// </summary>
internal static class LauncherPathLayout
{
    public const string FileName = "pcln-paths.json";

    private static readonly object Gate = new();
    private static LauncherPathOverrideDocument? _cachedDocument;
    private static string? _cachedOverrideFilePath;

    public static string OverrideFilePath => ResolveOverrideFilePath();

    /// <summary>
    /// Directory that contains the real host binary (preferred over <see cref="AppContext.BaseDirectory"/>).
    /// </summary>
    public static string GetHostDirectory()
    {
        try
        {
            string? executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath) &&
                !string.Equals(
                    Path.GetFileNameWithoutExtension(executablePath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                string? executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
                if (!string.IsNullOrWhiteSpace(executableDirectory) && Directory.Exists(executableDirectory))
                    return executableDirectory;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            string baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            if (Directory.Exists(baseDir))
                return baseDir;
        }
        catch
        {
            // fall through
        }

        return Path.GetFullPath(Environment.CurrentDirectory);
    }

    public static string GetDefaultDataDirectory()
    {
        DefaultPlatformPathProvider paths = new();
        return Path.GetFullPath(Path.Combine(paths.ApplicationDataDirectory, "PCL-N"));
    }

    public static string GetDefaultCacheDirectory()
    {
        DefaultPlatformPathProvider paths = new();
        return Path.GetFullPath(Path.Combine(paths.CacheDirectory, "PCL-N"));
    }

    public static LauncherPathOverrideDocument Load()
    {
        lock (Gate)
        {
            if (_cachedDocument is not null)
                return Clone(_cachedDocument);
        }

        LauncherPathOverrideDocument doc = LoadCore();
        lock (Gate)
            _cachedDocument = Clone(doc);
        return doc;
    }

    /// <summary>Drop cached path layout (tests / after external file edit).</summary>
    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _cachedDocument = null;
            _cachedOverrideFilePath = null;
        }
    }

    public static void Save(LauncherPathOverrideDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string primary = Path.Combine(GetHostDirectory(), FileName);
        try
        {
            string json = JsonSerializer.Serialize(
                document,
                LauncherPathLayoutJsonContext.Default.LauncherPathOverrideDocument);

            // Write to every known location so deleting only the exe-side file does not lose the mapping.
            foreach (string path in EnumerateWriteTargets(primary))
            {
                try
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(path, json);
                }
                catch (Exception mirrorEx)
                {
                    PortableLog.Warn("Paths", $"写入路径覆盖失败（{path}）：{mirrorEx.Message}");
                }
            }

            lock (Gate)
            {
                _cachedDocument = Clone(document);
                _cachedOverrideFilePath = primary;
            }

            PortableLog.Info("Paths", "已写入路径覆盖（含系统备份）：" + primary);
        }
        catch (Exception ex)
        {
            PortableLog.Warn("Paths", "写入路径覆盖失败：" + ex.Message);
        }
    }

    public static string ResolveDataDirectory(LauncherPathOverrideDocument? document = null)
    {
        document ??= Load();
        if (!string.IsNullOrWhiteSpace(document.ApplicationDataDirectory))
        {
            if (TryNormalizeExistingOrCreatableDirectory(
                    document.ApplicationDataDirectory,
                    out string custom))
                return custom;

            PortableLog.Warn(
                "Paths",
                "自定义数据目录不可用，回退默认：" + document.ApplicationDataDirectory);
        }

        string fallback = GetDefaultDataDirectory();
        TryEnsureDirectory(fallback);
        return fallback;
    }

    public static string ResolveCacheDirectory(LauncherPathOverrideDocument? document = null)
    {
        document ??= Load();
        if (!string.IsNullOrWhiteSpace(document.CacheDirectory))
        {
            if (TryNormalizeExistingOrCreatableDirectory(document.CacheDirectory, out string custom))
                return custom;

            PortableLog.Warn(
                "Paths",
                "自定义缓存目录不可用，回退默认：" + document.CacheDirectory);
        }

        string fallback = GetDefaultCacheDirectory();
        TryEnsureDirectory(fallback);
        return fallback;
    }

    public static string ResolveSettingsFilePath(LauncherPathOverrideDocument? document = null) =>
        Path.Combine(ResolveDataDirectory(document), "launcher-settings.json");

    /// <summary>
    /// Parent directory used by APIs that nest a <c>PCL-N</c> segment themselves
    /// (e.g. <c>DefaultSecureStorage</c>). When the data dir is already <c>…/PCL-N</c>, returns its parent.
    /// </summary>
    public static string ResolveLegacyApplicationDataRoot(LauncherPathOverrideDocument? document = null)
    {
        string data = ResolveDataDirectory(document);
        string leaf = Path.GetFileName(data.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, "PCL-N", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(data);
            if (!string.IsNullOrWhiteSpace(parent))
                return parent;
        }

        return data;
    }

    /// <summary>Safe log root: data/Logs, or host/Logs, or TEMP/PCL-N/Logs — never throws.</summary>
    public static string ResolveLogDirectory()
    {
        try
        {
            string dir = Path.Combine(ResolveDataDirectory(), "Logs");
            if (TryEnsureDirectory(dir))
                return dir;
        }
        catch
        {
            // fall through
        }

        try
        {
            string dir = Path.Combine(GetHostDirectory(), "Logs");
            if (TryEnsureDirectory(dir))
                return dir;
        }
        catch
        {
            // fall through
        }

        string temp = Path.Combine(Path.GetTempPath(), "PCL-N", "Logs");
        try { Directory.CreateDirectory(temp); } catch { /* ignore */ }
        return temp;
    }

    /// <summary>
    /// Persist chosen roots and copy existing data/cache into the new locations when they differ.
    /// </summary>
    public static LauncherPathMigrationResult ApplyAndMigrate(string? dataDirectory, string? cacheDirectory)
    {
        string oldData = ResolveDataDirectory();
        string oldCache = ResolveCacheDirectory();

        string newData = string.IsNullOrWhiteSpace(dataDirectory)
            ? GetDefaultDataDirectory()
            : Path.GetFullPath(dataDirectory.Trim());
        string newCache = string.IsNullOrWhiteSpace(cacheDirectory)
            ? GetDefaultCacheDirectory()
            : Path.GetFullPath(cacheDirectory.Trim());

        TryEnsureDirectory(newData);
        TryEnsureDirectory(newCache);

        bool dataMigrated = false;
        bool cacheMigrated = false;

        if (!PathsEqual(oldData, newData))
        {
            dataMigrated = TryMigrateDirectory(oldData, newData);
            PortableLog.Info("Paths", $"OOBE 数据目录迁移：{oldData} → {newData}；ok={dataMigrated}");
        }

        if (!PathsEqual(oldCache, newCache))
        {
            cacheMigrated = TryMigrateDirectory(oldCache, newCache);
            PortableLog.Info("Paths", $"OOBE 缓存目录迁移：{oldCache} → {newCache}；ok={cacheMigrated}");
        }

        // Only persist overrides when non-default so portable defaults stay clean.
        LauncherPathOverrideDocument doc = new()
        {
            ApplicationDataDirectory = PathsEqual(newData, GetDefaultDataDirectory()) ? null : newData,
            CacheDirectory = PathsEqual(newCache, GetDefaultCacheDirectory()) ? null : newCache
        };
        InvalidateCache();
        Save(doc);

        return new LauncherPathMigrationResult(oldData, newData, oldCache, newCache, dataMigrated, cacheMigrated);
    }

    private static string ResolveOverrideFilePath()
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(_cachedOverrideFilePath))
                return _cachedOverrideFilePath!;
        }

        foreach (string candidate in EnumerateOverrideCandidateFiles())
        {
            if (File.Exists(candidate))
            {
                lock (Gate)
                    _cachedOverrideFilePath = candidate;
                return candidate;
            }
        }

        string primary = Path.Combine(GetHostDirectory(), FileName);
        lock (Gate)
            _cachedOverrideFilePath = primary;
        return primary;
    }

    /// <summary>
    /// Search order for path mapping:
    /// 1) real exe directory
    /// 2) AppContext.BaseDirectory / cwd
    /// 3) %LocalAppData%/PCL-N and %AppData%/PCL-N backups (survive deleting only the exe-side json)
    /// </summary>
    private static IEnumerable<string> EnumerateOverrideCandidateFiles()
    {
        foreach (string dir in EnumerateHostCandidateDirectories())
            yield return Path.Combine(dir, FileName);

        foreach (string backupDir in EnumerateSystemBackupDirectories())
            yield return Path.Combine(backupDir, FileName);
    }

    private static HashSet<string> EnumerateWriteTargets(string primary)
    {
        HashSet<string> seen = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p))
                return;
            try { seen.Add(Path.GetFullPath(p)); } catch { /* skip */ }
        }

        Add(primary);
        try { Add(Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), FileName)); } catch { /* skip */ }
        foreach (string backupDir in EnumerateSystemBackupDirectories())
            Add(Path.Combine(backupDir, FileName));
        return seen;
    }

    private static IEnumerable<string> EnumerateSystemBackupDirectories()
    {
        // Always under the *system* profile folders so a missing exe-side json can still recover.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCL-N");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCL-N");
    }

    private static HashSet<string> EnumerateHostCandidateDirectories()
    {
        HashSet<string> seen = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                string full = Path.GetFullPath(path);
                if (Directory.Exists(full))
                    seen.Add(full);
            }
            catch
            {
                // skip invalid
            }
        }

        Add(GetHostDirectory());
        try { Add(AppContext.BaseDirectory); } catch { /* ignore */ }
        try { Add(Environment.CurrentDirectory); } catch { /* ignore */ }

        return seen;
    }

    private static LauncherPathOverrideDocument LoadCore()
    {
        foreach (string path in EnumerateOverrideCandidateFiles())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                string json = File.ReadAllText(path);
                LauncherPathOverrideDocument? doc =
                    JsonSerializer.Deserialize(
                        json,
                        LauncherPathLayoutJsonContext.Default.LauncherPathOverrideDocument);
                if (doc is not null)
                {
                    PortableLog.Info("Paths", "已加载路径覆盖：" + path);
                    lock (Gate)
                        _cachedOverrideFilePath = path;

                    // If exe-side file was deleted, restore it from backup so next launches stay stable.
                    try
                    {
                        string hostCopy = Path.Combine(GetHostDirectory(), FileName);
                        if (!File.Exists(hostCopy) && !PathsEqual(hostCopy, path))
                        {
                            Directory.CreateDirectory(GetHostDirectory());
                            File.WriteAllText(hostCopy, json);
                            PortableLog.Info("Paths", "已从备份恢复 exe 旁路径覆盖：" + hostCopy);
                        }
                    }
                    catch
                    {
                        // best-effort restore
                    }

                    return doc;
                }
            }
            catch (Exception ex)
            {
                PortableLog.Warn("Paths", $"读取路径覆盖失败（{path}）：" + ex.Message);
            }
        }

        // No mapping file → clean defaults. Never throw.
        return new LauncherPathOverrideDocument();
    }

    private static bool TryNormalizeExistingOrCreatableDirectory(string raw, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            fullPath = Path.GetFullPath(raw.Trim());
            // Reject obvious non-directory roots (e.g. missing drive letter on Windows).
            if (OperatingSystem.IsWindows() &&
                fullPath.Length >= 2 &&
                fullPath[1] == ':' &&
                !Directory.Exists(fullPath[..2] + "\\"))
            {
                return false;
            }

            if (!TryEnsureDirectory(fullPath))
                return false;

            return true;
        }
        catch
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMigrateDirectory(string source, string destination)
    {
        try
        {
            if (!Directory.Exists(source))
                return false;
            if (PathsEqual(source, destination))
                return false;

            Directory.CreateDirectory(destination);
            CopyDirectoryRecursive(source, destination);
            return true;
        }
        catch (Exception ex)
        {
            PortableLog.Warn("Paths", $"目录迁移失败 {source} → {destination}：{ex.Message}");
            return false;
        }
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            try
            {
                File.Copy(file, target, overwrite: true);
            }
            catch (IOException)
            {
                if (!File.Exists(target))
                    throw;
            }
        }
    }

    private static LauncherPathOverrideDocument Clone(LauncherPathOverrideDocument source) =>
        new()
        {
            ApplicationDataDirectory = source.ApplicationDataDirectory,
            CacheDirectory = source.CacheDirectory
        };

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed class LauncherPathOverrideDocument
{
    public string? ApplicationDataDirectory { get; set; }

    public string? CacheDirectory { get; set; }
}

internal sealed record LauncherPathMigrationResult(
    string PreviousDataDirectory,
    string DataDirectory,
    string PreviousCacheDirectory,
    string CacheDirectory,
    bool DataMigrated,
    bool CacheMigrated);

[JsonSerializable(typeof(LauncherPathOverrideDocument))]
internal sealed partial class LauncherPathLayoutJsonContext : JsonSerializerContext;
