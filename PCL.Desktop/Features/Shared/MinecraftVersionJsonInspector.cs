// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCL.Application.Launching;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Shared;

internal readonly record struct MinecraftVersionJsonInfo(
    string MinecraftVersionId,
    string? InheritsFrom,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<string> LoaderEntries);

internal static partial class MinecraftVersionJsonInspector
{
    public static MinecraftVersionJsonInfo Read(LaunchInstanceInfo instance)
    {
        if (!File.Exists(instance.VersionJsonPath))
            return new MinecraftVersionJsonInfo(instance.Name, null, [], []);

        List<string> libraries = [];
        List<string> loaderEntries = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string? immediateInherited = null;
        string? preferredVanillaVersion = null;
        string minecraftVersionId = instance.Name;
        string? jsonPath = instance.VersionJsonPath;
        string? minecraftRoot = Directory.GetParent(instance.InstanceDirectory)?.Parent?.FullName;

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
            loaderEntries.AddRange(node.Libraries);
            loaderEntries.AddRange(node.LoaderSignals);
            preferredVanillaVersion ??= InferMinecraftVersion(node);

            string? inheritedVersion = FirstNonEmpty(node.InheritsFrom);
            if (MinecraftVersionFileResolver.IsLegacyLiteLoaderWithoutInheritance(
                    inheritedVersion,
                    node.Jar,
                    node.Id,
                    new DirectoryInfo(Path.GetDirectoryName(normalizedPath) ?? string.Empty).Name,
                    node.HasLogging,
                    node.Libraries.Concat(node.LoaderSignals)))
            {
                inheritedVersion = node.Jar;
            }

            if (depth == 0)
                immediateInherited = inheritedVersion;

            if (string.IsNullOrWhiteSpace(inheritedVersion))
            {
                minecraftVersionId = FirstNonEmpty(
                                         preferredVanillaVersion,
                                         node.ClientVersion,
                                         node.Jar,
                                         node.Id)
                                     ?? minecraftVersionId;
                break;
            }

            minecraftVersionId = preferredVanillaVersion ?? inheritedVersion;
            jsonPath = string.IsNullOrWhiteSpace(minecraftRoot)
                ? null
                : MinecraftVersionFileResolver.ResolveJsonPath(
                    minecraftRoot,
                    Path.GetDirectoryName(normalizedPath),
                    inheritedVersion);
        }

        return new MinecraftVersionJsonInfo(
            FindMinecraftVersion(minecraftVersionId) ??
            FindMinecraftVersion(instance.Name) ??
            minecraftVersionId,
            string.IsNullOrWhiteSpace(immediateInherited) ? null : immediateInherited,
            libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            loaderEntries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool TryReadVersionJson(string jsonPath, out VersionJsonNode node)
    {
        node = default;
        try
        {
            using FileStream stream = File.OpenRead(jsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            string[] argumentValues = ReadArgumentValues(root).ToArray();
            node = new VersionJsonNode(
                ReadJsonString(root, "id"),
                ReadJsonString(root, "clientVersion"),
                ReadJsonString(root, "inheritsFrom"),
                ReadJsonString(root, "jar"),
                root.TryGetProperty("logging", out _),
                ReadLibraryNames(root).ToArray(),
                ReadLoaderSignals(root, argumentValues).ToArray(),
                argumentValues,
                ReadPatchGameVersion(root),
                ReadDownloadUrls(root).ToArray());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static IEnumerable<string> ReadLibraryNames(JsonElement root)
    {
        foreach (JsonElement part in EnumerateVersionParts(root))
        {
            if (!part.TryGetProperty("libraries", out JsonElement libraries) ||
                libraries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement library in libraries.EnumerateArray())
            {
                if (library.ValueKind == JsonValueKind.Object &&
                    library.TryGetProperty("name", out JsonElement nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    yield return nameElement.GetString()!;
                }
            }
        }
    }

    private static IEnumerable<string> ReadLoaderSignals(JsonElement root, IReadOnlyList<string> argumentValues)
    {
        foreach (JsonElement part in EnumerateVersionParts(root))
        {
            foreach (string signal in new[]
                     {
                         ReadJsonString(part, "id"),
                         ReadJsonString(part, "mainClass"),
                         ReadJsonString(part, "jar")
                     })
            {
                if (!string.IsNullOrWhiteSpace(signal))
                    yield return signal;
            }

            if (part.TryGetProperty("labymod_data", out JsonElement labyModData) &&
                labyModData.ValueKind == JsonValueKind.Object)
            {
                string version = ReadJsonString(labyModData, "version");
                yield return string.IsNullOrWhiteSpace(version)
                    ? "labymod"
                    : "net.labymod:labymod:" + version;
            }
        }

        foreach (string argument in argumentValues)
            yield return argument;
    }

    private static IEnumerable<JsonElement> EnumerateVersionParts(JsonElement root)
    {
        yield return root;
        if (!root.TryGetProperty("patches", out JsonElement patches) || patches.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (JsonElement patch in patches.EnumerateArray())
        {
            if (patch.ValueKind == JsonValueKind.Object)
                yield return patch;
        }
    }

    private static IEnumerable<string> ReadArgumentValues(JsonElement root)
    {
        foreach (JsonElement part in EnumerateVersionParts(root))
        {
            if (part.TryGetProperty("arguments", out JsonElement arguments))
            {
                foreach (string value in EnumerateStrings(arguments))
                    yield return value;
            }

            string legacyArguments = ReadJsonString(part, "minecraftArguments");
            if (!string.IsNullOrWhiteSpace(legacyArguments))
                yield return legacyArguments;
        }
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            foreach (string value in EnumerateStrings(child))
                yield return value;
            yield break;
        }

        if (element.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (JsonProperty property in element.EnumerateObject())
        foreach (string value in EnumerateStrings(property.Value))
            yield return value;
    }

    private static string ReadPatchGameVersion(JsonElement root)
    {
        if (!root.TryGetProperty("patches", out JsonElement patches) || patches.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (JsonElement patch in patches.EnumerateArray())
        {
            if (patch.ValueKind == JsonValueKind.Object &&
                string.Equals(ReadJsonString(patch, "id"), "game", StringComparison.OrdinalIgnoreCase))
            {
                return ReadJsonString(patch, "version");
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> ReadDownloadUrls(JsonElement root)
    {
        foreach (JsonElement part in EnumerateVersionParts(root))
        {
            if (part.TryGetProperty("downloads", out JsonElement downloads) &&
                downloads.ValueKind == JsonValueKind.Object)
            {
                foreach (string value in EnumerateStrings(downloads))
                {
                    if (Uri.TryCreate(value, UriKind.Absolute, out _))
                        yield return value;
                }
            }
        }
    }

    private static string? InferMinecraftVersion(VersionJsonNode node) =>
        FirstNonEmpty(
            FindMinecraftVersion(node.ClientVersion),
            FindMinecraftVersion(node.PatchGameVersion),
            FindFmlMinecraftVersion(node.ArgumentValues),
            FindLabyModMinecraftVersion(node.ArgumentValues),
            FindLibraryMinecraftVersion(node.Libraries),
            FindDownloadMinecraftVersion(node.DownloadUrls),
            FindMinecraftVersion(node.Jar),
            FindMinecraftVersion(node.Id));

    private static string? FindFmlMinecraftVersion(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], "--fml.mcVersion", StringComparison.OrdinalIgnoreCase))
                return FindMinecraftVersion(arguments[index + 1]);
        }

        return null;
    }

    private static string? FindLabyModMinecraftVersion(IEnumerable<string> arguments)
    {
        const string prefix = "-Dnet.labymod.running-version=";
        foreach (string argument in arguments)
        {
            int index = argument.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                return FindMinecraftVersion(argument[(index + prefix.Length)..]);
        }

        return null;
    }

    private static string? FindLibraryMinecraftVersion(IEnumerable<string> libraries)
    {
        foreach (string library in libraries)
        {
            string[] coordinate = library.Split(':');
            if (coordinate.Length < 3)
                continue;

            string group = coordinate[0];
            string artifact = coordinate[1];
            string version = coordinate[2];
            if ((group.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase) ||
                 group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase)) &&
                (artifact.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
                 artifact.Equals("fmlloader", StringComparison.OrdinalIgnoreCase)))
            {
                int separator = version.IndexOf('-');
                string? minecraftVersion = FindMinecraftVersion(separator > 0 ? version[..separator] : version);
                if (minecraftVersion is not null)
                    return minecraftVersion;
            }

            if (group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase) &&
                artifact.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = version.Split('.', 3);
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) &&
                    int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
                {
                    return $"1.{minor.ToString(CultureInfo.InvariantCulture)}.{patch.ToString(CultureInfo.InvariantCulture)}";
                }
            }

            if (group.Equals("optifine", StringComparison.OrdinalIgnoreCase))
            {
                int separator = version.IndexOf('_');
                string? minecraftVersion = FindMinecraftVersion(separator > 0 ? version[..separator] : version);
                if (minecraftVersion is not null)
                    return minecraftVersion;
            }

            if (artifact.Equals("intermediary", StringComparison.OrdinalIgnoreCase) &&
                (group.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) ||
                 group.Contains("quiltmc", StringComparison.OrdinalIgnoreCase) ||
                 group.Contains("legacyfabric", StringComparison.OrdinalIgnoreCase)))
            {
                string? minecraftVersion = FindMinecraftVersion(version);
                if (minecraftVersion is not null)
                    return minecraftVersion;
            }
        }

        return null;
    }

    private static string? FindDownloadMinecraftVersion(IEnumerable<string> urls)
    {
        const string marker = "/mc/game/";
        foreach (string url in urls)
        {
            int markerIndex = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                continue;

            string remainder = url[(markerIndex + marker.Length)..];
            int separator = remainder.IndexOf('/');
            string? minecraftVersion = FindMinecraftVersion(separator >= 0 ? remainder[..separator] : remainder);
            if (minecraftVersion is not null)
                return minecraftVersion;
        }

        return null;
    }

    private static string? FindMinecraftVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        Match match = MinecraftVersionRegex().Match(value);
        return match.Success ? match.Value : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private readonly record struct VersionJsonNode(
        string Id,
        string ClientVersion,
        string InheritsFrom,
        string Jar,
        bool HasLogging,
        IReadOnlyList<string> Libraries,
        IReadOnlyList<string> LoaderSignals,
        IReadOnlyList<string> ArgumentValues,
        string PatchGameVersion,
        IReadOnlyList<string> DownloadUrls);

    [GeneratedRegex(
        @"(([1-9][0-9]w[0-9]{2}[a-g])|((1|[2-9][0-9])\.[0-9]+(\.[0-9]+)?(-(pre|rc|snapshot-?)[1-9]*| Pre-Release( [1-9])?)?))(_unobfuscated)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinecraftVersionRegex();
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
