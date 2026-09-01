using System.Globalization;
using System.Text.Json.Nodes;
using PCL.Services.Minecraft;

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
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
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
    private const string Lwjgl3Arm64FallbackVersion = "3.3.2";
    private const string Lwjgl2Arm64Coordinate = "org.glavo.hmcl:lwjgl2-natives:2.9.3-linux-arm64";
    private const string Lwjgl2Arm64Url = MavenCentralBaseUrl + "org/glavo/hmcl/lwjgl2-natives/2.9.3-linux-arm64/lwjgl2-natives-2.9.3-linux-arm64.jar";
    private const string Lwjgl2Arm64Sha1 = "c47df34b6a0414b2d9972f602d0c85191129d69c";
    private const long Lwjgl2Arm64Size = 7_346_768;

    private static readonly Dictionary<string, Lwjgl3LegacyModuleMetadata> LegacyLwjgl3Modules = new(StringComparer.Ordinal)
    {
        ["lwjgl"] = new("4421d94af68e35dcaa31737a6fc59136a1e61b94", 786_196, "8bd89332c90a90e6bc4aa997a25c05b7db02c90a", 90_795),
        ["lwjgl-jemalloc"] = new("877e17e39ebcd58a9c956dc3b5b777813de0873a", 43_233, "5249f18a9ae20ea86c5816bc3107a888ce7a17d2", 206_402),
        ["lwjgl-openal"] = new("ae5357ed6d934546d3533993ea84c0cfb75eed95", 108_230, "22408980cc579709feaf9acb807992d3ebcf693f", 590_865),
        ["lwjgl-opengl"] = new("ee8e95be0b438602038bc1f02dc5e3d011b1b216", 928_871, "bb9eb56da6d1d549d6a767218e675e36bc568eb9", 58_627),
        ["lwjgl-glfw"] = new("757920418805fb90bfebb3d46b1d9e7669fca2eb", 135_828, "bc49e64bae0f7ff103a312ee8074a34c4eb034c7", 120_168),
        ["lwjgl-stb"] = new("a2550795014d622b686e9caac50b14baa87d2c70", 118_874, "11a380c37b0f03cb46db235e064528f84d736ff7", 207_419),
        ["lwjgl-tinyfd"] = new("9f65c248dd77934105274fcf8351abb75b34327c", 13_404, "93f8c5bc1984963cd79109891fb5a9d1e580373e", 43_381),
    };

    private static readonly Dictionary<string, Lwjgl3NativeMetadata> ModernLwjgl3Natives = new(StringComparer.Ordinal)
    {
        ["lwjgl:3.3.3"] = new("f35d8b6ffe1ac1e3a5eb1d4e33de80f044ad5fd8", 91_294),
        ["lwjgl-freetype:3.3.3"] = new("498965aac06c4a0d42df1fbef6bacd05bde7f974", 1_093_516),
        ["lwjgl-glfw:3.3.3"] = new("492a0f11f85b85899a6568f07511160c1b87cd38", 122_159),
        ["lwjgl-jemalloc:3.3.3"] = new("eff8b86798191192fe2cba2dc2776109f30c239d", 209_315),
        ["lwjgl-openal:3.3.3"] = new("ad8f302118a65bb8d615f8a2a680db58fb8f835e", 592_963),
        ["lwjgl-opengl:3.3.3"] = new("2096f6b94b2d68745d858fbfe53aacf5f0c8074c", 58_625),
        ["lwjgl-stb:3.3.3"] = new("ddc177afc2be1ee8d93684b11363b80589a13fe1", 207_418),
        ["lwjgl-tinyfd:3.3.3"] = new("2823a8c955c758d0954d282888075019ef99cec7", 43_864),
        ["lwjgl:3.4.1"] = new("46883f3b622d8b4d7f27b627ca3360cda3db0e0e", 120_615),
        ["lwjgl-freetype:3.4.1"] = new("8f37d0da3386ff602ec54cd06626881895711041", 1_309_568),
        ["lwjgl-glfw:3.4.1"] = new("e5e87034c47118960746077dba46280e8de864b3", 141_530),
        ["lwjgl-jemalloc:3.4.1"] = new("7891964dfb723209c6d02b0401432348fb707cc0", 238_730),
        ["lwjgl-openal:3.4.1"] = new("3729b70cdd42df5571b075e051fa2fc8586dc538", 839_895),
        ["lwjgl-opengl:3.4.1"] = new("61a4103e56bbaeb74ad3f19ec14299fd6891c4b0", 79_752),
        ["lwjgl-sdl:3.4.1"] = new("b5a62eb82113b0227077fb270128487a80a20299", 1_434_288),
        ["lwjgl-shaderc:3.4.1"] = new("bc5c99b2eb7e2109c6db21cfced15a6851f8111b", 3_397_476),
        ["lwjgl-spvc:3.4.1"] = new("e2eaa8f516d43fe11b8253e6243b3d8e1315c9c8", 1_139_919),
        ["lwjgl-stb:3.4.1"] = new("3bc107f901f931fea07cb0d80b1d74a34b806a2b", 259_116),
        ["lwjgl-tinyfd:3.4.1"] = new("20771d2b4e01f5295156912ab62e170508aef618", 45_433),
        ["lwjgl-vma:3.4.1"] = new("de40d43c5947c4a4f91376af8f2c00e5396cc109", 59_715),
        ["lwjgl:3.4.2"] = new("a2229c542e410157bc79aead90243bd50dbcd79c", 125_841),
        ["lwjgl-freetype:3.4.2"] = new("b5fb3db06d1ecb15f54fabda6a4914ae933525ee", 1_319_451),
        ["lwjgl-glfw:3.4.2"] = new("943ec4651c874e9e0a59724976923c7bd8fceb9f", 144_523),
        ["lwjgl-jemalloc:3.4.2"] = new("8e51830cf9077fb0a89a6f43fa09df55c8c0e8ef", 235_933),
        ["lwjgl-openal:3.4.2"] = new("a3c98570beeffd241263e9bc63d8650147b33c4c", 866_040),
        ["lwjgl-opengl:3.4.2"] = new("52f6e847226e41f60773a62314cfada4f82b578c", 80_853),
        ["lwjgl-sdl:3.4.2"] = new("e723c33b467c5ff2af02942a1459db4e3686ce57", 1_455_063),
        ["lwjgl-shaderc:3.4.2"] = new("23403f5c295d946d1d2f7785c551f48be2b2fe9b", 3_463_790),
        ["lwjgl-spvc:3.4.2"] = new("8d7b46d68788d217143eee402126ea286d100683", 1_166_742),
        ["lwjgl-stb:3.4.2"] = new("30a261559033e22e7eb7f9a4ac1f2ea96d76f4d8", 258_999),
        ["lwjgl-tinyfd:3.4.2"] = new("03b72a19851c1e4fa0ad51a03f78d08f664afc91", 45_348),
        ["lwjgl-vma:3.4.2"] = new("c6fb35dd852aedf2266d462c9b7142c41a860298", 60_868),
    };

    private static readonly HashSet<string> LegacyLwjgl2NativeCoordinates = new(StringComparer.Ordinal)
    {
        "org.lwjgl.lwjgl:lwjgl-platform:2.9.0",
        "org.lwjgl.lwjgl:lwjgl-platform:2.9.1",
        "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209",
    };

    private static readonly HashSet<string> UnsupportedLegacyNativeCoordinates = new(StringComparer.Ordinal)
    {
        "net.java.jinput:jinput-platform:2.0.5",
        "com.mojang:text2speech:1.10.3",
        "com.mojang:text2speech:1.11.3",
        "com.mojang:text2speech:1.12.4",
    };

    private static readonly HashSet<string> UnsupportedDirectNativeCoordinates = new(StringComparer.Ordinal)
    {
        "com.mojang:text2speech:1.13.9:natives-linux",
    };

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
            if (string.IsNullOrWhiteSpace(coordinate) || !MinecraftRuleEvaluator.IsAllowed(
                    library["rules"],
                    MinecraftRuleContext.From(
                        request.OperatingSystem,
                        request.OperatingSystemVersion,
                        request.Is64BitArchitecture,
                        request.IsArm64Architecture,
                        request.Features))) continue;
            bool local = string.Equals(library["hint"]?.ToString(), "local", StringComparison.OrdinalIgnoreCase);
            try
            {
                string? declaredClassifier = GetCoordinateClassifier(coordinate);
                JsonObject? natives = library["natives"] as JsonObject;
                JsonObject? artifact = library["downloads"]?["artifact"]?.AsObject();

                // A Mojang library entry can describe both the ordinary artifact and one or
                // more platform classifiers. Resolve the ordinary artifact independently so
                // the native classifier does not swallow the classpath dependency.
                if (artifact is not null || natives is null)
                {
                    string? rootUrl = BuildRootUrl(library["url"]?.ToString(), coordinate);
                    string localPath = local && !string.IsNullOrWhiteSpace(request.TargetInstanceDirectory)
                        ? Contained(Path.GetFullPath(request.TargetInstanceDirectory!), "libraries", GetLocalLibraryFileName(coordinate))
                        : ResolveArtifactPath(root, artifact?["path"]?.ToString(), coordinate, declaredClassifier);
                    MinecraftLibraryToken artifactToken = CreateToken(
                        coordinate,
                        localPath,
                        rootUrl ?? artifact?["url"]?.ToString(),
                        artifact?["sha1"]?.ToString(),
                        ParseSize(artifact?["size"]),
                        isNatives: IsNativeClassifier(declaredClassifier),
                        local);
                    MinecraftLibraryToken? resolvedArtifact = ResolveArchitectureSpecificArtifact(artifactToken, root, request);
                    if (resolvedArtifact is not null) result.Add(resolvedArtifact);
                }

                MinecraftLibraryToken? nativeToken = ResolveNativeToken(coordinate, library, natives, root, request, local);
                if (nativeToken is not null) result.Add(nativeToken);
            }
            catch (InvalidDataException)
            {
                // A malformed optional library must not turn a valid version manifest into a
                // path traversal. It is omitted and the caller can report a missing artifact.
            }
        }

        return result;
    }

    private static MinecraftLibraryToken? ResolveNativeToken(
        string coordinate,
        JsonObject library,
        JsonObject? natives,
        string minecraftRoot,
        MinecraftLibraryResolutionRequest request,
        bool isLocal)
    {
        if (natives is null) return null;
        string? classifier = GetNativeClassifier(natives, request);
        if (classifier is null) return null;

        JsonObject? classifierNode = library["downloads"]?["classifiers"]?[classifier]?.AsObject();
        bool hasArm64Classifier = request.OperatingSystem == MinecraftLibraryOperatingSystem.Linux &&
            request.IsArm64Architecture &&
            string.Equals(classifier, LinuxArm64Classifier, StringComparison.Ordinal) &&
            classifierNode is not null;
        if (!hasArm64Classifier && TryResolveLinuxArm64Native(coordinate, classifier, minecraftRoot, request, isLocal, out MinecraftLibraryToken? arm64Native))
            return arm64Native;

        if (classifierNode is null)
        {
            string fallbackKey = "natives-" + GetNativeOsKey(request.OperatingSystem);
            if (!string.Equals(fallbackKey, classifier, StringComparison.Ordinal))
                classifierNode = library["downloads"]?["classifiers"]?[fallbackKey]?.AsObject();
        }

        if (request.OperatingSystem == MinecraftLibraryOperatingSystem.Linux &&
            request.IsArm64Architecture &&
            UnsupportedLegacyNativeCoordinates.Contains(coordinate)) return null;

        string nativeUrlCoordinate = GetCoordinateClassifier(coordinate) is null
            ? coordinate + ":" + classifier
            : coordinate;
        MinecraftLibraryToken token = CreateToken(
            coordinate,
            ResolveArtifactPath(minecraftRoot, classifierNode?["path"]?.ToString(), coordinate, classifier),
            BuildRootUrl(library["url"]?.ToString(), nativeUrlCoordinate) ?? classifierNode?["url"]?.ToString(),
            classifierNode?["sha1"]?.ToString(),
            ParseSize(classifierNode?["size"]),
            isNatives: !request.UseSystemGlfw || !IsGlfw(coordinate),
            isLocal);
        return token;
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

    private static MinecraftLibraryToken? ResolveArchitectureSpecificArtifact(
        MinecraftLibraryToken artifact,
        string minecraftRoot,
        MinecraftLibraryResolutionRequest request)
    {
        if (request.OperatingSystem != MinecraftLibraryOperatingSystem.Linux ||
            !request.IsArm64Architecture ||
            string.IsNullOrWhiteSpace(artifact.OriginalName)) return artifact;
        if (UnsupportedDirectNativeCoordinates.Contains(artifact.OriginalName)) return null;

        string[] parts = artifact.OriginalName.Split(':');
        if (artifact.IsLocal || parts.Length is not (3 or 4) || !string.Equals(parts[0], "org.lwjgl", StringComparison.Ordinal)) return artifact;
        string module = parts[1];
        string version = parts[2];
        if (parts.Length == 3)
        {
            if (!IsLegacyLwjgl3Version(version) || !LegacyLwjgl3Modules.TryGetValue(module, out Lwjgl3LegacyModuleMetadata metadata)) return artifact;
            string replacement = $"org.lwjgl:{module}:{Lwjgl3Arm64FallbackVersion}";
            string path = GetCoordinatePath(replacement, minecraftRoot);
            return artifact with
            {
                OriginalName = replacement,
                NameWithoutVersion = GetNameWithoutVersion(replacement),
                LocalPath = path,
                Url = CreateMavenCentralUrl(path, minecraftRoot),
                Sha1 = metadata.ArtifactSha1,
                Size = metadata.ArtifactSize,
            };
        }

        if (!string.Equals(parts[3], "natives-linux", StringComparison.Ordinal) || !TryGetLwjgl3NativeReplacement(module, version, out string replacementVersion, out Lwjgl3NativeMetadata nativeMetadata)) return artifact;
        parts[2] = replacementVersion;
        parts[3] = LinuxArm64Classifier;
        string coordinate = string.Join(':', parts);
        string nativePath = GetNativeCoordinatePath(coordinate, minecraftRoot, LinuxArm64Classifier);
        return artifact with
        {
            OriginalName = coordinate,
            NameWithoutVersion = GetNameWithoutVersion(coordinate),
            LocalPath = nativePath,
            Url = CreateMavenCentralUrl(nativePath, minecraftRoot),
            Sha1 = nativeMetadata.Sha1,
            Size = nativeMetadata.Size,
            IsNatives = true,
        };
    }

    private static bool TryResolveLinuxArm64Native(
        string originalName,
        string nativeClassifier,
        string minecraftRoot,
        MinecraftLibraryResolutionRequest request,
        bool isLocal,
        out MinecraftLibraryToken? token)
    {
        token = null;
        if (request.OperatingSystem != MinecraftLibraryOperatingSystem.Linux || !request.IsArm64Architecture || isLocal || !string.Equals(nativeClassifier, "natives-linux", StringComparison.Ordinal)) return false;
        string[] parts = originalName.Split(':');
        if (parts.Length != 3) return false;
        if (string.Equals(parts[0], "org.lwjgl", StringComparison.Ordinal) && TryGetLwjgl3NativeReplacement(parts[1], parts[2], out string replacementVersion, out Lwjgl3NativeMetadata metadata))
        {
            parts[2] = replacementVersion;
            string coordinate = string.Join(':', parts) + ":" + LinuxArm64Classifier;
            string path = GetNativeCoordinatePath(coordinate, minecraftRoot, LinuxArm64Classifier);
            token = CreateToken(coordinate, path, CreateMavenCentralUrl(path, minecraftRoot), metadata.Sha1, metadata.Size, isNatives: true, isLocal: false);
            return true;
        }
        if (LegacyLwjgl2NativeCoordinates.Contains(originalName))
        {
            token = CreateToken(Lwjgl2Arm64Coordinate, GetCoordinatePath(Lwjgl2Arm64Coordinate, minecraftRoot), Lwjgl2Arm64Url, Lwjgl2Arm64Sha1, Lwjgl2Arm64Size, isNatives: true, isLocal: false);
            return true;
        }
        return UnsupportedLegacyNativeCoordinates.Contains(originalName);
    }

    private static bool TryGetLwjgl3NativeReplacement(string module, string version, out string replacementVersion, out Lwjgl3NativeMetadata metadata)
    {
        replacementVersion = version;
        if (LegacyLwjgl3Modules.TryGetValue(module, out Lwjgl3LegacyModuleMetadata legacy) && (IsLegacyLwjgl3Version(version) || string.Equals(version, Lwjgl3Arm64FallbackVersion, StringComparison.Ordinal)))
        {
            replacementVersion = Lwjgl3Arm64FallbackVersion;
            metadata = new Lwjgl3NativeMetadata(legacy.NativeSha1, legacy.NativeSize);
            return true;
        }
        return ModernLwjgl3Natives.TryGetValue($"{module}:{version}", out metadata);
    }

    private static bool IsLegacyLwjgl3Version(string version) => version is "3.1.6" or "3.2.1" or "3.2.2" or "3.3.1";

    private static string CreateMavenCentralUrl(string localPath, string minecraftRoot)
    {
        string relative = Path.GetRelativePath(Path.Combine(minecraftRoot, "libraries"), localPath)
            .Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        return MavenCentralBaseUrl + relative;
    }

    private static string? GetNativeClassifier(JsonObject natives, MinecraftLibraryResolutionRequest request)
    {
        string key = GetNativeOsKey(request.OperatingSystem);
        string? classifier = natives[key]?.ToString();
        return string.IsNullOrWhiteSpace(classifier) ? null : classifier.Replace("${arch}", request.Is64BitArchitecture ? "64" : "32", StringComparison.Ordinal);
    }

    private static string GetNativeOsKey(MinecraftLibraryOperatingSystem operatingSystem) => operatingSystem switch
    {
        MinecraftLibraryOperatingSystem.Win32 => "windows",
        MinecraftLibraryOperatingSystem.Linux => "linux",
        MinecraftLibraryOperatingSystem.MacOs => "osx",
        _ => "unknown",
    };

    private static string ResolveArtifactPath(string root, string? manifestPath, string coordinate, string? classifier)
    {
        if (!string.IsNullOrWhiteSpace(manifestPath)) return Contained(Path.Combine(root, "libraries"), NormalizeManifestPath(manifestPath));
        return classifier is null ? GetCoordinatePath(coordinate, root) : GetNativeCoordinatePath(coordinate, root, classifier);
    }

    private static string? BuildRootUrl(string? baseUrl, string coordinate)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        string relative = GetCoordinatePath(coordinate, string.Empty, includeMinecraftRoot: false).Replace(Path.DirectorySeparatorChar, '/');
        string? classifier = GetCoordinateClassifier(coordinate);
        if (IsNativeClassifier(classifier)) relative = Path.ChangeExtension(relative, null)!.Replace(Path.DirectorySeparatorChar, '/') + "-" + classifier + ".jar";
        return baseUrl.TrimEnd('/') + "/" + relative;
    }

    private static string? GetCoordinateClassifier(string coordinate)
    {
        string[] parts = coordinate.Split(':');
        return parts.Length == 4 ? parts[3] : null;
    }

    private static bool IsNativeClassifier(string? classifier) =>
        classifier?.StartsWith("natives-", StringComparison.OrdinalIgnoreCase) == true;

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

    private readonly record struct Lwjgl3LegacyModuleMetadata(string ArtifactSha1, long ArtifactSize, string NativeSha1, long NativeSize);
    private readonly record struct Lwjgl3NativeMetadata(string Sha1, long Size);
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
