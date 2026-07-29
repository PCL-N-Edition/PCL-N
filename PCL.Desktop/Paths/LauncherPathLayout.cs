// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.Logging;
using PCL.Platform.Paths;

namespace PCL.Desktop.Paths;

/// <summary>
/// Optional portable path overrides for launcher data / cache (OOBE + advanced setups).
/// <c>pcln-paths.json</c> has one canonical location under the platform-local
/// application-data directory. It is never created next to the launcher executable.
/// </summary>
internal static class LauncherPathLayout
{
    public const string FileName = "pcln-paths.json";

    private static readonly object Gate = new();
    private static LauncherPathOverrideDocument? _cachedDocument;

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
            _cachedDocument = null;
    }

    public static void Save(LauncherPathOverrideDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string primary = ResolveOverrideFilePath();
        try
        {
            string json = JsonSerializer.Serialize(
                document,
                LauncherPathLayoutJsonContext.Default.LauncherPathOverrideDocument);
            WriteOverrideFile(primary, json);
            CleanupLegacyOverrideFiles(primary);

            lock (Gate)
                _cachedDocument = Clone(document);

            PortableLog.Info("Paths", "已写入 LocalAppData 路径覆盖：" + primary);
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

    /// <summary>Safe log root: data/Logs, LocalAppData/PCL-N/Logs, or TEMP/PCL-N/Logs — never throws.</summary>
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
            string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localRoot))
            {
                string dir = Path.Combine(localRoot, "PCL-N", "Logs");
                if (TryEnsureDirectory(dir))
                    return dir;
            }
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
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = new DefaultPlatformPathProvider().ApplicationDataDirectory;
        return Path.GetFullPath(Path.Combine(root, "PCL-N", FileName));
    }

    /// <summary>
    /// Legacy locations are read once during upgrade, copied to LocalAppData,
    /// and then deleted. They are never regular lookup or write targets.
    /// </summary>
    private static IEnumerable<string> EnumerateLegacyOverrideCandidateFiles()
    {
        foreach (string dir in EnumerateHostCandidateDirectories())
            yield return Path.Combine(dir, FileName);

        string roamingRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingRoot))
            yield return Path.Combine(roamingRoot, "PCL-N", FileName);
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
        string primary = ResolveOverrideFilePath();
        if (TryReadOverrideFile(primary, out LauncherPathOverrideDocument? primaryDocument, out _))
        {
            CleanupLegacyOverrideFiles(primary);
            PortableLog.Info("Paths", "已加载 LocalAppData 路径覆盖：" + primary);
            return primaryDocument!;
        }

        foreach (string path in EnumerateLegacyOverrideCandidateFiles()
                     .Where(path => !PathsEqual(path, primary))
                     .Distinct(OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase
                         : StringComparer.Ordinal))
        {
            if (!TryReadOverrideFile(path, out LauncherPathOverrideDocument? document, out string? json))
                continue;

            try
            {
                WriteOverrideFile(primary, json!);
                CleanupLegacyOverrideFiles(primary);
                PortableLog.Info("Paths", $"已将旧路径覆盖迁移到 LocalAppData：{path} → {primary}");
            }
            catch (Exception ex)
            {
                PortableLog.Warn("Paths", "迁移旧路径覆盖失败，继续使用本次读取结果：" + ex.Message);
            }

            return document!;
        }

        // No mapping file → clean defaults. Never throw.
        return new LauncherPathOverrideDocument();
    }

    private static bool TryReadOverrideFile(
        string path,
        out LauncherPathOverrideDocument? document,
        out string? json)
    {
        document = null;
        json = null;
        if (!File.Exists(path))
            return false;

        try
        {
            json = File.ReadAllText(path);
            document = JsonSerializer.Deserialize(
                json,
                LauncherPathLayoutJsonContext.Default.LauncherPathOverrideDocument);
            return document is not null;
        }
        catch (Exception ex)
        {
            PortableLog.Warn("Paths", $"读取路径覆盖失败（{path}）：" + ex.Message);
            return false;
        }
    }

    private static void WriteOverrideFile(string path, string json)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
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

    private static void CleanupLegacyOverrideFiles(string primary)
    {
        foreach (string path in EnumerateLegacyOverrideCandidateFiles()
                     .Where(path => !PathsEqual(path, primary))
                     .Distinct(OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase
                         : StringComparer.Ordinal))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                PortableLog.Warn("Paths", $"清理旧路径覆盖失败（{path}）：" + ex.Message);
            }
        }
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
