// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.Logging;
using PCL.Platform.Paths;

namespace PCL.Desktop.Paths;

/// <summary>
/// Optional portable path overrides for launcher data / cache (OOBE + advanced setups).
/// Stored next to the host binary so custom locations survive restarts before settings load.
/// </summary>
internal static class LauncherPathLayout
{
    public const string FileName = "pcln-paths.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string OverrideFilePath =>
        Path.Combine(AppContext.BaseDirectory, FileName);

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
        string path = OverrideFilePath;
        if (!File.Exists(path))
            return new LauncherPathOverrideDocument();

        try
        {
            string json = File.ReadAllText(path);
            LauncherPathOverrideDocument? doc =
                JsonSerializer.Deserialize(json, LauncherPathLayoutJsonContext.Default.LauncherPathOverrideDocument);
            return doc ?? new LauncherPathOverrideDocument();
        }
        catch (Exception ex)
        {
            PortableLog.Warn("Paths", "读取路径覆盖失败：" + ex.Message);
            return new LauncherPathOverrideDocument();
        }
    }

    public static void Save(LauncherPathOverrideDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string path = OverrideFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        string json = JsonSerializer.Serialize(
            document,
            LauncherPathLayoutJsonContext.Default.LauncherPathOverrideDocument);
        File.WriteAllText(path, json);
        PortableLog.Info("Paths", "已写入路径覆盖：" + path);
    }

    public static string ResolveDataDirectory(LauncherPathOverrideDocument? document = null)
    {
        document ??= Load();
        if (!string.IsNullOrWhiteSpace(document.ApplicationDataDirectory))
            return Path.GetFullPath(document.ApplicationDataDirectory.Trim());
        return GetDefaultDataDirectory();
    }

    public static string ResolveCacheDirectory(LauncherPathOverrideDocument? document = null)
    {
        document ??= Load();
        if (!string.IsNullOrWhiteSpace(document.CacheDirectory))
            return Path.GetFullPath(document.CacheDirectory.Trim());
        return GetDefaultCacheDirectory();
    }

    public static string ResolveSettingsFilePath(LauncherPathOverrideDocument? document = null) =>
        Path.Combine(ResolveDataDirectory(document), "launcher-settings.json");

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

        Directory.CreateDirectory(newData);
        Directory.CreateDirectory(newCache);

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
        Save(doc);

        return new LauncherPathMigrationResult(oldData, newData, oldCache, newCache, dataMigrated, cacheMigrated);
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

            // Prefer newer / keep existing target if locked.
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
