using System.Globalization;
using System.Text.Json;

namespace PCL.Services.Minecraft;

public static class MinecraftVersionPaths
{
    public static string? ResolveJsonPath(string minecraftRootDirectory, string? localDirectory, string versionReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        if (!IsSafeReference(versionReference)) return null;

        string root = Path.GetFullPath(minecraftRootDirectory);
        string versions = Path.Combine(root, "versions");
        string? preferred = FindDirectory(versions, versionReference);
        string? path = FindJsonByNameOrId(preferred, versionReference);
        if (path is not null) return path;
        path = FindJsonByNameOrId(localDirectory, versionReference);
        if (path is not null) return path;

        foreach (string directory in EnumerateDirectories(versions))
        {
            if (preferred is not null && PathsEqual(directory, preferred)) continue;
            path = FindJsonByNameOrId(directory, versionReference);
            if (path is not null) return path;
        }

        return null;
    }

    public static string? ResolveJarPath(string minecraftRootDirectory, string? localDirectory, string versionReference)
    {
        string? json = ResolveJsonPath(minecraftRootDirectory, localDirectory, versionReference);
        if (!IsSafeReference(versionReference)) return null;
        string root = Path.GetFullPath(minecraftRootDirectory);
        string versions = Path.Combine(root, "versions");
        string? preferred = FindDirectory(versions, versionReference);
        string? direct = FindFile(preferred, versionReference + ".jar") ?? FindFile(localDirectory, versionReference + ".jar");
        if (direct is not null) return direct;
        foreach (string directory in EnumerateDirectories(versions))
        {
            direct = FindFile(directory, versionReference + ".jar");
            if (direct is not null) return direct;
        }

        if (json is null) return null;
        string directoryPath = Path.GetDirectoryName(json) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(json);
        if (FindFile(directoryPath, name + ".jar") is { } byJsonName) return byJsonName;
        if (TryReadDescriptor(json, out string? id, out _) && IsSafeReference(id) && FindFile(directoryPath, id + ".jar") is { } byId) return byId;
        return FindFile(directoryPath, directoryPath.Length == 0 ? string.Empty : new DirectoryInfo(directoryPath).Name + ".jar");
    }

    public static bool IsSafeReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 180 && value is not "." and not ".." &&
        !Path.IsPathRooted(value) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);

    private static string? FindJsonByNameOrId(string? directory, string reference)
    {
        string? byName = FindFile(directory, reference + ".json");
        if (byName is not null) return byName;
        foreach (string path in EnumerateFiles(directory, "*.json"))
        {
            if (TryReadDescriptor(path, out string? id, out _)
                && string.Equals(id, reference, StringComparison.OrdinalIgnoreCase)) return path;
        }

        return null;
    }

    internal static bool TryReadDescriptor(string path, out string? id, out JsonElement root)
    {
        id = null;
        root = default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String)
                id = idElement.GetString();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string? FindPrimaryJson(string versionDirectory)
    {
        if (!Directory.Exists(versionDirectory)) return null;
        string name = new DirectoryInfo(versionDirectory).Name;
        string? conventional = FindFile(versionDirectory, name + ".json");
        if (conventional is not null) return conventional;
        string[] candidates = EnumerateFiles(versionDirectory, "*.json");
        if (candidates.Length == 0) return null;
        List<(string Path, string? Id, bool LooksLikeVersion)> parsed = [];
        foreach (string path in candidates)
        {
            if (!TryReadDescriptor(path, out string? id, out JsonElement root)) continue;
            bool looksLike = !string.IsNullOrWhiteSpace(id) || root.TryGetProperty("mainClass", out _) ||
                             root.TryGetProperty("inheritsFrom", out _) || root.TryGetProperty("jar", out _) ||
                             root.TryGetProperty("clientVersion", out _) || root.TryGetProperty("libraries", out _) || root.TryGetProperty("downloads", out _) ||
                             root.TryGetProperty("arguments", out _) || root.TryGetProperty("minecraftArguments", out _) || root.TryGetProperty("patches", out _);
            parsed.Add((path, id, looksLike));
        }

        return parsed.FirstOrDefault(candidate => string.Equals(candidate.Id, name, StringComparison.OrdinalIgnoreCase)).Path ??
               (parsed.Count == 1 ? parsed[0].Path : parsed.Count(static candidate => candidate.LooksLikeVersion) == 1
                   ? parsed.Single(static candidate => candidate.LooksLikeVersion).Path
                   : null);
    }

    private static string? FindDirectory(string parent, string name)
    {
        if (!Directory.Exists(parent)) return null;
        string exact = Path.Combine(parent, name);
        if (Directory.Exists(exact)) return exact;
        return EnumerateDirectories(parent).FirstOrDefault(path => string.Equals(new DirectoryInfo(path).Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindFile(string? directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || fileName.Length == 0) return null;
        string exact = Path.Combine(directory, fileName);
        if (File.Exists(exact)) return exact;
        return EnumerateFiles(directory, "*").FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] EnumerateDirectories(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetDirectories(directory).Order(StringComparer.OrdinalIgnoreCase).ToArray() : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string[] EnumerateFiles(string? directory, string pattern)
    {
        if (string.IsNullOrWhiteSpace(directory)) return [];
        try { return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern).Order(StringComparer.OrdinalIgnoreCase).ToArray() : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed class MinecraftVersionDiscovery
{
    private readonly string _versionsDirectoryName = "versions";

    public IReadOnlyList<MinecraftVersionDescriptor> Discover(string minecraftRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        string root = Path.GetFullPath(minecraftRootDirectory);
        string versionsDirectory = Path.Combine(root, _versionsDirectoryName);
        if (!Directory.Exists(versionsDirectory)) return [];

        List<MinecraftVersionDescriptor> result = [];
        string[] directories;
        try { directories = Directory.GetDirectories(versionsDirectory).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
        foreach (string directory in directories)
        {
            string? jsonPath = MinecraftVersionPaths.FindPrimaryJson(directory);
            if (jsonPath is null || !MinecraftVersionPaths.TryReadDescriptor(jsonPath, out string? jsonId, out JsonElement json)) continue;
            string id = string.IsNullOrWhiteSpace(jsonId) ? new DirectoryInfo(directory).Name : jsonId!;
            string type = ReadString(json, "type") ?? "custom";
            DateTimeOffset? release = ReadDate(json, "releaseTime");
            MinecraftVersionManifestEntry catalog = new(id, type, "file://" + jsonPath, release);
            result.Add(new MinecraftVersionDescriptor(
                id,
                directory,
                jsonPath,
                MinecraftVersionPaths.ResolveJarPath(root, directory, id),
                ReadString(json, "inheritsFrom"),
                ReadString(json, "mainClass"),
                release,
                MinecraftVersionClassifier.Classify(catalog)));
        }

        return result;
    }

    private static string? ReadString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static DateTimeOffset? ReadDate(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;
}
