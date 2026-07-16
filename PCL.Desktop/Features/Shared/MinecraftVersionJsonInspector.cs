// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Shared;

internal readonly record struct MinecraftVersionJsonInfo(
    string MinecraftVersionId,
    string? InheritsFrom,
    IReadOnlyList<string> Libraries);

internal static class MinecraftVersionJsonInspector
{
    public static MinecraftVersionJsonInfo Read(LaunchInstanceInfo instance)
    {
        if (!File.Exists(instance.VersionJsonPath))
            return new MinecraftVersionJsonInfo(instance.Name, null, []);

        List<string> libraries = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string? immediateInherited = null;
        string minecraftVersionId = instance.Name;
        string? jsonPath = instance.VersionJsonPath;
        string? versionsDirectory = Directory.GetParent(instance.InstanceDirectory)?.FullName;

        for (int depth = 0; depth < 32 && !string.IsNullOrWhiteSpace(jsonPath); depth++)
        {
            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(jsonPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                break;
            }

            if (!visited.Add(normalizedPath) || !TryReadVersionJson(normalizedPath, out VersionJsonNode node))
                break;

            libraries.AddRange(node.Libraries);
            if (depth == 0)
                immediateInherited = node.InheritsFrom;

            if (string.IsNullOrWhiteSpace(node.InheritsFrom))
            {
                minecraftVersionId = !string.IsNullOrWhiteSpace(node.ClientVersion)
                    ? node.ClientVersion
                    : !string.IsNullOrWhiteSpace(node.Id) ? node.Id : minecraftVersionId;
                break;
            }

            minecraftVersionId = node.InheritsFrom;
            jsonPath = ResolveInheritedJsonPath(
                versionsDirectory,
                instance.InstanceDirectory,
                node.InheritsFrom);
        }

        return new MinecraftVersionJsonInfo(
            minecraftVersionId,
            string.IsNullOrWhiteSpace(immediateInherited) ? null : immediateInherited,
            libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool TryReadVersionJson(string jsonPath, out VersionJsonNode node)
    {
        node = default;
        try
        {
            using FileStream stream = File.OpenRead(jsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            node = new VersionJsonNode(
                ReadJsonString(root, "id"),
                ReadJsonString(root, "clientVersion"),
                ReadJsonString(root, "inheritsFrom"),
                ReadLibraryNames(root).ToArray());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ResolveInheritedJsonPath(
        string? versionsDirectory,
        string instanceDirectory,
        string inheritedVersion)
    {
        if (string.IsNullOrWhiteSpace(inheritedVersion) ||
            inheritedVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(inheritedVersion), inheritedVersion, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(versionsDirectory))
        {
            string sibling = Path.Combine(
                versionsDirectory,
                inheritedVersion,
                inheritedVersion + ".json");
            if (File.Exists(sibling))
                return sibling;
        }

        string local = Path.Combine(instanceDirectory, inheritedVersion + ".json");
        return File.Exists(local) ? local : null;
    }

    private static string ReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static IEnumerable<string> ReadLibraryNames(JsonElement root)
    {
        if (!root.TryGetProperty("libraries", out JsonElement libraries) ||
            libraries.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement library in libraries.EnumerateArray())
        {
            if (library.TryGetProperty("name", out JsonElement nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                yield return nameElement.GetString()!;
            }
        }
    }

    private readonly record struct VersionJsonNode(
        string Id,
        string ClientVersion,
        string InheritsFrom,
        IReadOnlyList<string> Libraries);
}

internal static class MinecraftLoaderLibraryDetector
{
    public static string? DetectVersion(IReadOnlyList<string> libraries, params string[] needles)
    {
        string? library = libraries.FirstOrDefault(library =>
            needles.Any(needle => library.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        return string.IsNullOrWhiteSpace(library) ? null : SimplifyVersion(library);
    }

    public static string SimplifyVersion(string library)
    {
        string[] coordinate = library.Split(':');
        if (coordinate.Length < 3 || string.IsNullOrWhiteSpace(coordinate[2]))
            return "已安装";

        string version = coordinate[2];
        int minecraftPrefixIndex = version.IndexOf('-');
        return minecraftPrefixIndex > 0 && minecraftPrefixIndex < version.Length - 1
            ? version[(minecraftPrefixIndex + 1)..]
            : version;
    }
}
