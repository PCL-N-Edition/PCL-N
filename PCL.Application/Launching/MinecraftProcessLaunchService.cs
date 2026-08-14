// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PCL.Application.Minecraft.Assets;
using PCL.Application.Minecraft.Launch.Arguments;
using PCL.Application.Minecraft.Launch.Libraries;
using PCL.Application.Minecraft.Launch.Natives;
using PCL.Core.Logging;

namespace PCL.Application.Launching;

public sealed record MinecraftProcessLaunchRequest
{
    public required string VersionId { get; init; }
    public required string VersionJsonPath { get; init; }
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
    public string? AuthlibInjectorPath { get; init; }
    public string? AuthlibServer { get; init; }
    public string? AuthlibPrefetchedMetadata { get; init; }
    public bool UseExperimentalJvmHost { get; init; }
    public MinecraftJvmHostIdentityMode JvmHostIdentityMode { get; init; }
    public string? OfflineSkinSource { get; init; }
    public bool OfflineSkinSlim { get; init; }
    public MinecraftJvmIpPreference PreferredIpStack { get; init; }
    public string? Server { get; init; }
    public DateTimeOffset? ReleaseTime { get; init; }
    public bool HasOptiFine { get; init; }
    public string? WorldName { get; init; }
    public string LauncherName { get; init; } = "PCL-N";
    public string VersionType { get; init; } = "PCL-N";
    public bool UseSystemGlfw { get; init; }
}

public sealed record MinecraftProcessLaunchPlan(
    ProcessStartInfo StartInfo,
    string NativesDirectory,
    IReadOnlyList<string> ClasspathEntries,
    MinecraftNativeExtractionResult NativeExtraction)
{
    public MinecraftJvmHostRequest? JvmHostRequest { get; init; }
}

public static class MinecraftProcessLaunchService
{
    public static async Task<MinecraftProcessLaunchPlan> CreatePlanAsync(
        MinecraftProcessLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlayerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlayerUuid);

        PortableLog.Info("LaunchPlan", $"开始生成 {request.VersionId} 的进程启动计划。");
        PortableLog.Debug(
            "LaunchPlan",
            $"计划参数：VersionJson={request.VersionJsonPath}；Instance={request.InstanceDirectory}；Root={request.MinecraftRootDirectory}；" +
            $"Java={request.JavaExecutablePath}；JavaMajor={request.JavaMajorVersion}；MemoryMiB={request.MemoryMegabytes}；" +
            $"Window={request.Width}x{request.Height}；Fullscreen={request.Fullscreen}；Isolated={request.IsolatedGameDirectory}。");

        try
        {

        string versionJsonPath = Path.GetFullPath(request.VersionJsonPath);
        JsonObject versionJson = await ReadJsonObjectAsync(versionJsonPath, cancellationToken).ConfigureAwait(false);
        string minecraftRoot = Path.GetFullPath(request.MinecraftRootDirectory);
        string instanceDirectory = Path.GetFullPath(request.InstanceDirectory);
        IReadOnlyList<InheritedVersionJson> inheritedVersions = await ReadInheritedVersionJsonsAsync(
                versionJson,
                versionJsonPath,
                minecraftRoot,
                cancellationToken)
            .ConfigureAwait(false);
        JsonObject[] inheritedVersionJsons = inheritedVersions.Select(static version => version.Json).ToArray();

        string mainClass = FindString(versionJson, inheritedVersionJsons, "mainClass")
                           ?? throw new FormatException("version.json 缺少 mainClass。");
        string gameDirectory = request.IsolatedGameDirectory ? instanceDirectory : minecraftRoot;
        // WPF uses "{versionId}-natives". Prefer that if already populated (PCL2/CE installs).
        string nativesDirectory = ResolveNativesDirectory(instanceDirectory, request.VersionId);

        MinecraftArgumentRuleContext ruleContext = CreateRuleContext();
        List<MinecraftLibraryToken> libraries = ResolveLibraries(versionJson, inheritedVersionJsons, minecraftRoot, instanceDirectory, request.UseSystemGlfw);
        PortableLog.Info(
            "LaunchPlan",
            $"版本图解析完成：MainClass={mainClass}；继承层数={inheritedVersions.Count}；库数量={libraries.Count}；游戏目录={gameDirectory}。");
        if (request.UseSystemGlfw)
        {
            PortableLog.Info("LaunchPlan", "已启用使用系统 GLFW 选项，正在检查 GLFW 库处理...");
            int glfwLibraryCount = libraries.Count(lib => lib.NameWithoutVersion?.Contains("glfw", StringComparison.OrdinalIgnoreCase) == true);
            PortableLog.Info("LaunchPlan", $"检测到 {glfwLibraryCount} 个 GLFW 相关库");
        }

        // Extract both legacy IsNatives classifiers and modern artifact natives (name ends with :natives-os).
        // Modern natives stay on the classpath; we still pre-extract DLLs so -Djava.library.path is usable.
        string[] nativeArchives = libraries
            .Where(static library => File.Exists(library.LocalPath) &&
                                     (library.IsNatives || IsModernNativeLibrary(library.OriginalName)))
            .Select(static library => library.LocalPath)
            .Distinct(GetPathComparer())
            .ToArray();

        MinecraftNativeExtractionResult nativeExtraction = MinecraftNativeExtractionService.Extract(
            new MinecraftNativeExtractionRequest
            {
                ArchivePaths = nativeArchives,
                TargetDirectory = nativesDirectory,
                OperatingSystem = GetNativeOperatingSystem()
            });

        MinecraftClasspathPlan classpath = MinecraftClasspathPlanner.CreatePlan(
            new MinecraftClasspathPlanRequest
            {
                Libraries = libraries,
                ClasspathHeadEntries = request.ClasspathHeadEntries,
                BundledClasspathEntries = CreateBundledClasspathEntries(
                    versionJson,
                    versionJsonPath,
                    request.VersionId,
                    inheritedVersions,
                    minecraftRoot)
            });
        string classpathText = string.Join(Path.PathSeparator, classpath.Entries);
        string assetIndexName = MinecraftAssetIndexResolver.GetIndexName(
            new MinecraftAssetIndexNameRequest
            {
                VersionJson = versionJson,
                InheritedVersionJsons = inheritedVersionJsons
            });

        MinecraftLaunchPlanResult launchPlan = MinecraftLaunchPlanService.CreatePlan(
            new MinecraftLaunchPlanRequest
            {
                Jvm = new MinecraftJvmArgumentRequest
                {
                    VersionJson = versionJson,
                    InheritedVersionJsons = inheritedVersionJsons,
                    RuleContext = ruleContext,
                    MainClass = mainClass,
                    CustomJvmArguments = request.CustomJvmArguments,
                    MemoryMegabytes = request.MemoryMegabytes,
                    NativesDirectory = nativesDirectory,
                    JavaMajorVersion = request.JavaMajorVersion,
                    PreferredIpStack = request.PreferredIpStack,
                    PrefixArguments = CreateJvmPrefixArguments(request, nativesDirectory),
                    UseModernArguments = HasArguments(versionJson, inheritedVersionJsons, "jvm")
                },
                ModernGame = HasArguments(versionJson, inheritedVersionJsons, "game")
                    ? new MinecraftModernGameArgumentRequest
                    {
                        VersionJson = versionJson,
                        InheritedVersionJsons = inheritedVersionJsons,
                        RuleContext = ruleContext
                    }
                    : null,
                LegacyGame = FindString(versionJson, inheritedVersionJsons, "minecraftArguments") is { } minecraftArguments
                    ? new MinecraftLegacyGameArgumentRequest
                    {
                        MinecraftArguments = minecraftArguments
                    }
                    : null,
                Replacements = CreateReplacements(request, minecraftRoot, gameDirectory, nativesDirectory, classpathText, assetIndexName),
                JavaMajorVersion = request.JavaMajorVersion,
                Fullscreen = request.Fullscreen,
                CustomGameArguments = request.CustomGameArguments,
                WorldName = request.WorldName,
                Server = request.Server,
                ReleaseTime = request.ReleaseTime,
                HasOptiFine = request.HasOptiFine
            });

        EnsureMacOsJavaExecutable(request.JavaExecutablePath);
        ProcessStartInfo startInfo = new()
        {
            FileName = request.JavaExecutablePath,
            Arguments = launchPlan.Arguments,
            WorkingDirectory = gameDirectory,
            UseShellExecute = false
        };

        if (OperatingSystem.IsLinux())
        {
            SetupLinuxEnvironment(startInfo, request.UseSystemGlfw, libraries);
        }
            PortableLog.Info(
                "LaunchPlan",
                $"进程启动计划已生成；版本={request.VersionId}；Classpath={classpath.Entries.Count}；Natives={nativeArchives.Length}；主类={mainClass}。");
            PortableLog.Debug(
                "LaunchPlan",
                $"进程参数：FileName={startInfo.FileName}；WorkingDirectory={startInfo.WorkingDirectory}；Arguments={startInfo.Arguments}");
            MinecraftJvmHostRequest? jvmHostRequest = null;
            if (request.UseExperimentalJvmHost)
            {
                IReadOnlyList<string> launchTokens = ParseCommandLine(launchPlan.Arguments);
                jvmHostRequest = CreateJvmHostRequest(
                    request,
                    launchTokens,
                    mainClass,
                    gameDirectory,
                    classpath.Entries);
            }
            return new MinecraftProcessLaunchPlan(startInfo, nativesDirectory, classpath.Entries, nativeExtraction)
            {
                JvmHostRequest = jvmHostRequest
            };
        }
        catch (OperationCanceledException)
        {
            PortableLog.Warn("LaunchPlan", $"生成 {request.VersionId} 的进程启动计划时被取消。");
            throw;
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "LaunchPlan", $"生成 {request.VersionId} 的进程启动计划失败。");
            throw;
        }
    }

    private static Dictionary<string, string> CreateReplacements(
        MinecraftProcessLaunchRequest request,
        string minecraftRoot,
        string gameDirectory,
        string nativesDirectory,
        string classpath,
        string assetIndexName)
    {
        // NeoForge/Forge module path uses these (unquoted) — leaving them literal causes instant exit code 1.
        string libraryDirectory = Path.Combine(minecraftRoot, "libraries");
        string classpathSeparator = Path.PathSeparator.ToString();

        return new(StringComparer.Ordinal)
        {
            ["${natives_directory}"] = Quote(nativesDirectory),
            ["${launcher_name}"] = request.LauncherName,
            ["${launcher_version}"] = "Avalonia",
            ["${classpath}"] = Quote(classpath),
            ["${classpath_separator}"] = classpathSeparator,
            // Must NOT Quote — value is embedded mid-token: ${library_directory}/cpw/mods/...
            ["${library_directory}"] = libraryDirectory,
            ["${libraries_directory}"] = libraryDirectory,
            ["${auth_player_name}"] = request.PlayerName,
            ["${version_name}"] = request.VersionId,
            ["${game_directory}"] = Quote(gameDirectory),
            ["${assets_root}"] = Quote(Path.Combine(minecraftRoot, "assets")),
            ["${assets_index_name}"] = assetIndexName,
            ["${auth_uuid}"] = request.PlayerUuid.Replace("-", string.Empty, StringComparison.Ordinal),
            ["${auth_access_token}"] = request.AccessToken,
            ["${clientid}"] = Guid.NewGuid().ToString("N"),
            ["${auth_xuid}"] = "0",
            // Microsoft → msa; third-party Yggdrasil → mojang; offline → legacy.
            ["${user_type}"] = request.JvmHostIdentityMode switch
            {
                MinecraftJvmHostIdentityMode.ThirdParty => "mojang",
                MinecraftJvmHostIdentityMode.Offline => "legacy",
                _ => "msa"
            },
            ["${version_type}"] = request.VersionType,
            ["${resolution_width}"] = request.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["${resolution_height}"] = request.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["${quickPlayPath}"] = string.Empty,
            ["${quickPlaySingleplayer}"] = string.Empty,
            ["${quickPlayMultiplayer}"] = string.Empty,
            ["${quickPlayRealms}"] = string.Empty,
            // logging.client.argument uses ${path} for the downloaded log4j config; empty is safe (JVM default).
            ["${path}"] = string.Empty,
            ["${game_assets}"] = Quote(Path.Combine(minecraftRoot, "assets", "virtual", "legacy"))
        };
    }

    private static async Task<IReadOnlyList<InheritedVersionJson>> ReadInheritedVersionJsonsAsync(
        JsonObject versionJson,
        string versionJsonPath,
        string minecraftRoot,
        CancellationToken cancellationToken)
    {
        List<InheritedVersionJson> result = [];
        HashSet<string> seen = new(GetPathComparer())
        {
            Path.GetFullPath(versionJsonPath)
        };
        JsonObject current = versionJson;
        string currentJsonPath = versionJsonPath;
        for (int depth = 0; depth < 32; depth++)
        {
            string? inheritedId = EmptyToNull(current["inheritsFrom"]?.ToString())?.Trim();
            string? jar = EmptyToNull(current["jar"]?.ToString())?.Trim();
            if (MinecraftVersionFileResolver.IsLegacyLiteLoaderWithoutInheritance(
                    inheritedId,
                    jar,
                    EmptyToNull(current["id"]?.ToString())?.Trim(),
                    new DirectoryInfo(Path.GetDirectoryName(currentJsonPath) ?? string.Empty).Name,
                    current["logging"] is not null,
                    [current.ToJsonString()]))
            {
                inheritedId = jar;
            }

            if (inheritedId is null)
                return result;

            string? inheritedJsonPath = MinecraftVersionFileResolver.ResolveJsonPath(
                minecraftRoot,
                Path.GetDirectoryName(currentJsonPath),
                inheritedId);
            if (inheritedJsonPath is null)
            {
                string expectedPath = Path.Combine(
                    minecraftRoot,
                    "versions",
                    inheritedId,
                    inheritedId + ".json");
                throw new FileNotFoundException("缺少继承版本描述：" + inheritedId, expectedPath);
            }

            string normalizedPath = Path.GetFullPath(inheritedJsonPath);
            if (!seen.Add(normalizedPath))
                throw new FormatException("version.json 存在循环继承：" + inheritedId);

            JsonObject inheritedJson = await ReadJsonObjectAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
            result.Add(new InheritedVersionJson(inheritedId, normalizedPath, inheritedJson));
            current = inheritedJson;
            currentJsonPath = normalizedPath;
        }

        throw new FormatException("version.json 继承层数超过 32 层。");
    }

    private static List<MinecraftLibraryToken> ResolveLibraries(
        JsonObject versionJson,
        IReadOnlyList<JsonObject> inheritedVersionJsons,
        string minecraftRoot,
        string instanceDirectory,
        bool useSystemGlfw)
    {
        List<MinecraftLibraryToken> result = [];
        AddResolvedLibraries(result, versionJson, minecraftRoot, instanceDirectory, useSystemGlfw);
        foreach (JsonObject inheritedVersionJson in inheritedVersionJsons)
            AddResolvedLibraries(result, inheritedVersionJson, minecraftRoot, instanceDirectory, useSystemGlfw);

        List<MinecraftLibraryToken> deduplicated = [];
        HashSet<string> seen = new(GetPathComparer());
        foreach (MinecraftLibraryToken library in result)
        {
            if (seen.Add(library.LocalPath))
                deduplicated.Add(library);
        }

        return deduplicated;
    }

    private static void AddResolvedLibraries(
        List<MinecraftLibraryToken> target,
        JsonObject versionJson,
        string minecraftRoot,
        string instanceDirectory,
        bool useSystemGlfw)
    {
        target.AddRange(MinecraftLibraryResolver.Resolve(
            new MinecraftLibraryResolutionRequest
            {
                VersionJson = versionJson,
                MinecraftRootDirectory = minecraftRoot,
                TargetInstanceDirectory = instanceDirectory,
                OperatingSystem = GetLibraryOperatingSystem(),
                Is64BitArchitecture = Environment.Is64BitOperatingSystem,
                IsArm64Architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64,
                OperatingSystemVersion = Environment.OSVersion.VersionString,
                UseSystemGlfw = useSystemGlfw
            }));
    }

    private static List<string> CreateBundledClasspathEntries(
        JsonObject versionJson,
        string versionJsonPath,
        string versionId,
        IReadOnlyList<InheritedVersionJson> inheritedVersions,
        string minecraftRoot)
    {
        List<string> entries = [];
        HashSet<string> seen = new(GetPathComparer());
        AddVersionJar(entries, seen, versionJson, versionJsonPath, versionId, minecraftRoot);

        foreach (InheritedVersionJson inheritedVersion in inheritedVersions)
            AddVersionJar(
                entries,
                seen,
                inheritedVersion.Json,
                inheritedVersion.JsonPath,
                inheritedVersion.VersionId,
                minecraftRoot);

        return entries;
    }

    private static void AddVersionJar(
        List<string> entries,
        HashSet<string> seen,
        JsonObject versionJson,
        string versionJsonPath,
        string preferredVersionId,
        string minecraftRoot)
    {
        string? localDirectory = Path.GetDirectoryName(versionJsonPath);
        string? explicitJar = EmptyToNull(versionJson["jar"]?.ToString())?.Trim();
        if (explicitJar is not null)
        {
            string? explicitJarPath = MinecraftVersionFileResolver.ResolveJarPath(
                minecraftRoot,
                localDirectory,
                explicitJar);
            if (TryAddClasspathEntry(entries, seen, explicitJarPath))
                return;
        }

        string[] candidates =
        [
            preferredVersionId,
            Path.GetFileNameWithoutExtension(versionJsonPath),
            EmptyToNull(versionJson["id"]?.ToString())?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(localDirectory)
                ? string.Empty
                : new DirectoryInfo(localDirectory).Name
        ];
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string? jarPath = MinecraftVersionFileResolver.ResolveJarPath(
                minecraftRoot,
                localDirectory,
                candidate);
            if (TryAddClasspathEntry(entries, seen, jarPath))
                return;
        }
    }

    private static bool TryAddClasspathEntry(
        List<string> entries,
        HashSet<string> seen,
        string? path)
    {
        if (path is null || !File.Exists(path))
            return false;

        string normalizedPath = Path.GetFullPath(path);
        if (seen.Add(normalizedPath))
            entries.Add(normalizedPath);
        return true;
    }

    private static string? FindString(
        JsonObject versionJson,
        IReadOnlyList<JsonObject> inheritedVersionJsons,
        string propertyName)
    {
        string? value = EmptyToNull(versionJson[propertyName]?.ToString());
        if (value is not null)
            return value;

        foreach (JsonObject inheritedVersionJson in inheritedVersionJsons)
        {
            value = EmptyToNull(inheritedVersionJson[propertyName]?.ToString());
            if (value is not null)
                return value;
        }

        return null;
    }

    private static bool HasArguments(
        JsonObject versionJson,
        IReadOnlyList<JsonObject> inheritedVersionJsons,
        string argumentName)
    {
        if (versionJson["arguments"]?[argumentName] is not null)
            return true;

        return inheritedVersionJsons.Any(inheritedVersionJson => inheritedVersionJson["arguments"]?[argumentName] is not null);
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true);
        JsonNode? node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return node as JsonObject
               ?? throw new FormatException("JSON 根节点不是对象：" + path);
    }

    private static MinecraftArgumentRuleContext CreateRuleContext() => new()
    {
        OperatingSystem = GetArgumentOperatingSystem(),
        Architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => MinecraftArgumentArchitecture.X86,
            Architecture.X64 => MinecraftArgumentArchitecture.X64,
            Architecture.Arm64 => MinecraftArgumentArchitecture.Arm64,
            _ => MinecraftArgumentArchitecture.Unknown
        },
        OperatingSystemVersion = Environment.OSVersion.VersionString,
        EnableQuickPlayFeatureArguments = false
    };

    private static MinecraftArgumentOperatingSystem GetArgumentOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
            return MinecraftArgumentOperatingSystem.Win32;
        if (OperatingSystem.IsLinux())
            return MinecraftArgumentOperatingSystem.Linux;
        if (OperatingSystem.IsMacOS())
            return MinecraftArgumentOperatingSystem.MacOs;
        return MinecraftArgumentOperatingSystem.Unknown;
    }

    private static MinecraftLibraryOperatingSystem GetLibraryOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
            return MinecraftLibraryOperatingSystem.Win32;
        if (OperatingSystem.IsLinux())
            return MinecraftLibraryOperatingSystem.Linux;
        if (OperatingSystem.IsMacOS())
            return MinecraftLibraryOperatingSystem.MacOs;
        return MinecraftLibraryOperatingSystem.Unknown;
    }

    private static MinecraftNativeOperatingSystem GetNativeOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
            return MinecraftNativeOperatingSystem.Win32;
        if (OperatingSystem.IsLinux())
            return MinecraftNativeOperatingSystem.Linux;
        if (OperatingSystem.IsMacOS())
            return MinecraftNativeOperatingSystem.MacOs;
        return MinecraftNativeOperatingSystem.Unknown;
    }

    /// <summary>
    /// Prefer an already-populated WPF-style "{versionId}-natives" folder; otherwise use "natives".
    /// </summary>
    private static string ResolveNativesDirectory(string instanceDirectory, string versionId)
    {
        string versionNatives = Path.Combine(instanceDirectory, versionId + "-natives");
        if (Directory.Exists(versionNatives) &&
            Directory.EnumerateFiles(versionNatives, "*", SearchOption.AllDirectories).Any())
        {
            return versionNatives;
        }

        return Path.Combine(instanceDirectory, "natives");
    }

    /// <summary>
    /// Modern Mojang/NeoForge libraries use coordinates like "org.lwjgl:lwjgl:3.3.3:natives-windows"
    /// without a legacy "natives" JSON map.
    /// </summary>
    private static bool IsModernNativeLibrary(string? originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
            return false;

        string[] parts = originalName.Split(':');
        if (parts.Length < 4)
            return false;

        string classifier = parts[3];
        return classifier.StartsWith("natives-", StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string GetSystemLibraryPaths()
    {
        List<string> paths = [];

        string[] standardPaths =
        [
            "/usr/lib",
            "/usr/local/lib",
            "/lib/x86_64-linux-gnu",
            "/lib/aarch64-linux-gnu"
        ];

        foreach (string path in standardPaths)
        {
            if (Directory.Exists(path))
                paths.Add(path);
        }

        return paths.Count == 0 ? string.Empty : string.Join(Path.PathSeparator, paths);
    }

    internal static void SetupLinuxEnvironment(ProcessStartInfo startInfo, bool useSystemGlfw, IReadOnlyList<MinecraftLibraryToken> libraries)
    {
        bool isWayland = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.Equals("wayland", StringComparison.OrdinalIgnoreCase) == true;
        bool usesSdl3 = libraries.Any(static lib =>
            lib.NameWithoutVersion?.Equals("org.lwjgl:lwjgl-sdl", StringComparison.Ordinal) == true);

        if (isWayland)
        {
            if (usesSdl3)
            {
                PortableLog.Info("LaunchPlan", "检测到Wayland环境且版本使用SDL3，保留Wayland显示后端");
            }
            else
            {
                PortableLog.Info("LaunchPlan", "Wayland 环境，强制使用X11后端");
                startInfo.Environment["GDK_BACKEND"] = "x11";
                startInfo.Environment["SDL_VIDEODRIVER"] = "x11";
                startInfo.Environment["QT_QPA_PLATFORM"] = "xcb";
            }
        }

        if (useSystemGlfw)
        {
            string systemLibPaths = GetSystemLibraryPaths();
            if (!string.IsNullOrEmpty(systemLibPaths))
            {
                string currentLdPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                string newLdPath = string.IsNullOrEmpty(currentLdPath)
                    ? systemLibPaths
                    : systemLibPaths + Path.PathSeparator + currentLdPath;

                startInfo.Environment["LD_LIBRARY_PATH"] = newLdPath;
                PortableLog.Info("LaunchPlan", $"已设置 LD_LIBRARY_PATH: {newLdPath}");
            }
        }
    }

    internal static UnixFileMode AddExecutableBits(UnixFileMode current)
    {
        UnixFileMode result = current | UnixFileMode.UserExecute;
        if (current.HasFlag(UnixFileMode.GroupRead))
            result |= UnixFileMode.GroupExecute;
        if (current.HasFlag(UnixFileMode.OtherRead))
            result |= UnixFileMode.OtherExecute;
        return result;
    }

    private static void EnsureMacOsJavaExecutable(string javaExecutablePath)
    {
        if (!OperatingSystem.IsMacOS() ||
            !Path.IsPathRooted(javaExecutablePath) ||
            !File.Exists(javaExecutablePath))
        {
            return;
        }

        try
        {
            UnixFileMode current = File.GetUnixFileMode(javaExecutablePath);
            UnixFileMode executable = AddExecutableBits(current);
            if (executable == current)
                return;

            File.SetUnixFileMode(javaExecutablePath, executable);
            PortableLog.Info("LaunchPlan", "已修复 macOS Java 可执行权限：" + javaExecutablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                "macOS 无法为 Java 授予执行权限，请将 Java 安装到当前账户可写的位置：" +
                javaExecutablePath,
                ex);
        }
    }

    private static List<string> CreateJvmPrefixArguments(MinecraftProcessLaunchRequest request, string nativesDirectory)
    {
        List<string> prefix = [];

        if (request.UseSystemGlfw && OperatingSystem.IsLinux())
        {
            PortableLog.Info("LaunchPlan", "使用系统 GLFW，将添加系统库路径到 JVM 参数");
            string systemLibPaths = GetSystemLibraryPaths();
            if (!string.IsNullOrEmpty(systemLibPaths))
            {
                prefix.Add("-Djava.library.path=" + Quote(nativesDirectory) + Path.PathSeparator + systemLibPaths);
                prefix.Add("-Dorg.lwjgl.glfw.libname=glfw.3");
            }
        }

        if (string.IsNullOrWhiteSpace(request.AuthlibInjectorPath) ||
            string.IsNullOrWhiteSpace(request.AuthlibServer))
        {
            return prefix;
        }

        // Separate JVM options so Jvm.NET host / ParseCommandLine keep each flag intact.
        prefix.AddRange(
        [
            "-javaagent:" + Quote(request.AuthlibInjectorPath) + "=" + request.AuthlibServer,
            "-Dauthlibinjector.side=client"
        ]);
        if (!string.IsNullOrWhiteSpace(request.AuthlibPrefetchedMetadata))
        {
            prefix.Add(
                "-Dauthlibinjector.yggdrasil.prefetched=" +
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.AuthlibPrefetchedMetadata)));
        }

        return prefix;
    }

    private static MinecraftJvmHostRequest CreateJvmHostRequest(
        MinecraftProcessLaunchRequest request,
        IReadOnlyList<string> tokens,
        string mainClass,
        string gameDirectory,
        IReadOnlyList<string> classpathEntries)
    {
        int mainClassIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], mainClass, StringComparison.Ordinal))
            {
                mainClassIndex = i;
                break;
            }
        }

        if (mainClassIndex < 0)
            throw new FormatException("启动参数中未找到 Minecraft 主类：" + mainClass);

        List<string> vmArguments = NormalizeJvmHostVmArguments(tokens.Take(mainClassIndex).ToArray()).ToList();

        string[] gameArguments = tokens.Skip(mainClassIndex + 1).ToArray();
        return new MinecraftJvmHostRequest
        {
            JavaExecutablePath = request.JavaExecutablePath,
            JavaMajorVersion = request.JavaMajorVersion,
            WorkingDirectory = gameDirectory,
            MainClass = mainClass,
            PlayerName = request.PlayerName,
            PlayerUuid = request.PlayerUuid.Replace("-", string.Empty, StringComparison.Ordinal),
            AccessToken = request.AccessToken,
            VmArguments = vmArguments.ToArray(),
            ClasspathEntries = classpathEntries.ToArray(),
            GameArguments = gameArguments,
            IdentityMode = request.JvmHostIdentityMode,
            AuthServer = request.AuthlibServer,
            AuthServerMetadata = request.AuthlibPrefetchedMetadata,
            OfflineSkinSource = request.OfflineSkinSource,
            OfflineSkinSlim = request.OfflineSkinSlim
        };
    }

    /// <summary>
    /// Parses the quoting shape accepted by ProcessStartInfo. It follows the CRT
    /// backslash-before-quote rules and also accepts single quotes for custom Unix arguments.
    /// </summary>
    internal static IReadOnlyList<string> NormalizeJvmHostVmArguments(IReadOnlyList<string> tokens)
    {
        HashSet<string> pairedOptions = new(StringComparer.Ordinal)
        {
            "-p", "--module-path", "--upgrade-module-path", "--add-modules", "--limit-modules",
            "--add-reads", "--add-exports", "--add-opens", "--patch-module"
        };
        List<string> normalized = [];
        for (int index = 0; index < tokens.Count; index++)
        {
            string token = tokens[index];
            if (token is "-cp" or "-classpath" or "--class-path")
            {
                if (++index >= tokens.Count)
                    throw new FormatException($"JVM 参数 {token} 缺少 classpath 值。");
                continue;
            }
            if (token.StartsWith("-Djava.class.path=", StringComparison.Ordinal))
                continue;
            if (!pairedOptions.Contains(token))
            {
                normalized.Add(token);
                continue;
            }
            if (++index >= tokens.Count || string.IsNullOrWhiteSpace(tokens[index]) ||
                tokens[index][0] == '-')
            {
                throw new FormatException($"JVM 模块参数 {token} 缺少值。");
            }
            string canonicalName = token == "-p" ? "--module-path" : token;
            normalized.Add(canonicalName + "=" + tokens[index]);
        }
        return normalized;
    }

    internal static IReadOnlyList<string> ParseCommandLine(string commandLine)
    {
        List<string> result = [];
        int index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
                index++;
            if (index >= commandLine.Length)
                break;

            System.Text.StringBuilder token = new();
            bool quoted = false;
            bool tokenStarted = false;
            char quote = '\0';
            while (index < commandLine.Length)
            {
                char current = commandLine[index];
                if (!quoted && char.IsWhiteSpace(current))
                    break;

                if (current == '\\' && quote != '\'')
                {
                    int slashStart = index;
                    while (index < commandLine.Length && commandLine[index] == '\\')
                        index++;
                    int slashCount = index - slashStart;
                    if (index < commandLine.Length && commandLine[index] == '"')
                    {
                        token.Append('\\', slashCount / 2);
                        if ((slashCount & 1) != 0)
                        {
                            token.Append('"');
                            index++;
                        }
                        else
                        {
                            quoted = !quoted;
                            quote = quoted ? '"' : '\0';
                            index++;
                        }
                    }
                    else
                    {
                        token.Append('\\', slashCount);
                    }
                    tokenStarted = true;
                    continue;
                }

                if ((current == '"' || current == '\'') && (!quoted || quote == current))
                {
                    quoted = !quoted;
                    quote = quoted ? current : '\0';
                    tokenStarted = true;
                    index++;
                    continue;
                }

                token.Append(current);
                tokenStarted = true;
                index++;
            }

            if (quoted)
                throw new FormatException("启动参数包含未闭合的引号。");
            if (tokenStarted)
                result.Add(token.ToString());
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
                index++;
        }

        return result;
    }

    private sealed record InheritedVersionJson(string VersionId, string JsonPath, JsonObject Json);
}
