// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCL.Application.Downloads;

public sealed record MinecraftModMetadata(
    string FilePath,
    string Id,
    string Name,
    string Version,
    string Loader,
    IReadOnlyList<string> Dependencies);

/// <summary>
/// Reads local mod descriptors without loading any mod classes. This is the only file-inspection
/// surface exposed to the crash advisor; archives remain data and never become executable input.
/// </summary>
public static partial class MinecraftModMetadataReader
{
    public static IReadOnlyList<MinecraftModMetadata> ReadDirectory(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory))
            return [];

        List<MinecraftModMetadata> result = [];
        foreach (string path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(IsModArchive)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (TryRead(path, out MinecraftModMetadata? metadata) && metadata is not null)
                result.Add(metadata);
        }
        return result;
    }

    public static bool TryRead(string archivePath, out MinecraftModMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return false;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            if (TryReadFabric(archive, archivePath, out metadata) ||
                TryReadQuilt(archive, archivePath, out metadata) ||
                TryReadToml(archive, archivePath, "META-INF/neoforge.mods.toml", "neoforge", out metadata) ||
                TryReadToml(archive, archivePath, "META-INF/mods.toml", "forge", out metadata) ||
                TryReadManifest(archive, archivePath, out metadata))
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or JsonException)
        {
        }
        return false;
    }

    private static bool TryReadFabric(ZipArchive archive, string path, out MinecraftModMetadata? metadata)
    {
        metadata = null;
        ZipArchiveEntry? entry = archive.GetEntry("fabric.mod.json");
        if (entry is null)
            return false;
        using Stream stream = entry.Open();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        string id = ReadString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
            return false;
        metadata = new MinecraftModMetadata(
            path,
            id,
            ReadString(root, "name", id),
            ReadString(root, "version", "unknown"),
            "fabric",
            ReadObjectKeys(root, "depends"));
        return true;
    }

    private static bool TryReadQuilt(ZipArchive archive, string path, out MinecraftModMetadata? metadata)
    {
        metadata = null;
        ZipArchiveEntry? entry = archive.GetEntry("quilt.mod.json");
        if (entry is null)
            return false;
        using Stream stream = entry.Open();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("quilt_loader", out JsonElement loader))
            return false;
        string id = ReadString(loader, "id");
        if (string.IsNullOrWhiteSpace(id))
            return false;
        string name = id;
        if (loader.TryGetProperty("metadata", out JsonElement descriptor))
            name = ReadString(descriptor, "name", id);
        metadata = new MinecraftModMetadata(
            path,
            id,
            name,
            ReadString(loader, "version", "unknown"),
            "quilt",
            ReadQuiltDependencies(loader));
        return true;
    }

    private static bool TryReadToml(
        ZipArchive archive,
        string path,
        string entryName,
        string loader,
        out MinecraftModMetadata? metadata)
    {
        metadata = null;
        ZipArchiveEntry? entry = archive.GetEntry(entryName);
        if (entry is null)
            return false;
        using StreamReader reader = new(entry.Open());
        string content = reader.ReadToEnd();
        string id = ReadTomlValue(content, "modId");
        if (string.IsNullOrWhiteSpace(id))
            return false;
        string name = ReadTomlValue(content, "displayName");
        string version = ReadTomlValue(content, "version");
        string[] dependencies = TomlDependencyRegex().Matches(content)
            .Select(match => match.Groups[1].Value)
            .Where(value => !string.Equals(value, id, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(value, "minecraft", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(value, "forge", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(value, "neoforge", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        metadata = new MinecraftModMetadata(
            path,
            id,
            string.IsNullOrWhiteSpace(name) ? id : name,
            string.IsNullOrWhiteSpace(version) ? "unknown" : version,
            loader,
            dependencies);
        return true;
    }

    private static bool TryReadManifest(ZipArchive archive, string path, out MinecraftModMetadata? metadata)
    {
        metadata = null;
        ZipArchiveEntry? entry = archive.GetEntry("META-INF/MANIFEST.MF");
        if (entry is null)
            return false;
        using StreamReader reader = new(entry.Open());
        string content = reader.ReadToEnd();
        string title = ReadManifestValue(content, "Implementation-Title");
        if (string.IsNullOrWhiteSpace(title))
            return false;
        metadata = new MinecraftModMetadata(
            path,
            Path.GetFileNameWithoutExtension(path),
            title,
            ReadManifestValue(content, "Implementation-Version", "unknown"),
            "unknown",
            []);
        return true;
    }

    private static string[] ReadObjectKeys(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            return [];
        return value.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static string[] ReadQuiltDependencies(JsonElement loader)
    {
        if (!loader.TryGetProperty("depends", out JsonElement depends) || depends.ValueKind != JsonValueKind.Array)
            return [];
        return depends.EnumerateArray()
            .Select(element => element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.TryGetProperty("id", out JsonElement id) ? id.GetString() : null)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();
    }

    private static string ReadString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string ReadTomlValue(string content, string key)
    {
        Match match = Regex.Match(
            content,
            "(?im)^\\s*" + Regex.Escape(key) + "\\s*=\\s*[\"']([^\"']*)[\"']");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string ReadManifestValue(string content, string key, string fallback = "")
    {
        Match match = Regex.Match(content, "(?im)^" + Regex.Escape(key) + ":\\s*(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : fallback;
    }

    private static bool IsModArchive(string path) =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("(?im)^\\s*modId\\s*=\\s*[\"']([^\"']+)[\"']")]
    private static partial Regex TomlDependencyRegex();
}
