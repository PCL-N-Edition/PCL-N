using System.Diagnostics;
using System.Text.Json.Nodes;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.ModLoaders;

namespace PCL.Services.Minecraft.Launch;

public enum MinecraftLaunchIdentityMode
{
    Offline,
    Microsoft,
    ThirdParty,
}

public sealed record MinecraftLaunchRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
    public required string VersionId { get; init; }
    public required string InstanceDirectory { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public required string PlayerName { get; init; }
    public required string PlayerUuid { get; init; }
    public string AccessToken { get; init; } = "0";
    public string JavaExecutablePath { get; init; } = "java";
    public int JavaMajorVersion { get; init; } = 17;
    public int MemoryMegabytes { get; init; } = 2048;
    public int Width { get; init; } = 854;
    public int Height { get; init; } = 480;
    public bool Fullscreen { get; init; }
    public bool IsolatedGameDirectory { get; init; }
    public string? CustomJvmArguments { get; init; }
    public string? CustomGameArguments { get; init; }
    public IReadOnlyList<string> ClasspathHeadEntries { get; init; } = [];

    /// <summary>
    /// The resolved client/version JAR. When omitted the planner derives
    /// <c>&lt;instance&gt;/&lt;versionId&gt;.jar</c>, and the derived path must exist.
    /// </summary>
    public string? ClientJarPath { get; init; }
    public string? AuthlibInjectorPath { get; init; }
    public string? AuthlibServer { get; init; }
    public string? AuthlibPrefetchedMetadata { get; init; }
    public MinecraftLaunchIdentityMode IdentityMode { get; init; }
    public string? Server { get; init; }
    public string? WorldName { get; init; }
    public DateTimeOffset? ReleaseTime { get; init; }
    public string? NativesDirectory { get; init; }
    public string LauncherName { get; init; } = "PCL-N";
    public string LauncherVersion { get; init; } = "2.0.0";
    public string VersionType { get; init; } = "PCL-N";
    public bool UseSystemGlfw { get; init; }
    public bool HasCleanroom { get; init; }
    public MinecraftLibraryOperatingSystem OperatingSystem { get; init; } = MinecraftLibraryOperatingSystem.Unknown;
    public string OperatingSystemVersion { get; init; } = string.Empty;
    public bool Is64BitArchitecture { get; init; } = true;
    public bool IsArm64Architecture { get; init; }
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
}

public sealed record MinecraftLaunchPlan(
    string JavaExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> ClasspathEntries,
    IReadOnlyList<MinecraftLibraryToken> Libraries,
    MinecraftModLoaderDescriptor ModLoader)
{
    /// <summary>The directory where native libraries must be extracted before launch.</summary>
    public string NativesDirectory { get; init; } = string.Empty;

    public ProcessStartInfo ToStartInfo()
    {
        ProcessStartInfo startInfo = new(JavaExecutablePath)
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in Arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}

public static class MinecraftLaunchPlanner
{
    public static MinecraftLaunchPlan CreatePlan(MinecraftLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlayerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlayerUuid);
        string root = Path.GetFullPath(request.MinecraftRootDirectory);
        string instance = Path.GetFullPath(request.InstanceDirectory);
        MinecraftModLoaderDescriptor loader = MinecraftModLoaderDetector.Detect(request.VersionJson);
        bool hasCleanroom = request.HasCleanroom || loader.Kind == MinecraftModLoaderKind.Cleanroom;
        JsonObject effectiveManifest = MergeManifests(request.VersionJson, request.InheritedVersionJsons);
        IReadOnlyList<MinecraftLibraryToken> libraries = MinecraftLibraryResolver.Resolve(new MinecraftLibraryResolutionRequest
        {
            VersionJson = effectiveManifest,
            MinecraftRootDirectory = root,
            TargetInstanceDirectory = instance,
            OperatingSystem = request.OperatingSystem,
            OperatingSystemVersion = request.OperatingSystemVersion,
            Is64BitArchitecture = request.Is64BitArchitecture,
            IsArm64Architecture = request.IsArm64Architecture,
            UseSystemGlfw = request.UseSystemGlfw,
        });
        if (!MinecraftVersionPaths.IsSafeReference(request.VersionId))
        {
            throw new InvalidDataException($"The version id is not a safe file name: {request.VersionId}");
        }

        string clientJar = Path.GetFullPath(request.ClientJarPath
            ?? Path.Combine(instance, request.VersionId + ".jar"));
        if (!File.Exists(clientJar))
        {
            throw new FileNotFoundException(
                "The Minecraft client JAR is missing; download it before launching.", clientJar);
        }

        MinecraftClasspathPlan classpath = MinecraftClasspathPlanner.CreatePlan(new MinecraftClasspathPlanRequest
        {
            Libraries = libraries,
            ClasspathHeadEntries = [clientJar, .. request.ClasspathHeadEntries],
            HasCleanroom = hasCleanroom,
        });
        string gameDirectory = request.IsolatedGameDirectory ? instance : root;
        string assetsRoot = Path.Combine(root, "assets");
        string assetsIndex = effectiveManifest["assetIndex"]?["id"]?.ToString() ?? effectiveManifest["assets"]?.ToString() ?? "legacy";
        string nativesDirectory = Path.GetFullPath(request.NativesDirectory ?? Path.Combine(instance, "natives"));
        string classpathSeparator = Path.PathSeparator.ToString();
        string libraryDirectory = Path.Combine(root, "libraries");
        string mainClass = effectiveManifest["mainClass"]?.ToString() ?? loader.MainClass ?? "net.minecraft.client.main.Main";
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(request.AuthlibInjectorPath))
        {
            string agent = "-javaagent:" + request.AuthlibInjectorPath;
            if (!string.IsNullOrWhiteSpace(request.AuthlibServer)) agent += "=" + request.AuthlibServer;
            args.Add(agent);
            if (!string.IsNullOrWhiteSpace(request.AuthlibPrefetchedMetadata))
            {
                string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.AuthlibPrefetchedMetadata));
                args.Add("-Dauthlibinjector.yggdrasil.prefetched=" + encoded);
            }
        }
        args.Add("-Xmx" + Math.Max(256, request.MemoryMegabytes) + "m");
        if (request.JavaMajorVersion >= 18) args.Add("-Dfile.encoding=COMPAT");
        AddVersionJvmArguments(args, effectiveManifest, request, gameDirectory, assetsRoot, assetsIndex, nativesDirectory, classpathSeparator, libraryDirectory);
        AddTokens(args, request.CustomJvmArguments);
        args.Add("-cp");
        args.Add(string.Join(Path.PathSeparator, classpath.Entries));
        args.Add(mainClass);

        List<string> gameArgs = ReadGameArguments(effectiveManifest, request);
        if (gameArgs.Count == 0) gameArgs = Tokenize(effectiveManifest["minecraftArguments"]?.ToString());
        foreach (string token in gameArgs)
        {
            string value = ReplaceToken(token, request, gameDirectory, assetsRoot, assetsIndex, nativesDirectory, classpathSeparator, libraryDirectory, request.LauncherVersion);
            if (value.Length > 0) args.Add(value);
        }
        if (!string.IsNullOrWhiteSpace(request.CustomGameArguments)) AddTokens(args, ReplaceToken(request.CustomGameArguments!, request, gameDirectory, assetsRoot, assetsIndex, nativesDirectory, classpathSeparator, libraryDirectory, request.LauncherVersion));
        if (gameArgs.Count == 0)
        {
            args.Add("--username"); args.Add(request.PlayerName);
            args.Add("--version"); args.Add(request.VersionId);
            args.Add("--gameDir"); args.Add(gameDirectory);
            args.Add("--assetsDir"); args.Add(assetsRoot);
            args.Add("--assetIndex"); args.Add(assetsIndex);
            args.Add("--uuid"); args.Add(request.PlayerUuid);
            args.Add("--accessToken"); args.Add(request.AccessToken);
            args.Add("--userType"); args.Add(request.IdentityMode == MinecraftLaunchIdentityMode.Offline ? "legacy" : "msa");
            args.Add("--versionType"); args.Add(request.VersionType);
        }
        if (request.Width > 0 && request.Height > 0)
        {
            args.Add("--width"); args.Add(request.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--height"); args.Add(request.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (request.Fullscreen) args.Add("--fullscreen");
        AddJoinArguments(args, request);
        EnsureNoUnresolvedTokens(args);
        return new MinecraftLaunchPlan(request.JavaExecutablePath, instance, args, classpath.Entries, libraries, loader)
        {
            NativesDirectory = nativesDirectory,
        };
    }

    private static List<string> ReadGameArguments(JsonObject json, MinecraftLaunchRequest request)
    {
        JsonArray? game = json["arguments"]?["game"]?.AsArray();
        if (game is null) return [];
        return ReadArgumentArray(game, request);
    }

    private static void AddVersionJvmArguments(
        List<string> target,
        JsonObject manifest,
        MinecraftLaunchRequest request,
        string gameDirectory,
        string assetsRoot,
        string assetsIndex,
        string nativesDirectory,
        string classpathSeparator,
        string libraryDirectory)
    {
        JsonArray? jvm = manifest["arguments"]?["jvm"]?.AsArray();
        if (jvm is null) return;
        foreach (string value in ReadArgumentArray(jvm, request))
        {
            string replaced = ReplaceToken(value, request, gameDirectory, assetsRoot, assetsIndex, nativesDirectory, classpathSeparator, libraryDirectory, request.LauncherVersion);
            if (replaced is "-cp" or "-classpath" || replaced.Contains(MinecraftLaunchPlanner.UnresolvedTokenMarker, StringComparison.Ordinal)) continue;
            if (replaced.Length > 0) target.Add(replaced);
        }
    }

    private static List<string> ReadArgumentArray(JsonArray array, MinecraftLaunchRequest request)
    {
        List<string> result = [];
        foreach (JsonNode? node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out string? text) && !string.IsNullOrEmpty(text))
            {
                result.Add(text);
                continue;
            }

            if (node is JsonObject conditional && conditional["value"] is JsonNode conditionalValue && IsRuleAllowed(conditional["rules"], request))
            {
                if (conditionalValue is JsonArray values)
                {
                    result.AddRange(values.Select(item => item?.ToString() ?? string.Empty).Where(static item => item.Length > 0));
                }
                else if (conditionalValue.ToString().Length > 0)
                {
                    result.Add(conditionalValue.ToString());
                }
            }
        }
        return result;
    }

    private static bool IsRuleAllowed(JsonNode? node, MinecraftLaunchRequest request)
    {
        if (node is not JsonArray rules || rules.Count == 0) return true;

        // Mojang evaluates rules in order: the LAST matching rule decides. No match means the
        // value is not used.
        bool allowed = false;
        bool matched = false;
        foreach (JsonObject rule in rules.OfType<JsonObject>())
        {
            if (!MatchesRule(rule, request)) continue;
            matched = true;
            allowed = !string.Equals(rule["action"]?.ToString(), "disallow", StringComparison.OrdinalIgnoreCase);
        }

        return matched && allowed;
    }

    private static bool MatchesRule(JsonObject rule, MinecraftLaunchRequest request)
    {
        if (rule["os"] is JsonObject os)
        {
            string currentOs = request.OperatingSystem switch
            {
                MinecraftLibraryOperatingSystem.Win32 => "windows",
                MinecraftLibraryOperatingSystem.Linux => "linux",
                MinecraftLibraryOperatingSystem.MacOs => "osx",
                _ => "unknown",
            };
            if (os["name"] is JsonNode name && !string.Equals(name.ToString(), currentOs, StringComparison.OrdinalIgnoreCase)) return false;
            if (os["arch"] is JsonNode arch)
            {
                string currentArch = request.IsArm64Architecture ? "arm64" : request.Is64BitArchitecture ? "x86_64" : "x86";
                string expected = arch.ToString().ToLowerInvariant();
                if (expected is "aarch64") expected = "arm64";
                if (expected is "amd64") expected = "x86_64";
                if (!string.Equals(expected, currentArch, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (os["version"] is JsonNode required && request.OperatingSystem == MinecraftLibraryOperatingSystem.Unknown) return false;
            // The manifest contract for os.version is a regular expression over the OS version.
            if (os["version"] is JsonNode version)
            {
                if (string.IsNullOrWhiteSpace(request.OperatingSystemVersion)) return false;
                if (!System.Text.RegularExpressions.Regex.IsMatch(request.OperatingSystemVersion, version.ToString())) return false;
            }
        }
        if (rule["features"] is JsonObject features)
        {
            foreach ((string name, JsonNode? expected) in features)
            {
                bool required = bool.TryParse(expected?.ToString(), out bool parsed) && parsed;
                // An absent feature participates as false.
                bool actual = request.Features.TryGetValue(name, out bool value) && value;
                if (actual != required) return false;
            }
        }
        return true;
    }

    private static JsonObject MergeManifests(JsonObject current, IReadOnlyList<JsonObject> inherited)
    {
        JsonObject result = new();
        foreach (JsonObject manifest in inherited.Reverse())
        {
            foreach ((string key, JsonNode? value) in manifest)
                result[key] = value?.DeepClone();
        }
        foreach ((string key, JsonNode? value) in current)
        {
            if (key is "libraries" or "arguments") continue;
            result[key] = value?.DeepClone();
        }

        JsonArray libraries = [];
        foreach (JsonObject manifest in inherited.Reverse())
            if (manifest["libraries"] is JsonArray parentLibraries) foreach (JsonNode? library in parentLibraries) libraries.Add(library?.DeepClone());
        if (current["libraries"] is JsonArray currentLibraries) foreach (JsonNode? library in currentLibraries) libraries.Add(library?.DeepClone());
        if (libraries.Count > 0) result["libraries"] = libraries;

        JsonObject arguments = new();
        foreach (JsonObject manifest in inherited.Reverse())
            if (manifest["arguments"] is JsonObject parentArguments)
                foreach ((string key, JsonNode? value) in parentArguments)
                    if (value is JsonArray parentArray) arguments[key] = parentArray.DeepClone();
        if (current["arguments"] is JsonObject currentArguments)
        {
            foreach ((string key, JsonNode? value) in currentArguments)
            {
                if (value is JsonArray currentArray && arguments[key] is JsonArray existing)
                {
                    JsonArray combined = [];
                    foreach (JsonNode? item in existing) combined.Add(item?.DeepClone());
                    foreach (JsonNode? item in currentArray) combined.Add(item?.DeepClone());
                    arguments[key] = combined;
                }
                else arguments[key] = value?.DeepClone();
            }
        }
        if (arguments.Count > 0) result["arguments"] = arguments;
        return result;
    }

    private static void AddJoinArguments(List<string> args, MinecraftLaunchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Server))
        {
            if (request.ReleaseTime is { } release && release >= new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero))
            {
                args.Add("--quickPlayMultiplayer");
                args.Add(request.Server!);
            }
            else
            {
                string[] split = request.Server!.Split(':', 2);
                args.Add("--server"); args.Add(split[0]);
                if (split.Length == 2 && int.TryParse(split[1], out _)) { args.Add("--port"); args.Add(split[1]); }
            }
        }
        if (!string.IsNullOrWhiteSpace(request.WorldName)) args.AddRange(["--quickPlaySingleplayer", request.WorldName!]);
    }

    private const string UnresolvedTokenMarker = "PCL-UNRESOLVED-TOKEN";

    private static string ReplaceToken(
        string value,
        MinecraftLaunchRequest request,
        string gameDirectory,
        string assetsRoot,
        string assetsIndex,
        string nativesDirectory,
        string classpathSeparator,
        string libraryDirectory,
        string launcherVersion)
    {
        string replaced = value
            .Replace("${auth_player_name}", request.PlayerName, StringComparison.Ordinal)
            .Replace("${version_name}", request.VersionId, StringComparison.Ordinal)
            .Replace("${game_directory}", gameDirectory, StringComparison.Ordinal)
            .Replace("${assets_root}", assetsRoot, StringComparison.Ordinal)
            .Replace("${assets_index_name}", assetsIndex, StringComparison.Ordinal)
            .Replace("${auth_uuid}", request.PlayerUuid, StringComparison.Ordinal)
            .Replace("${auth_access_token}", request.AccessToken, StringComparison.Ordinal)
            .Replace("${user_type}", request.IdentityMode == MinecraftLaunchIdentityMode.Offline ? "legacy" : "msa", StringComparison.Ordinal)
            .Replace("${version_type}", request.VersionType, StringComparison.Ordinal)
            .Replace("${resolution_width}", request.Width.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("${resolution_height}", request.Height.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("${launcher_name}", request.LauncherName, StringComparison.Ordinal)
            .Replace("${launcher_version}", launcherVersion, StringComparison.Ordinal)
            .Replace("${natives_directory}", nativesDirectory, StringComparison.Ordinal)
            .Replace("${classpath_separator}", classpathSeparator, StringComparison.Ordinal)
            .Replace("${library_directory}", libraryDirectory, StringComparison.Ordinal)
            .Replace("${user_properties}", "{}", StringComparison.Ordinal);
        if (replaced.Contains("${", StringComparison.Ordinal))
        {
            // Mark instead of throwing inside argument assembly: plan creation fails once the
            // full argv is known, so callers see the offending argument verbatim.
            replaced = replaced.Replace("${", UnresolvedTokenMarker, StringComparison.Ordinal);
        }

        return replaced;
    }

    private static void EnsureNoUnresolvedTokens(IReadOnlyList<string> arguments)
    {
        foreach (string argument in arguments)
        {
            if (argument.Contains(UnresolvedTokenMarker, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The launch arguments contain an unresolved ${...} token: "
                    + argument.Replace(UnresolvedTokenMarker, "${", StringComparison.Ordinal));
            }
        }
    }

    private static void AddTokens(List<string> target, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return;
        target.AddRange(Tokenize(commandLine));
    }

    private static List<string> Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        List<string> tokens = [];
        System.Text.StringBuilder current = new();
        bool quoted = false;
        foreach (char character in commandLine)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(character);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
