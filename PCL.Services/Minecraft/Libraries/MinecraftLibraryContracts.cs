using System.Globalization;
using System.Text.Json.Nodes;

namespace PCL.Services.Minecraft.Libraries;

public enum MinecraftLibraryOperatingSystem
{
    Win32,
    Linux,
    MacOs,
    Unknown,
}

public sealed record MinecraftLibraryResolutionRequest
{
    public required JsonObject VersionJson { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public string? TargetInstanceDirectory { get; init; }
    public required MinecraftLibraryOperatingSystem OperatingSystem { get; init; }
    public bool Is64BitArchitecture { get; init; }
    public bool IsArm64Architecture { get; init; }
    public string OperatingSystemVersion { get; init; } = string.Empty;
    public bool UseSystemGlfw { get; init; }
}

public sealed record MinecraftLibraryToken
{
    public string? OriginalName { get; init; }
    public string? NameWithoutVersion { get; init; }
    public string? Url { get; init; }
    public required string LocalPath { get; init; }
    public string? Sha1 { get; init; }
    public long Size { get; init; }
    public bool IsNatives { get; init; }
    public bool IsLocal { get; init; }
}

public readonly record struct MinecraftLibraryNameFragment(string Value)
{
    public bool Matches(string? coordinate) => coordinate?.Contains(Value, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record MinecraftClasspathPlanRequest
{
    public required IReadOnlyList<MinecraftLibraryToken> Libraries { get; init; }
    public IReadOnlyList<string> ClasspathHeadEntries { get; init; } = [];
    public IReadOnlyList<string> BundledClasspathEntries { get; init; } = [];
    public bool HasCleanroom { get; init; }
}

public sealed record MinecraftClasspathPlan(IReadOnlyList<string> Entries);

public static class MinecraftClasspathRuleRegistry
{
    private static readonly MinecraftLibraryNameFragment[] CleanroomExclusions =
    [
        new("org.lwjgl.lwjgl:lwjgl:2.9.4"),
        new("net.java.dev.jna:platform:3.4.0"),
        new("com.ibm.icu:icu4j-core-mojang:51.2"),
    ];

    public static IReadOnlyList<MinecraftLibraryNameFragment> CleanroomExcludedLibraryFragments => CleanroomExclusions;
}

public static class MinecraftLibraryResolver
{
    private const string MavenCentralBaseUrl = "https://repo1.maven.org/maven2/";
    private const string LinuxArm64Classifier = "natives-linux-arm64";

    public static IReadOnlyList<MinecraftLibraryToken> Resolve(MinecraftLibraryResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftRootDirectory);
        JsonArray? libraries = request.VersionJson["libraries"]?.AsArray();
        if (libraries is null) return [];
        string root = Path.GetFullPath(request.MinecraftRootDirectory);
        List<MinecraftLibraryToken> result = [];
        foreach (JsonNode? node in libraries)
        {
            if (node is not JsonObject library) continue;
            string? coordinate = library["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(coordinate) || !IsRuleAllowed(library["rules"], request)) continue;
            bool local = string.Equals(library["hint"]?.ToString(), "local", StringComparison.OrdinalIgnoreCase);
            try
            {
                string? rootUrl = BuildRootUrl(library["url"]?.ToString(), coordinate);
                if (library["natives"] is JsonObject natives)
                {
                    string? classifier = GetNativeClassifier(natives, request);
                    if (classifier is null) continue;
                    JsonObject? classifierNode = library["downloads"]?["classifiers"]?[classifier]?.AsObject();
                    MinecraftLibraryToken token = CreateToken(
                        coordinate,
                        ResolveArtifactPath(root, classifierNode?["path"]?.ToString(), coordinate, classifier),
                        rootUrl ?? classifierNode?["url"]?.ToString(),
                        classifierNode?["sha1"]?.ToString(),
                        ParseSize(classifierNode?["size"]),
                        isNatives: !request.UseSystemGlfw || !IsGlfw(coordinate),
                        local);
                    result.Add(token);
                    continue;
                }

                JsonObject? artifact = library["downloads"]?["artifact"]?.AsObject();
                string localPath = local && !string.IsNullOrWhiteSpace(request.TargetInstanceDirectory)
                    ? Contained(Path.GetFullPath(request.TargetInstanceDirectory!), "libraries", GetLocalLibraryFileName(coordinate))
                    : ResolveArtifactPath(root, artifact?["path"]?.ToString(), coordinate, classifier: null);
                result.Add(CreateToken(
                    coordinate,
                    localPath,
                    rootUrl ?? artifact?["url"]?.ToString(),
                    artifact?["sha1"]?.ToString(),
                    ParseSize(artifact?["size"]),
                    isNatives: false,
                    local));
            }
            catch (InvalidDataException)
            {
                // A malformed optional library must not turn a valid version manifest into a
                // path traversal. It is omitted and the caller can report a missing artifact.
            }
        }

        return result;
    }

    public static string GetCoordinatePath(string coordinate, string minecraftRootDirectory, bool includeMinecraftRoot = true)
    {
        string[] parts = ParseCoordinate(coordinate);
        string relative = Path.Combine(parts[0].Replace('.', Path.DirectorySeparatorChar), parts[1], parts[2], parts[1] + "-" + parts[2] + ".jar");
        return includeMinecraftRoot ? Contained(Path.GetFullPath(minecraftRootDirectory), "libraries", relative) : relative;
    }

    public static string GetNativeCoordinatePath(string coordinate, string minecraftRootDirectory, string classifier)
    {
        ValidatePart(classifier, allowDots: false);
        string artifact = GetCoordinatePath(coordinate, minecraftRootDirectory);
        return Path.ChangeExtension(artifact, null) + "-" + classifier + ".jar";
    }

    private static MinecraftLibraryToken CreateToken(string coordinate, string path, string? url, string? sha1, long size, bool isNatives, bool isLocal) => new()
    {
        OriginalName = coordinate,
        NameWithoutVersion = GetNameWithoutVersion(coordinate),
        LocalPath = path,
        Url = Empty(url),
        Sha1 = Empty(sha1),
        Size = size,
        IsNatives = isNatives,
        IsLocal = isLocal,
    };

    private static string? GetNativeClassifier(JsonObject natives, MinecraftLibraryResolutionRequest request)
    {
        string key = request.OperatingSystem switch { MinecraftLibraryOperatingSystem.Win32 => "windows", MinecraftLibraryOperatingSystem.Linux => "linux", MinecraftLibraryOperatingSystem.MacOs => "osx", _ => "unknown" };
        string? classifier = natives[key]?.ToString();
        return string.IsNullOrWhiteSpace(classifier) ? null : classifier.Replace("${arch}", request.Is64BitArchitecture ? "64" : "32", StringComparison.Ordinal);
    }

    private static bool IsRuleAllowed(JsonNode? rulesNode, MinecraftLibraryResolutionRequest request)
    {
        if (rulesNode is not JsonArray rules || rules.Count == 0) return true;
        bool hasAllow = rules.OfType<JsonObject>().Any(rule => !string.Equals(rule["action"]?.ToString(), "disallow", StringComparison.OrdinalIgnoreCase));
        bool allowed = false;
        foreach (JsonNode? node in rules)
        {
            if (node is not JsonObject rule) continue;
            if (!RuleMatches(rule["os"]?.AsObject(), request)) continue;
            string action = rule["action"]?.ToString() ?? "allow";
            if (action.Equals("disallow", StringComparison.OrdinalIgnoreCase)) return false;
            allowed = true;
        }

        return !hasAllow || allowed;
    }

    private static bool RuleMatches(JsonObject? os, MinecraftLibraryResolutionRequest request)
    {
        if (os is null) return true;
        string current = request.OperatingSystem switch { MinecraftLibraryOperatingSystem.Win32 => "windows", MinecraftLibraryOperatingSystem.Linux => "linux", MinecraftLibraryOperatingSystem.MacOs => "osx", _ => "unknown" };
        if (os["name"] is JsonNode name && !string.Equals(name.ToString(), current, StringComparison.OrdinalIgnoreCase)) return false;
        if (os["arch"] is JsonNode arch)
        {
            string currentArch = request.IsArm64Architecture ? "arm64" : request.Is64BitArchitecture ? "x86_64" : "x86";
            if (!string.Equals(arch.ToString(), currentArch, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static string ResolveArtifactPath(string root, string? manifestPath, string coordinate, string? classifier)
    {
        if (!string.IsNullOrWhiteSpace(manifestPath)) return Contained(Path.Combine(root, "libraries"), NormalizeManifestPath(manifestPath));
        return classifier is null ? GetCoordinatePath(coordinate, root) : GetNativeCoordinatePath(coordinate, root, classifier);
    }

    private static string? BuildRootUrl(string? baseUrl, string coordinate)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        string relative = GetCoordinatePath(coordinate, string.Empty, includeMinecraftRoot: false).Replace(Path.DirectorySeparatorChar, '/');
        return baseUrl.TrimEnd('/') + "/" + relative;
    }

    private static string[] ParseCoordinate(string coordinate)
    {
        string[] parts = coordinate.Split(':');
        if (parts.Length < 3 || parts.Length > 4) throw new InvalidDataException("Invalid library coordinate.");
        ValidatePart(parts[0], allowDots: true);
        ValidatePart(parts[1], allowDots: false);
        ValidatePart(parts[2], allowDots: false);
        if (parts.Length == 4) ValidatePart(parts[3], allowDots: false);
        return parts;
    }

    private static void ValidatePart(string value, bool allowDots)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains('/') || value.Contains('\\') || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || allowDots && value.Split('.').Any(static part => part.Length == 0))
            throw new InvalidDataException("Library coordinate contains an unsafe path segment.");
    }

    private static string NormalizeManifestPath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Split('/').Any(static part => part is "" or "." or "..")) throw new InvalidDataException("Library manifest path escapes libraries.");
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string Contained(string root, params string[] parts)
    {
        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(parts).ToArray()));
        string prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison)) throw new InvalidDataException("Library path escapes its root.");
        return candidate;
    }

    private static string GetLocalLibraryFileName(string coordinate)
    {
        string[] parts = ParseCoordinate(coordinate);
        return parts[1] + "-" + parts[2] + ".jar";
    }

    private static string? GetNameWithoutVersion(string coordinate)
    {
        string[] parts = coordinate.Split(':');
        return parts.Length >= 3 ? string.Join(':', parts.Take(2).Concat(parts.Skip(3))) : null;
    }

    private static long ParseSize(JsonNode? node) => long.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) && size >= 0 ? size : 0;
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static bool IsGlfw(string coordinate) => GetNameWithoutVersion(coordinate) is "org.lwjgl:lwjgl-glfw" or "org.lwjgl.lwjgl:lwjgl-glfw";
}

public static class MinecraftClasspathPlanner
{
    public static MinecraftClasspathPlan CreatePlan(MinecraftClasspathPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> entries = request.BundledClasspathEntries.Where(static value => !string.IsNullOrWhiteSpace(value)).ToList();
        string? optiFine = null;
        foreach (MinecraftLibraryToken library in request.Libraries)
        {
            if (library.IsNatives || string.IsNullOrWhiteSpace(library.LocalPath)) continue;
            if (request.HasCleanroom && library.OriginalName is { } original && MinecraftClasspathRuleRegistry.CleanroomExcludedLibraryFragments.Any(fragment => fragment.Matches(original))) continue;
            if (string.Equals(library.NameWithoutVersion, "optifine:OptiFine", StringComparison.Ordinal)) { optiFine = library.LocalPath; continue; }
            entries.Add(library.LocalPath);
        }

        foreach (string head in request.ClasspathHeadEntries.Where(static value => !string.IsNullOrWhiteSpace(value))) entries.Insert(0, head);
        if (!string.IsNullOrWhiteSpace(optiFine)) entries.Insert(Math.Max(0, entries.Count - 2), optiFine);
        return new MinecraftClasspathPlan(entries);
    }
}
