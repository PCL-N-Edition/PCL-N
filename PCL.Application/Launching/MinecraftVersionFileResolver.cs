// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;

namespace PCL.Application.Launching;

internal static class MinecraftVersionFileResolver
{
    public static string? FindPrimaryJson(string versionDirectory)
    {
        if (string.IsNullOrWhiteSpace(versionDirectory) || !Directory.Exists(versionDirectory))
            return null;

        string directoryName = new DirectoryInfo(versionDirectory).Name;
        string? conventional = FindFile(versionDirectory, directoryName + ".json");
        if (conventional is not null)
            return conventional;

        string[] jsonFiles = EnumerateFiles(versionDirectory, "*.json");
        if (jsonFiles.Length == 0)
            return null;

        List<JsonCandidate> candidates = [];
        foreach (string jsonFile in jsonFiles)
        {
            if (TryReadJsonCandidate(jsonFile, out JsonCandidate candidate))
                candidates.Add(candidate);
        }

        JsonCandidate? matchingId = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, directoryName, StringComparison.OrdinalIgnoreCase));
        if (matchingId is not null)
            return matchingId.Path;

        if (candidates.Count == 1)
            return candidates[0].Path;

        JsonCandidate[] descriptors = candidates.Where(static candidate => candidate.LooksLikeVersion).ToArray();
        return descriptors.Length == 1 ? descriptors[0].Path : null;
    }

    public static string? ResolveJsonPath(
        string minecraftRootDirectory,
        string? localDirectory,
        string versionReference)
    {
        if (!IsSafeVersionReference(versionReference))
            return null;

        string versionsDirectory = Path.Combine(minecraftRootDirectory, "versions");
        string? referenceDirectory = FindDirectory(versionsDirectory, versionReference);
        if (referenceDirectory is not null)
        {
            string? conventional = FindFile(referenceDirectory, versionReference + ".json");
            if (conventional is not null)
                return conventional;

            string? primary = FindPrimaryJson(referenceDirectory);
            if (primary is not null)
                return primary;
        }

        string? local = FindJsonByFileNameOrId(localDirectory, versionReference);
        if (local is not null)
            return local;

        foreach (string directory in EnumerateDirectories(versionsDirectory))
        {
            if (referenceDirectory is not null && PathsEqual(directory, referenceDirectory))
                continue;

            string? match = FindJsonByFileNameOrId(directory, versionReference);
            if (match is not null)
                return match;
        }

        return null;
    }

    public static string? ResolveJarPath(
        string minecraftRootDirectory,
        string? localDirectory,
        string versionReference)
    {
        if (!IsSafeVersionReference(versionReference))
            return null;

        string versionsDirectory = Path.Combine(minecraftRootDirectory, "versions");
        string? referenceDirectory = FindDirectory(versionsDirectory, versionReference);
        string? direct = FindFile(referenceDirectory, versionReference + ".jar")
                         ?? FindFile(localDirectory, versionReference + ".jar");
        if (direct is not null)
            return direct;

        foreach (string directory in EnumerateDirectories(versionsDirectory))
        {
            direct = FindFile(directory, versionReference + ".jar");
            if (direct is not null)
                return direct;
        }

        string? jsonPath = ResolveJsonPath(minecraftRootDirectory, localDirectory, versionReference);
        if (jsonPath is null)
            return null;

        string jsonDirectory = Path.GetDirectoryName(jsonPath) ?? string.Empty;
        string jsonName = Path.GetFileNameWithoutExtension(jsonPath);
        string? byJsonName = FindFile(jsonDirectory, jsonName + ".jar");
        if (byJsonName is not null)
            return byJsonName;

        if (TryReadJsonCandidate(jsonPath, out JsonCandidate candidate) &&
            IsSafeVersionReference(candidate.Id))
        {
            string? byId = FindFile(jsonDirectory, candidate.Id + ".jar");
            if (byId is not null)
                return byId;
        }

        string directoryName = new DirectoryInfo(jsonDirectory).Name;
        return FindFile(jsonDirectory, directoryName + ".jar");
    }

    public static bool IsLegacyLiteLoaderWithoutInheritance(
        string? inheritsFrom,
        string? jar,
        string? currentVersionId,
        string? currentDirectoryName,
        bool hasLogging,
        IEnumerable<string> loaderSignals)
    {
        if (!string.IsNullOrWhiteSpace(inheritsFrom) ||
            !IsSafeVersionReference(jar) ||
            string.Equals(jar, currentVersionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(jar, currentDirectoryName, StringComparison.OrdinalIgnoreCase) ||
            hasLogging)
        {
            return false;
        }

        return loaderSignals.Any(static signal =>
            signal.Contains("liteloader", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindJsonByFileNameOrId(string? directory, string versionReference)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        string? byFileName = FindFile(directory, versionReference + ".json");
        if (byFileName is not null)
            return byFileName;

        foreach (string jsonFile in EnumerateFiles(directory, "*.json"))
        {
            if (TryReadJsonCandidate(jsonFile, out JsonCandidate candidate) &&
                string.Equals(candidate.Id, versionReference, StringComparison.OrdinalIgnoreCase))
            {
                return jsonFile;
            }
        }

        return null;
    }

    private static bool IsSafeVersionReference(string? versionReference) =>
        !string.IsNullOrWhiteSpace(versionReference) &&
        versionReference.Length <= 180 &&
        versionReference is not "." and not ".." &&
        !Path.IsPathRooted(versionReference) &&
        versionReference.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        string.Equals(Path.GetFileName(versionReference), versionReference, StringComparison.Ordinal);

    private static string? FindDirectory(string parentDirectory, string name)
    {
        if (!Directory.Exists(parentDirectory))
            return null;

        string exact = Path.Combine(parentDirectory, name);
        if (Directory.Exists(exact))
            return exact;

        return EnumerateDirectories(parentDirectory).FirstOrDefault(directory =>
            string.Equals(new DirectoryInfo(directory).Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindFile(string? directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        string exact = Path.Combine(directory, fileName);
        if (File.Exists(exact))
            return exact;

        return EnumerateFiles(directory, "*").FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] EnumerateFiles(string directory, string searchPattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryReadJsonCandidate(string path, out JsonCandidate candidate)
    {
        candidate = null!;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string id = ReadString(root, "id");
            bool looksLikeVersion = !string.IsNullOrWhiteSpace(id) ||
                                    root.TryGetProperty("mainClass", out _) ||
                                    root.TryGetProperty("inheritsFrom", out _) ||
                                    root.TryGetProperty("jar", out _) ||
                                    root.TryGetProperty("clientVersion", out _) ||
                                    root.TryGetProperty("libraries", out _) ||
                                    root.TryGetProperty("downloads", out _) ||
                                    root.TryGetProperty("arguments", out _) ||
                                    root.TryGetProperty("minecraftArguments", out _) ||
                                    root.TryGetProperty("patches", out _);
            candidate = new JsonCandidate(path, id, looksLikeVersion);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed record JsonCandidate(string Path, string Id, bool LooksLikeVersion);
}
