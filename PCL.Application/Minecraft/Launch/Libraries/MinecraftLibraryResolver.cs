// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Application.Minecraft.Launch.Arguments;
using PCL.Core.Logging;

namespace PCL.Application.Minecraft.Launch.Libraries;

public static class MinecraftLibraryResolver
{
    private const string MavenCentralBaseUrl = "https://repo1.maven.org/maven2/";
    private const string LinuxArm64Classifier = "natives-linux-arm64";
    private const string Lwjgl3Arm64FallbackVersion = "3.3.2";
    private const string Lwjgl2Arm64Coordinate = "org.glavo.hmcl:lwjgl2-natives:2.9.3-linux-arm64";
    private const string Lwjgl2Arm64Url =
        MavenCentralBaseUrl +
        "org/glavo/hmcl/lwjgl2-natives/2.9.3-linux-arm64/lwjgl2-natives-2.9.3-linux-arm64.jar";
    private const string Lwjgl2Arm64Sha1 = "c47df34b6a0414b2d9972f602d0c85191129d69c";
    private const long Lwjgl2Arm64Size = 7_346_768;

    private static readonly Dictionary<string, Lwjgl3LegacyModuleMetadata> LegacyLwjgl3Modules =
        new(StringComparer.Ordinal)
        {
            ["lwjgl"] = new(
                "4421d94af68e35dcaa31737a6fc59136a1e61b94",
                786_196,
                "8bd89332c90a90e6bc4aa997a25c05b7db02c90a",
                90_795),
            ["lwjgl-jemalloc"] = new(
                "877e17e39ebcd58a9c956dc3b5b777813de0873a",
                43_233,
                "5249f18a9ae20ea86c5816bc3107a888ce7a17d2",
                206_402),
            ["lwjgl-openal"] = new(
                "ae5357ed6d934546d3533993ea84c0cfb75eed95",
                108_230,
                "22408980cc579709feaf9acb807992d3ebcf693f",
                590_865),
            ["lwjgl-opengl"] = new(
                "ee8e95be0b438602038bc1f02dc5e3d011b1b216",
                928_871,
                "bb9eb56da6d1d549d6a767218e675e36bc568eb9",
                58_627),
            ["lwjgl-glfw"] = new(
                "757920418805fb90bfebb3d46b1d9e7669fca2eb",
                135_828,
                "bc49e64bae0f7ff103a312ee8074a34c4eb034c7",
                120_168),
            ["lwjgl-stb"] = new(
                "a2550795014d622b686e9caac50b14baa87d2c70",
                118_874,
                "11a380c37b0f03cb46db235e064528f84d736ff7",
                207_419),
            ["lwjgl-tinyfd"] = new(
                "9f65c248dd77934105274fcf8351abb75b34327c",
                13_404,
                "93f8c5bc1984963cd79109891fb5a9d1e580373e",
                43_381)
        };

    private static readonly Dictionary<string, Lwjgl3NativeMetadata> ModernLwjgl3Natives =
        new(StringComparer.Ordinal)
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
            ["lwjgl-vma:3.4.2"] = new("c6fb35dd852aedf2266d462c9b7142c41a860298", 60_868)
        };

    private static readonly HashSet<string> LegacyLwjgl2NativeCoordinates = new(StringComparer.Ordinal)
    {
        "org.lwjgl.lwjgl:lwjgl-platform:2.9.0",
        "org.lwjgl.lwjgl:lwjgl-platform:2.9.1",
        "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209"
    };

    private static readonly HashSet<string> UnsupportedLegacyNativeCoordinates = new(StringComparer.Ordinal)
    {
        "net.java.jinput:jinput-platform:2.0.5",
        "com.mojang:text2speech:1.10.3",
        "com.mojang:text2speech:1.11.3",
        "com.mojang:text2speech:1.12.4"
    };

    private static readonly HashSet<string> UnsupportedDirectNativeCoordinates = new(StringComparer.Ordinal)
    {
        "com.mojang:text2speech:1.13.9:natives-linux"
    };

    public static IReadOnlyList<MinecraftLibraryToken> Resolve(MinecraftLibraryResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftRootDirectory);

        JsonArray? libraries = request.VersionJson["libraries"]?.AsArray();
        if (libraries is null)
            return [];

        List<MinecraftLibraryToken> result = [];
        MinecraftArgumentRuleContext ruleContext = CreateRuleContext(request);
        string minecraftRoot = Path.GetFullPath(request.MinecraftRootDirectory);

        foreach (JsonNode? libraryNode in libraries)
        {
            if (libraryNode is null || libraryNode.GetValueKind() != JsonValueKind.Object)
                continue;

            JsonObject library = libraryNode.AsObject();
            if (!MinecraftLaunchArgumentService.IsRuleAllowed(library["rules"], ruleContext))
                continue;

            string? originalName = library["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(originalName))
                continue;

            bool isLocal = string.Equals(library["hint"]?.ToString(), "local", StringComparison.Ordinal);

            try
            {
                string? rootUrl = BuildRootUrl(library["url"]?.ToString(), originalName);
                if (library["natives"] is null)
                {
                    MinecraftLibraryToken artifact = ResolveArtifact(
                        library,
                        originalName,
                        rootUrl,
                        minecraftRoot,
                        request.TargetInstanceDirectory,
                        isLocal);
                    MinecraftLibraryToken? resolvedArtifact = ResolveArchitectureSpecificArtifact(
                        artifact,
                        minecraftRoot,
                        request);
                    if (resolvedArtifact is not null)
                        result.Add(resolvedArtifact);
                    continue;
                }

                MinecraftLibraryToken? nativeToken = ResolveNative(library, originalName, rootUrl, minecraftRoot, request, isLocal);
                if (nativeToken is not null)
                    result.Add(nativeToken);
            }
            catch (InvalidDataException exception)
            {
                PortableLog.Warn(
                    exception,
                    "MinecraftLibrary",
                    $"跳过包含非法下载路径的支持库：{originalName}。");
            }
        }

        return result;
    }

    public static string GetCoordinatePath(
        string coordinate,
        string minecraftRootDirectory,
        bool includeMinecraftRoot = true)
    {
        string[] parts = coordinate.Split(':');
        if (parts.Length < 3)
            throw new FormatException($"Invalid library coordinate: {coordinate}");

        ValidateCoordinatePart(parts[0], nameof(coordinate), allowDots: true);
        ValidateCoordinatePart(parts[1], nameof(coordinate), allowDots: false);
        ValidateCoordinatePart(parts[2], nameof(coordinate), allowDots: false);

        string relativePath = Path.Combine(
            parts[0].Replace('.', Path.DirectorySeparatorChar),
            parts[1],
            parts[2],
            parts[1] + "-" + parts[2] + ".jar");

        return includeMinecraftRoot
            ? Path.Combine(minecraftRootDirectory, "libraries", relativePath)
            : relativePath;
    }

    private static MinecraftLibraryToken ResolveArtifact(
        JsonObject library,
        string originalName,
        string? rootUrl,
        string minecraftRoot,
        string? targetInstanceDirectory,
        bool isLocal)
    {
        JsonNode? artifact = library["downloads"]?["artifact"];
        string localPath = isLocal && !string.IsNullOrWhiteSpace(targetInstanceDirectory)
            ? Path.Combine(targetInstanceDirectory, "libraries", GetLocalLibraryFileName(originalName))
            : GetCoordinatePath(originalName, minecraftRoot);

        if (artifact is not null)
        {
            localPath = artifact["path"] is null
                ? GetCoordinatePath(originalName, minecraftRoot)
                : ResolveManifestLibraryPath(minecraftRoot, artifact["path"]!.ToString());

            return CreateToken(
                originalName,
                localPath,
                rootUrl ?? EmptyToNull(artifact["url"]?.ToString()),
                EmptyToNull(artifact["sha1"]?.ToString()),
                ParseSize(artifact["size"]),
                isNatives: false,
                isLocal);
        }

        return CreateToken(originalName, localPath, rootUrl, sha1: null, size: 0, isNatives: false, isLocal);
    }

    private static MinecraftLibraryToken? ResolveNative(
        JsonObject library,
        string originalName,
        string? rootUrl,
        string minecraftRoot,
        MinecraftLibraryResolutionRequest request,
        bool isLocal)
    {
        string? nativeClassifier = GetNativeClassifier(library["natives"], request.OperatingSystem, request.Is64BitArchitecture);
        if (string.IsNullOrWhiteSpace(nativeClassifier))
            return null;

        JsonNode? classifier = library["downloads"]?["classifiers"]?[nativeClassifier];
        bool keepExistingArm64Classifier =
            request.OperatingSystem == MinecraftLibraryOperatingSystem.Linux &&
            request.IsArm64Architecture &&
            string.Equals(nativeClassifier, LinuxArm64Classifier, StringComparison.Ordinal) &&
            classifier is not null;
        if (!keepExistingArm64Classifier && TryResolveLinuxArm64Native(
                originalName,
                nativeClassifier,
                minecraftRoot,
                request,
                isLocal,
                out MinecraftLibraryToken? arm64Native))
        {
            return arm64Native;
        }

        if (classifier is null)
        {
            string fallbackKey = GetFallbackNativeClassifierKey(request.OperatingSystem);
            if (!string.Equals(fallbackKey, nativeClassifier, StringComparison.Ordinal))
                classifier = library["downloads"]?["classifiers"]?[fallbackKey];
        }

        if (classifier is not null)
        {
            string localPath = classifier["path"] is null
                ? GetNativeCoordinatePath(originalName, minecraftRoot, nativeClassifier)
                : ResolveManifestLibraryPath(minecraftRoot, classifier["path"]!.ToString());

            return CreateToken(
                originalName,
                localPath,
                rootUrl ?? EmptyToNull(classifier["url"]?.ToString()),
                EmptyToNull(classifier["sha1"]?.ToString()),
                ParseSize(classifier["size"]),
                isNatives: true,
                isLocal);
        }

        return CreateToken(
            originalName,
            GetNativeCoordinatePath(originalName, minecraftRoot, nativeClassifier),
            rootUrl,
            sha1: null,
            size: 0,
            isNatives: true,
            isLocal);
    }

    private static MinecraftLibraryToken CreateToken(
        string originalName,
        string localPath,
        string? url,
        string? sha1,
        long size,
        bool isNatives,
        bool isLocal) =>
        new()
        {
            OriginalName = originalName,
            NameWithoutVersion = GetNameWithoutVersion(originalName),
            Url = EmptyToNull(url),
            LocalPath = localPath,
            Sha1 = EmptyToNull(sha1),
            Size = size,
            IsNatives = isNatives,
            IsLocal = isLocal
        };

    private static MinecraftLibraryToken? ResolveArchitectureSpecificArtifact(
        MinecraftLibraryToken artifact,
        string minecraftRoot,
        MinecraftLibraryResolutionRequest request)
    {
        if (request.OperatingSystem != MinecraftLibraryOperatingSystem.Linux ||
            !request.IsArm64Architecture ||
            string.IsNullOrWhiteSpace(artifact.OriginalName))
        {
            return artifact;
        }

        if (UnsupportedDirectNativeCoordinates.Contains(artifact.OriginalName))
            return null;

        string[] coordinateParts = artifact.OriginalName.Split(':');
        if (artifact.IsLocal ||
            coordinateParts.Length is not (3 or 4) ||
            !string.Equals(coordinateParts[0], "org.lwjgl", StringComparison.Ordinal))
        {
            return artifact;
        }

        string module = coordinateParts[1];
        string version = coordinateParts[2];
        if (coordinateParts.Length == 3)
        {
            if (!IsLegacyLwjgl3Version(version) ||
                !LegacyLwjgl3Modules.TryGetValue(module, out Lwjgl3LegacyModuleMetadata metadata))
            {
                return artifact;
            }

            string replacementCoordinate = $"org.lwjgl:{module}:{Lwjgl3Arm64FallbackVersion}";
            string localPath = GetCoordinatePath(replacementCoordinate, minecraftRoot);
            return artifact with
            {
                OriginalName = replacementCoordinate,
                NameWithoutVersion = GetNameWithoutVersion(replacementCoordinate),
                LocalPath = localPath,
                Url = CreateMavenCentralUrl(localPath, minecraftRoot),
                Sha1 = metadata.ArtifactSha1,
                Size = metadata.ArtifactSize
            };
        }

        if (!string.Equals(coordinateParts[3], "natives-linux", StringComparison.Ordinal) ||
            !TryGetLwjgl3NativeReplacement(
                module,
                version,
                out string replacementVersion,
                out Lwjgl3NativeMetadata nativeMetadata))
        {
            return artifact;
        }

        coordinateParts[2] = replacementVersion;
        coordinateParts[3] = LinuxArm64Classifier;
        string arm64Coordinate = string.Join(':', coordinateParts);
        string arm64Path = GetNativeCoordinatePath(arm64Coordinate, minecraftRoot, LinuxArm64Classifier);

        return artifact with
        {
            OriginalName = arm64Coordinate,
            NameWithoutVersion = GetNameWithoutVersion(arm64Coordinate),
            LocalPath = arm64Path,
            Url = CreateMavenCentralUrl(arm64Path, minecraftRoot),
            Sha1 = nativeMetadata.Sha1,
            Size = nativeMetadata.Size
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
        if (request.OperatingSystem != MinecraftLibraryOperatingSystem.Linux ||
            !request.IsArm64Architecture ||
            isLocal)
        {
            return false;
        }

        if (!string.Equals(nativeClassifier, "natives-linux", StringComparison.Ordinal))
            return false;

        string[] coordinateParts = originalName.Split(':');
        if (coordinateParts.Length != 3)
            return false;

        string group = coordinateParts[0];
        string module = coordinateParts[1];
        string version = coordinateParts[2];
        if (string.Equals(group, "org.lwjgl", StringComparison.Ordinal) &&
            TryGetLwjgl3NativeReplacement(
                module,
                version,
                out string replacementVersion,
                out Lwjgl3NativeMetadata nativeMetadata))
        {
            coordinateParts[2] = replacementVersion;
            string arm64Coordinate = string.Join(':', coordinateParts) + ":" + LinuxArm64Classifier;
            string localPath = GetNativeCoordinatePath(arm64Coordinate, minecraftRoot, LinuxArm64Classifier);
            token = CreateToken(
                arm64Coordinate,
                localPath,
                CreateMavenCentralUrl(localPath, minecraftRoot),
                nativeMetadata.Sha1,
                nativeMetadata.Size,
                isNatives: false,
                isLocal: false);
            return true;
        }

        if (LegacyLwjgl2NativeCoordinates.Contains(originalName))
        {
            token = CreateToken(
                Lwjgl2Arm64Coordinate,
                GetCoordinatePath(Lwjgl2Arm64Coordinate, minecraftRoot),
                Lwjgl2Arm64Url,
                Lwjgl2Arm64Sha1,
                Lwjgl2Arm64Size,
                isNatives: true,
                isLocal: false);
            return true;
        }

        // The LWJGL 2 compatibility archive already contains libjinput. These
        // exact text-to-speech native versions have no Linux ARM64 build.
        return UnsupportedLegacyNativeCoordinates.Contains(originalName);
    }

    private static bool TryGetLwjgl3NativeReplacement(
        string module,
        string version,
        out string replacementVersion,
        out Lwjgl3NativeMetadata metadata)
    {
        replacementVersion = version;
        if (LegacyLwjgl3Modules.TryGetValue(module, out Lwjgl3LegacyModuleMetadata legacyMetadata) &&
            (IsLegacyLwjgl3Version(version) ||
             string.Equals(version, Lwjgl3Arm64FallbackVersion, StringComparison.Ordinal)))
        {
            replacementVersion = Lwjgl3Arm64FallbackVersion;
            metadata = new Lwjgl3NativeMetadata(legacyMetadata.NativeSha1, legacyMetadata.NativeSize);
            return true;
        }

        return ModernLwjgl3Natives.TryGetValue($"{module}:{version}", out metadata);
    }

    private static bool IsLegacyLwjgl3Version(string version) =>
        version is "3.1.6" or "3.2.1" or "3.2.2" or "3.3.1";

    private static string CreateMavenCentralUrl(string localPath, string minecraftRoot)
    {
        string relativePath = Path.GetRelativePath(
                Path.Combine(minecraftRoot, "libraries"),
                localPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return MavenCentralBaseUrl + relativePath;
    }

    private static MinecraftArgumentRuleContext CreateRuleContext(MinecraftLibraryResolutionRequest request) => new()
    {
        OperatingSystem = request.OperatingSystem switch
        {
            MinecraftLibraryOperatingSystem.Win32 => MinecraftArgumentOperatingSystem.Win32,
            MinecraftLibraryOperatingSystem.Linux => MinecraftArgumentOperatingSystem.Linux,
            MinecraftLibraryOperatingSystem.MacOs => MinecraftArgumentOperatingSystem.MacOs,
            _ => MinecraftArgumentOperatingSystem.Unknown
        },
        Architecture = request.IsArm64Architecture
            ? MinecraftArgumentArchitecture.Arm64
            : request.Is64BitArchitecture
                ? MinecraftArgumentArchitecture.X64
                : MinecraftArgumentArchitecture.X86,
        OperatingSystemVersion = request.OperatingSystemVersion,
        EnableQuickPlayFeatureArguments = false
    };

    private static string? BuildRootUrl(string? rootUrl, string coordinate)
    {
        rootUrl = EmptyToNull(rootUrl);
        return rootUrl is null
            ? null
            : rootUrl + GetCoordinatePath(coordinate, minecraftRootDirectory: string.Empty, includeMinecraftRoot: false)
                .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string? GetNativeClassifier(JsonNode? nativesNode, MinecraftLibraryOperatingSystem operatingSystem, bool is64Bit)
    {
        if (nativesNode is null)
            return null;

        string nativeKey = GetNativeOsKey(operatingSystem);
        return EmptyToNull(nativesNode[nativeKey]?.ToString())
            ?.Replace("${arch}", is64Bit ? "64" : "32", StringComparison.Ordinal);
    }

    private static string GetNativeOsKey(MinecraftLibraryOperatingSystem operatingSystem) =>
        operatingSystem switch
        {
            MinecraftLibraryOperatingSystem.Win32 => "windows",
            MinecraftLibraryOperatingSystem.Linux => "linux",
            MinecraftLibraryOperatingSystem.MacOs => "osx",
            _ => "unknown"
        };

    private static string GetFallbackNativeClassifierKey(MinecraftLibraryOperatingSystem operatingSystem) =>
        "natives-" + GetNativeOsKey(operatingSystem);

    private static string GetNativeCoordinatePath(string coordinate, string minecraftRoot, string classifier)
    {
        ValidateCoordinatePart(classifier, nameof(classifier), allowDots: false);
        string artifactPath = GetCoordinatePath(coordinate, minecraftRoot);
        return Path.ChangeExtension(artifactPath, null) + "-" + classifier + ".jar";
    }

    private static string ResolveManifestLibraryPath(string minecraftRoot, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new InvalidDataException("支持库下载路径不能为空。");

        try
        {
            string normalized = manifestPath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
                throw new InvalidDataException($"支持库下载路径不能为绝对路径：{manifestPath}");

            string librariesRoot = Path.GetFullPath(Path.Combine(minecraftRoot, "libraries"));
            string candidate = Path.GetFullPath(Path.Combine(librariesRoot, normalized));
            string rootPrefix = Path.TrimEndingDirectorySeparator(librariesRoot) + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(rootPrefix, comparison))
            {
                throw new InvalidDataException(
                    $"支持库下载路径不能位于 libraries 文件夹外：{manifestPath}");
            }

            return candidate;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidDataException($"支持库下载路径无效：{manifestPath}", exception);
        }
    }

    private static long ParseSize(JsonNode? node)
    {
        if (node is null)
            return 0;

        string value = node.ToString();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : 0;
    }

    private static string GetLocalLibraryFileName(string coordinate)
    {
        string[] parts = coordinate.Split(':');
        if (parts.Length < 3)
            throw new InvalidDataException($"支持库坐标无效：{coordinate}");
        ValidateCoordinatePart(parts[1], nameof(coordinate), allowDots: false);
        ValidateCoordinatePart(parts[2], nameof(coordinate), allowDots: false);
        return parts[1] + "-" + parts[2] + ".jar";
    }

    private static void ValidateCoordinatePart(
        string value,
        string parameterName,
        bool allowDots)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"支持库坐标包含非法路径片段：{value}");
        }

        if (allowDots && value.Split('.').Any(static segment => string.IsNullOrWhiteSpace(segment)))
            throw new InvalidDataException($"支持库坐标包含非法组名：{value}");
        if (!allowDots && value.Contains(Path.DirectorySeparatorChar))
            throw new ArgumentException("Invalid path segment.", parameterName);
    }

    private static string? GetNameWithoutVersion(string originalName)
    {
        string[] parts = originalName.Split(':');
        if (parts.Length < 3)
            return null;

        List<string> nameParts = [.. parts];
        nameParts.RemoveAt(2);
        return string.Join(':', nameParts);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private readonly record struct Lwjgl3LegacyModuleMetadata(
        string ArtifactSha1,
        long ArtifactSize,
        string NativeSha1,
        long NativeSize);

    private readonly record struct Lwjgl3NativeMetadata(string Sha1, long Size);
}
