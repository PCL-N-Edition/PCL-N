// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PCL.Application.Instances;
using PCL.Application.Launching;
using PCL.Application.Minecraft.Launch;
using PCL.Application.Minecraft.Launch.Arguments;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Features.Shared;
using PCL.Platform.Java;
using PCL.Platform.Paths;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Builds <see cref="MinecraftProcessLaunchPlan"/> and related pure launch helpers
/// formerly nested as private statics on MainWindow.
/// </summary>
internal static class MinecraftLaunchPlanFactory
{
    public static async Task<MinecraftProcessLaunchPlan> CreateAsync(
        LaunchInstanceInfo instance,
        LoginProfileInfo profile,
        string javaExecutablePath,
        CancellationToken cancellationToken,
        string? worldName = null,
        InstanceMetadata? metadataOverride = null,
        string? serverAddress = null)
    {
        InstanceMetadata metadata = metadataOverride ??
            await InstanceMetadataStore.LoadAsync(instance.InstanceDirectory, cancellationToken).ConfigureAwait(false);
        // Never hit settings store synchronously on the launch path (disk IO hitch).
        LauncherSettings settings = await Task.Run(
                LauncherSettingsPageBinder.LoadSettings,
                cancellationToken)
            .ConfigureAwait(false);
        bool useJvmHost = ShouldUseJvmHostForProfile(
            settings.GetBooleanOption(
                LauncherSettingKeys.ExperimentalJvmLifecycleHost,
                LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalJvmLifecycleHost.Value)),
            profile.Kind);
        int windowType = GetIntegerOption(settings, LauncherSettingKeys.LaunchArgumentWindowType, 1);
        (int width, int height) = GetWindowSize(settings);
        (string? authlibPath, string? authlibServer, string? authlibMetadata) =
            await ResolveAuthlibLaunchOptionsAsync(profile, useJvmHost, cancellationToken).ConfigureAwait(false);
        int javaMajorVersion = await ResolveJavaMajorVersionAsync(javaExecutablePath, cancellationToken)
            .ConfigureAwait(false);

        if (profile.Kind is LaunchLoginProfileKind.NCloud or LaunchLoginProfileKind.LittleSkin)
        {
            // These providers must never launch against Mojang sessionserver alone —
            // skins, capes, and join-server checks all depend on Authlib Injector.
            if (string.IsNullOrWhiteSpace(authlibPath) ||
                string.IsNullOrWhiteSpace(authlibServer) ||
                string.IsNullOrWhiteSpace(authlibMetadata))
            {
                throw new InvalidOperationException(
                    profile.Kind == LaunchLoginProfileKind.NCloud
                        ? "N Cloud 启动需要 Authlib Injector 与认证服务器元数据（API），但未能准备完整。请重新登录 N Cloud 后重试。"
                        : "LittleSkin 启动需要 Authlib Injector 与认证服务器元数据（API），但未能准备完整。请重新登录后重试。");
            }

            PortableLog.Info(
                "LaunchPlan",
                $"已为 {profile.Kind} 启用 Authlib Injector；server={authlibServer}；agent={authlibPath}；prefetched={authlibMetadata.Length}B。");
        }
        else if (!string.IsNullOrWhiteSpace(authlibPath) && !string.IsNullOrWhiteSpace(authlibServer))
        {
            PortableLog.Info(
                "LaunchPlan",
                $"已启用 Authlib Injector；server={authlibServer}。");
        }

        // Compatibility path: standard third-party authentication agent.
        // Eligible host identities use the session bridge and ASM authlib patch instead.
        return await MinecraftProcessLaunchService.CreatePlanAsync(
            new MinecraftProcessLaunchRequest
            {
                VersionId = instance.Name,
                VersionJsonPath = instance.VersionJsonPath,
                InstanceDirectory = instance.InstanceDirectory,
                MinecraftRootDirectory = GetMinecraftRootFromInstance(instance),
                PlayerName = profile.Username,
                PlayerUuid = string.IsNullOrWhiteSpace(profile.Uuid) ? Guid.NewGuid().ToString("N") : profile.Uuid,
                AccessToken = string.IsNullOrWhiteSpace(profile.AccessToken) ? "0" : profile.AccessToken,
                JavaExecutablePath = javaExecutablePath,
                JavaMajorVersion = javaMajorVersion,
                MemoryMegabytes = ResolveLaunchMemoryMegabytes(instance, metadata, settings),
                Width = width,
                Height = height,
                Fullscreen = windowType == 0,
                IsolatedGameDirectory = metadata.InstanceIsolation,
                CustomJvmArguments = BuildInstanceJvmArguments(metadata, settings),
                CustomGameArguments = FirstNonEmpty(metadata.GameArguments, GetTextOption(settings, LauncherSettingKeys.LaunchAdvanceGame)),
                ClasspathHeadEntries = SplitClasspathHead(metadata.ClasspathHead),
                AuthlibInjectorPath = authlibPath,
                AuthlibServer = authlibServer,
                AuthlibPrefetchedMetadata = authlibMetadata,
                UseExperimentalJvmHost = useJvmHost,
                JvmHostIdentityMode = profile.Kind switch
                {
                    LaunchLoginProfileKind.ThirdParty or
                        LaunchLoginProfileKind.LittleSkin or
                        LaunchLoginProfileKind.NCloud => MinecraftJvmHostIdentityMode.ThirdParty,
                    LaunchLoginProfileKind.Offline => MinecraftJvmHostIdentityMode.Offline,
                    _ => MinecraftJvmHostIdentityMode.Official
                },
                OfflineSkinSource = profile.Kind == LaunchLoginProfileKind.Offline ? profile.SkinAddress : null,
                OfflineSkinSlim = profile.Kind == LaunchLoginProfileKind.Offline &&
                                  string.Equals(
                                      LoginProfileInfo.ResolveOfflineDefaultModel(profile.Uuid),
                                      "Alex",
                                      StringComparison.Ordinal),
                PreferredIpStack = GetPreferredIpStack(settings),
                Server = string.IsNullOrWhiteSpace(worldName)
                    ? FirstNonEmpty(serverAddress, metadata.ServerToEnter)
                    : null,
                ReleaseTime = TryReadReleaseTime(instance),
                HasOptiFine = HasOptiFine(instance),
                WorldName = worldName,
                LauncherName = "PCL-N",
                VersionType = FirstNonEmpty(
                    metadata.CustomInfo,
                    settings.GetTextOption("LaunchArgumentInfo", LauncherSettingDefaults.GetText("LaunchArgumentInfo"))) ?? "PCL-N",
                UseSystemGlfw = metadata.UseSystemGlfw ||
                    settings.GetBooleanOption(
                        LauncherSettingKeys.LaunchUseSystemGlfw.Value,
                        LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.LaunchUseSystemGlfw.Value)),
                ForceX11OnWayland = metadata.ForceX11OnWayland &&
                    settings.GetBooleanOption(
                        LauncherSettingKeys.LaunchForceX11OnWayland.Value,
                        LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.LaunchForceX11OnWayland.Value))
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task RunPreLaunchCommandAsync(
        string command,
        bool waitForExit,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        using Process? process = Process.Start(CreateShellStartInfo(command, workingDirectory));
        if (process is null)
            throw new InvalidOperationException("预启动命令未能启动。");

        if (!waitForExit)
            return;

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("预启动命令执行失败，退出码：" + process.ExitCode.ToString(CultureInfo.InvariantCulture));
    }

    public static void ApplyProcessPriority(Process process, LauncherSettings settings)
    {
        int configuredPriority = settings.GetIntegerOption(
            "LaunchArgumentPriority",
            LauncherSettingDefaults.GetInteger("LaunchArgumentPriority"));
        try
        {
            process.PriorityClass = configuredPriority switch
            {
                0 => ProcessPriorityClass.AboveNormal,
                2 => ProcessPriorityClass.BelowNormal,
                3 => ProcessPriorityClass.High,
                4 => ProcessPriorityClass.RealTime,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            int processId;
            try
            {
                processId = process.Id;
            }
            catch (InvalidOperationException)
            {
                processId = -1;
            }

            PortableLog.Warn(
                ex,
                "Launch",
                $"无法设置 Minecraft 进程优先级；PID={processId}；配置值={configuredPriority}。");
        }
    }

    public static string GetMinecraftRootFromInstance(LaunchInstanceInfo instance)
    {
        DirectoryInfo versionDirectory = new(instance.InstanceDirectory);
        DirectoryInfo versionsDirectory = versionDirectory.Parent
            ?? throw new InvalidOperationException("无法确定 versions 目录。");
        return versionsDirectory.Parent?.FullName
               ?? throw new InvalidOperationException("无法确定 Minecraft 根目录。");
    }

    public static string ResolvePreferredJavaExecutablePath(bool forceConsole = false)
    {
        bool forceConsoleJava = forceConsole || LauncherSettingDefaults.GetBoolean("LaunchAdvanceNoJavaw");
        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            forceConsoleJava = forceConsole || settings.GetBooleanOption(
                "LaunchAdvanceNoJavaw",
                LauncherSettingDefaults.GetBoolean("LaunchAdvanceNoJavaw"));
            if (settings.TryGetTextOption(LauncherSettingKeys.LaunchSelectedJava, out string? selectedJava) &&
                !string.IsNullOrWhiteSpace(selectedJava) &&
                File.Exists(selectedJava))
            {
                if (OperatingSystem.IsWindows() && forceConsoleJava &&
                    string.Equals(Path.GetFileName(selectedJava), "javaw.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string java = Path.Combine(Path.GetDirectoryName(selectedJava) ?? string.Empty, "java.exe");
                    if (File.Exists(java))
                        return java;
                }

                if (!forceConsoleJava && OperatingSystem.IsWindows() &&
                    string.Equals(Path.GetFileName(selectedJava), "java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string javaw = Path.Combine(Path.GetDirectoryName(selectedJava) ?? string.Empty, "javaw.exe");
                    if (File.Exists(javaw))
                        return javaw;
                }

                return selectedJava;
            }
        }
        catch (Exception)
        {
            // 启动路径读取失败时退回系统 PATH，避免设置文件损坏阻断启动。
        }

        return OperatingSystem.IsWindows() && !forceConsoleJava
            ? "javaw"
            : "java";
    }

    public static string ResolveInstanceJavaExecutablePath(InstanceMetadata metadata, bool forceConsole = false)
    {
        if (metadata.JavaSelectionMode == 2 &&
            !string.IsNullOrWhiteSpace(metadata.SelectedJavaPath) &&
            File.Exists(metadata.SelectedJavaPath))
        {
            string selectedJava = metadata.SelectedJavaPath;
            if (OperatingSystem.IsWindows())
            {
                string directory = Path.GetDirectoryName(selectedJava) ?? string.Empty;
                if (forceConsole && string.Equals(Path.GetFileName(selectedJava), "javaw.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string consoleJava = Path.Combine(directory, "java.exe");
                    if (File.Exists(consoleJava))
                        return consoleJava;
                }
                if (!forceConsole && string.Equals(Path.GetFileName(selectedJava), "java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string windowJava = Path.Combine(directory, "javaw.exe");
                    if (File.Exists(windowJava))
                        return windowJava;
                }
            }
            return selectedJava;
        }

        if (metadata.JavaSelectionMode == 1)
            return OperatingSystem.IsWindows() && !forceConsole ? "javaw" : "java";
        return ResolvePreferredJavaExecutablePath(forceConsole);
    }

    public static string ReadMinecraftVersionId(LaunchInstanceInfo instance)
        => MinecraftVersionJsonInspector.Read(instance).MinecraftVersionId;

    public static bool IsAccessTokenUsable(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return false;

        string[] parts = accessToken.Split('.');
        if (parts.Length < 2)
            return true;
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty("exp", out JsonElement expiration) ||
                !expiration.TryGetInt64(out long seconds))
            {
                return true;
            }

            return DateTimeOffset.FromUnixTimeSeconds(seconds) > DateTimeOffset.UtcNow.AddMinutes(2d);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    public static string CreateOfflineUuid(string username, bool legacy)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(legacy ? username : "OfflinePlayer:" + username);
#pragma warning disable CA5351
        byte[] hash = System.Security.Cryptography.MD5.HashData(bytes);
#pragma warning restore CA5351
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash).ToString("N");
    }

    public static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    internal static bool ShouldUseJvmHostForProfile(
        bool experimentalJvmHostEnabled,
        LaunchLoginProfileKind profileKind)
    {
        // LittleSkin and N Cloud issue standard Yggdrasil sessions whose texture and
        // join-server behavior must be handled by the compatibility agent. The embedded
        // host's local session bridge is intentionally not used for these providers.
        return experimentalJvmHostEnabled &&
               profileKind is not (
                   LaunchLoginProfileKind.LittleSkin or
                   LaunchLoginProfileKind.NCloud);
    }

    private static async Task<int> ResolveJavaMajorVersionAsync(
        string javaExecutablePath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PCL.Domain.Minecraft.Java.JavaRuntimeCandidate> candidates =
            await new FileSystemJavaLocator([javaExecutablePath]).FindAllAsync(cancellationToken)
                .ConfigureAwait(false);
        return candidates.Count > 0 ? candidates[0].Installation.MajorVersion : 17;
    }

    /// <summary>
    /// Canonical N Cloud Yggdrasil root for Authlib Injector.
    /// Rewrites empty / legacy Supabase Edge URLs to <c>PCLN_PLUGIN_API_URL</c> (or
    /// <c>https://api.pcln.top/v1/</c>) + <c>yggdrasil</c>. Old edge metadata only lists
    /// <c>.supabase.co</c> skin domains, so launching against it drops in-game skins after
    /// the Cloudflare cutover.
    /// </summary>
    internal static string ResolveNCloudAuthServer(string? authServer)
    {
        string canonical = GetCanonicalNCloudYggdrasilRoot();
        if (string.IsNullOrWhiteSpace(authServer))
            return canonical;

        string normalized = AuthlibInjectorService.NormalizeAuthServer(authServer);
        if (IsLegacyNCloudAuthServer(normalized))
            return canonical;

        return normalized;
    }

    /// <summary>True when the URL is the historical Supabase Edge Function Yggdrasil root.</summary>
    internal static bool IsLegacyNCloudAuthServer(string? authServer)
    {
        if (string.IsNullOrWhiteSpace(authServer))
            return false;
        if (!Uri.TryCreate(authServer.Trim(), UriKind.Absolute, out Uri? uri))
            return false;

        return uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.Contains("plugin-center-api", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCanonicalNCloudYggdrasilRoot()
    {
        string? envBase = Environment.GetEnvironmentVariable("PCLN_PLUGIN_API_URL");
        string apiBase = string.IsNullOrWhiteSpace(envBase)
            ? "https://api.pcln.top/v1/"
            : envBase.Trim();

        if (!apiBase.EndsWith('/'))
            apiBase += "/";
        return AuthlibInjectorService.NormalizeAuthServer(apiBase + "yggdrasil");
    }

    private static async Task<(string? Path, string? Server, string? Metadata)> ResolveAuthlibLaunchOptionsAsync(
        LoginProfileInfo profile,
        bool useJvmHost,
        CancellationToken cancellationToken)
    {
        bool requiresAgent =
            profile.Kind is LaunchLoginProfileKind.NCloud or LaunchLoginProfileKind.LittleSkin;

        if (!profile.UsesYggdrasil)
            return (null, null, null);

        string authServer;
        if (profile.Kind == LaunchLoginProfileKind.NCloud)
        {
            // Always resolve through the N Cloud helper so empty and legacy Supabase URLs
            // become the canonical API before Authlib metadata / -javaagent is built.
            string? previous = string.IsNullOrWhiteSpace(profile.AuthServer)
                ? null
                : AuthlibInjectorService.NormalizeAuthServer(profile.AuthServer);
            authServer = ResolveNCloudAuthServer(profile.AuthServer);
            if (string.IsNullOrWhiteSpace(previous))
            {
                PortableLog.Warn(
                    "LaunchPlan",
                    "N Cloud 档案缺少 AuthServer，已回退到默认 Yggdrasil 根：" + authServer);
            }
            else if (!string.Equals(previous, authServer, StringComparison.OrdinalIgnoreCase))
            {
                PortableLog.Warn(
                    "LaunchPlan",
                    "N Cloud AuthServer 已从旧地址迁移：" + previous + " → " + authServer);
            }
        }
        else if (string.IsNullOrWhiteSpace(profile.AuthServer))
        {
            if (requiresAgent)
            {
                throw new InvalidOperationException(
                    "LittleSkin 档案缺少认证服务器地址（AuthServer），无法注入 Authlib。请重新登录。");
            }

            return (null, null, null);
        }
        else
        {
            authServer = AuthlibInjectorService.NormalizeAuthServer(profile.AuthServer);
        }

        AuthlibInjectorService service = new();
        string metadata;
        try
        {
            metadata = await service.GetServerMetadataAsync(authServer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (requiresAgent && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"无法读取 Authlib 认证服务器元数据：{authServer}。N Cloud / 外置登录皮肤依赖该地址可用。",
                ex);
        }

        if (requiresAgent && string.IsNullOrWhiteSpace(metadata))
        {
            throw new InvalidOperationException(
                $"Authlib 认证服务器返回了空元数据：{authServer}。无法预取 API（皮肤 / 会话）配置。");
        }

        // Host mode takes over compatible generic third-party accounts. LittleSkin and
        // N Cloud are excluded before this method and always keep the standard agent path.
        if (useJvmHost)
            return (null, authServer, metadata);

        string authlibPath;
        try
        {
            authlibPath = await service.EnsureAsync(GetAuthlibInjectorCachePath(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (requiresAgent && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "无法准备 Authlib Injector 组件。请检查网络后重试，否则游戏内将无法加载外置皮肤。",
                ex);
        }

        if (requiresAgent)
        {
            PortableLog.Info(
                "LaunchPlan",
                $"Authlib API 已就绪：server={authServer}；prefetchedBytes={metadata.Length}；agent={authlibPath}");
        }

        return (authlibPath, authServer, metadata);
    }

    private static string GetAuthlibInjectorCachePath()
    {
        return Path.Combine(PCL.Desktop.Paths.LauncherPathLayout.ResolveDataDirectory(), "authlib-injector.jar");
    }

    private static ProcessStartInfo CreateShellStartInfo(string command, string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
            startInfo.ArgumentList.Add("/C");
        else
            startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static int ResolveLaunchMemoryMegabytes(
        LaunchInstanceInfo instance,
        InstanceMetadata metadata,
        LauncherSettings settings)
    {
        int memorySolution = metadata.MemorySolution;
        int customMemorySize = metadata.CustomMemorySize;
        if (memorySolution == 2)
        {
            memorySolution = GetIntegerOption(settings, LauncherSettingKeys.LaunchRamType, 0);
            customMemorySize = GetIntegerOption(settings, LauncherSettingKeys.LaunchRamCustom, 15);
        }

        return LaunchMemoryCalculator.ResolveMemoryMegabytes(
            new LaunchMemoryRequest
            {
                MemorySolution = memorySolution,
                CustomMemorySize = customMemorySize,
                MemoryInfo = new PCL.Platform.System.DefaultSystemInfoProvider().GetMemoryInfo(),
                Profile = GetMemoryProfile(instance, metadata),
                ModCount = CountModFiles(instance, metadata)
            });
    }

    private static LaunchMemoryProfile GetMemoryProfile(LaunchInstanceInfo instance, InstanceMetadata metadata)
    {
        if (CountModFiles(instance, metadata) > 0 || VersionJsonContains(instance, "fabric-loader", "forge", "neoforge", "quilt"))
            return LaunchMemoryProfile.Modded;
        return HasOptiFine(instance) ? LaunchMemoryProfile.OptiFine : LaunchMemoryProfile.Vanilla;
    }

    private static int CountModFiles(LaunchInstanceInfo instance, InstanceMetadata metadata)
    {
        HashSet<string> modPaths = new(StringComparer.OrdinalIgnoreCase);
        AddModFiles(modPaths, Path.Combine(instance.InstanceDirectory, "mods"));
        if (!metadata.InstanceIsolation)
            AddModFiles(modPaths, Path.Combine(GetMinecraftRootFromInstance(instance), "mods"));
        return modPaths.Count;
    }

    private static void AddModFiles(HashSet<string> modPaths, string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory))
            return;

        foreach (string file in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
            modPaths.Add(file);
    }

    private static (int Width, int Height) GetWindowSize(LauncherSettings settings)
    {
        int width = GetTextOptionAsInt(settings, LauncherSettingKeys.LaunchArgumentWindowWidth, 854);
        int height = GetTextOptionAsInt(settings, LauncherSettingKeys.LaunchArgumentWindowHeight, 480);
        return (Math.Clamp(width, 1, 9999), Math.Clamp(height, 1, 9999));
    }

    private static int GetIntegerOption(LauncherSettings settings, SettingKey key, int fallback) =>
        settings.GetIntegerOption(key, fallback);

    private static string GetTextOption(LauncherSettings settings, SettingKey key) =>
        settings.GetTextOption(key);

    private static int GetTextOptionAsInt(LauncherSettings settings, SettingKey key, int fallback) =>
        int.TryParse(GetTextOption(settings, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static MinecraftJvmIpPreference GetPreferredIpStack(LauncherSettings settings) =>
        GetIntegerOption(settings, LauncherSettingKeys.LaunchPreferredIpStack, 1) switch
        {
            0 => MinecraftJvmIpPreference.PreferV4,
            2 => MinecraftJvmIpPreference.PreferV6,
            _ => MinecraftJvmIpPreference.SystemDefault
        };

    private static string[] SplitClasspathHead(string classpathHead)
    {
        if (string.IsNullOrWhiteSpace(classpathHead))
            return [];

        return classpathHead.Split(
                ["\r\n", "\n", Path.PathSeparator.ToString()],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static entry => !string.IsNullOrWhiteSpace(entry))
            .ToArray();
    }

    private static string BuildInstanceJvmArguments(InstanceMetadata metadata, LauncherSettings settings)
    {
        string arguments = FirstNonEmpty(
            metadata.JvmArguments,
            GetTextOption(settings, LauncherSettingKeys.LaunchAdvanceJvm)) ?? string.Empty;
        if (!metadata.UseProxy ||
            settings.GetIntegerOption("SystemHttpProxyType", LauncherSettingDefaults.GetInteger("SystemHttpProxyType")) != 2 ||
            !Uri.TryCreate(
                settings.GetTextOption("SystemHttpProxy", LauncherSettingDefaults.GetText("SystemHttpProxy")),
                UriKind.Absolute,
                out Uri? proxy))
        {
            return arguments;
        }

        string proxyArguments = $"-Dhttp.proxyHost={proxy.Host} -Dhttp.proxyPort={proxy.Port} " +
                                $"-Dhttps.proxyHost={proxy.Host} -Dhttps.proxyPort={proxy.Port}";
        return string.IsNullOrWhiteSpace(arguments) ? proxyArguments : arguments.Trim() + " " + proxyArguments;
    }

    private static DateTimeOffset? TryReadReleaseTime(LaunchInstanceInfo instance)
    {
        try
        {
            using FileStream stream = File.OpenRead(instance.VersionJsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            string? releaseTime = TryReadJsonString(document.RootElement, "releaseTime");
            return DateTimeOffset.TryParse(
                releaseTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset value)
                ? value
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadJsonString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool HasOptiFine(LaunchInstanceInfo instance)
    {
        if (VersionJsonContains(instance, "optifine"))
            return true;

        try
        {
            return Directory.EnumerateFiles(instance.InstanceDirectory, "*", SearchOption.TopDirectoryOnly)
                .Any(static file => Path.GetFileName(file).Contains("optifine", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool VersionJsonContains(LaunchInstanceInfo instance, params string[] needles)
    {
        bool hasNeedle = false;
        int overlapLength = 0;
        foreach (string needle in needles)
        {
            if (string.IsNullOrWhiteSpace(needle))
                continue;

            hasNeedle = true;
            overlapLength = Math.Max(overlapLength, needle.Length - 1);
        }

        if (!hasNeedle)
            return false;

        try
        {
            char[] buffer = ArrayPool<char>.Shared.Rent(8 * 1024 + overlapLength);
            try
            {
                using StreamReader reader = new(
                    new FileStream(
                        instance.VersionJsonPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        bufferSize: 16 * 1024,
                        useAsync: false),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 8 * 1024,
                    leaveOpen: false);

                int carryLength = 0;
                while (true)
                {
                    int read = reader.ReadBlock(buffer, carryLength, buffer.Length - carryLength);
                    if (read == 0)
                        return false;

                    ReadOnlySpan<char> current = buffer.AsSpan(0, carryLength + read);
                    foreach (string needle in needles)
                    {
                        if (!string.IsNullOrWhiteSpace(needle) &&
                            current.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    carryLength = Math.Min(overlapLength, current.Length);
                    if (carryLength > 0)
                        current[^carryLength..].CopyTo(buffer);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}
