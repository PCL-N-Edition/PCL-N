// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using PCL.Application.Downloads;
using PCL.Application.Minecraft.Launch;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Domain.Minecraft.Java;

namespace PCL.Desktop.Features.Downloads;

/// <summary>
/// Shared host-side request building for vanilla instance installs and modpack base installs.
/// Both paths call <see cref="MinecraftVanillaInstallService.InstallAsync"/>; this coordinator
/// keeps Java / writable root / thread / download-source selection identical.
/// </summary>
internal static class DesktopMinecraftInstallCoordinator
{
    public static string ResolveWritableMinecraftRoot(string? requestedRoot, Func<string> getDefaultRoot)
    {
        ArgumentNullException.ThrowIfNull(getDefaultRoot);
        string minecraftRoot = string.IsNullOrWhiteSpace(requestedRoot)
            ? getDefaultRoot()
            : requestedRoot;

        if (!LaunchInstanceDiscovery.CanUseAsMinecraftRoot(minecraftRoot))
        {
            string fallback = getDefaultRoot();
            DesktopFileLog.Warn(
                "Install",
                $"目标游戏目录不可写，改用可写目录：{minecraftRoot} → {fallback}");
            minecraftRoot = fallback;
        }

        try
        {
            Directory.CreateDirectory(minecraftRoot);
            return minecraftRoot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string fallback = LaunchInstanceDiscovery.GetCurrentMinecraftRoot();
            if (string.Equals(
                    Path.GetFullPath(minecraftRoot),
                    Path.GetFullPath(fallback),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            DesktopFileLog.Warn(
                "Install",
                $"创建游戏目录失败，回退到当前目录：{minecraftRoot} → {fallback}",
                ex);
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static int ResolveDownloadThreadLimit(LauncherSettings? settings = null)
    {
        settings ??= LauncherSettingsPageBinder.LoadSettings();
        return Math.Clamp(
            settings.GetIntegerOption(LauncherSettingKeys.ToolDownloadThread, 63) + 1,
            1,
            256);
    }

    public static bool ResolvePreferOfficialSource(LauncherSettings? settings = null)
    {
        settings ??= LauncherSettingsPageBinder.LoadSettings();
        return settings.DownloadSource != DownloadSourcePreference.MirrorOnly;
    }

    public static async Task<string> ResolveInstallJavaExecutableAsync(
        string? preferredOrContextPath,
        string? minecraftVersionHint,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveExistingJava(preferredOrContextPath, out string fromContext))
            return MinecraftLaunchCoordinator.PreferJavaExecutable(fromContext, forceConsole: true);

        string preferred = MinecraftLaunchPlanFactory.ResolvePreferredJavaExecutablePath(forceConsole: true);
        if (TryResolveExistingJava(preferred, out string fromSettings))
            return MinecraftLaunchCoordinator.PreferJavaExecutable(fromSettings, forceConsole: true);

        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        IReadOnlyList<JavaRuntimeCandidate> catalog = await JavaRuntimeCatalog
            .LoadAsync(settings, cancellationToken)
            .ConfigureAwait(false);

        JavaVersionRange range = GuessJavaRangeForMinecraft(minecraftVersionHint);
        JavaRuntimeCandidate? best = JavaRuntimeCatalog.SelectBest(catalog, range);
        if (best is null)
        {
            best = catalog
                .Where(static candidate => candidate.IsAvailable && candidate.IsEnabled)
                .OrderByDescending(static candidate => candidate.Installation.MajorVersion)
                .FirstOrDefault();
        }

        if (best is null)
        {
            throw new InvalidOperationException(
                "安装需要可用的 Java，但启动器没有找到已启用的 Java。" +
                "请先到设置 → 启动中添加或选择 Java，然后再试。");
        }

        PortableLog.Info(
            "MinecraftInstall",
            $"安装使用 Java {best.Installation.MajorVersion}：{best.Installation.JavaExecutablePath}");
        return MinecraftLaunchCoordinator.PreferJavaExecutable(
            best.Installation.JavaExecutablePath,
            forceConsole: true);
    }

    public static async Task<MinecraftInstallRequest> BuildVanillaInstallRequestAsync(
        string versionId,
        string baseVersionId,
        string versionJsonUrl,
        string? minecraftRoot,
        Func<string> getDefaultRoot,
        MinecraftLoaderInstallRequest? loader,
        IReadOnlyList<MinecraftInstallAddonRequest>? addons,
        bool replaceExistingVersion,
        CancellationToken cancellationToken = default)
    {
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        string root = ResolveWritableMinecraftRoot(minecraftRoot, getDefaultRoot);
        string java = await ResolveInstallJavaExecutableAsync(
                preferredOrContextPath: null,
                minecraftVersionHint: baseVersionId,
                cancellationToken)
            .ConfigureAwait(false);

        return new MinecraftInstallRequest
        {
            VersionId = versionId,
            BaseVersionId = baseVersionId,
            VersionJsonUrl = versionJsonUrl,
            MinecraftRootDirectory = root,
            PreferOfficialSource = ResolvePreferOfficialSource(settings),
            DownloadThreadLimit = ResolveDownloadThreadLimit(settings),
            Loader = loader,
            Addons = addons ?? [],
            ReplaceExistingVersion = replaceExistingVersion,
            JavaExecutablePath = java,
            LoaderExtraJvmArguments = ResolveLoaderProxyJvmArguments(settings)
        };
    }

    public static async Task<MinecraftModpackInstallRequest> BuildModpackInstallRequestAsync(
        string archivePath,
        string? minecraftRoot,
        Func<string> getDefaultRoot,
        string? preferredJavaHint,
        string? minecraftVersionHint,
        CancellationToken cancellationToken = default)
    {
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        string root = ResolveWritableMinecraftRoot(minecraftRoot, getDefaultRoot);
        string java = await ResolveInstallJavaExecutableAsync(
                preferredJavaHint,
                minecraftVersionHint,
                cancellationToken)
            .ConfigureAwait(false);

        return new MinecraftModpackInstallRequest
        {
            ArchivePath = archivePath,
            MinecraftRootDirectory = root,
            PreferOfficialSource = ResolvePreferOfficialSource(settings),
            DownloadThreadLimit = ResolveDownloadThreadLimit(settings),
            JavaExecutablePath = java,
            LoaderExtraJvmArguments = ResolveLoaderProxyJvmArguments(settings)
        };
    }

    /// <summary>
    /// JVM system properties for Forge/NeoForge installers.
    /// Covers custom proxy and "follow system proxy" (Java ignores WinHTTP otherwise).
    /// </summary>
    public static string? ResolveLoaderProxyJvmArguments(LauncherSettings? settings = null)
    {
        settings ??= LauncherSettingsPageBinder.LoadSettings();
        int proxyType = settings.GetIntegerOption(
            "SystemHttpProxyType",
            LauncherSettingDefaults.GetInteger("SystemHttpProxyType"));

        // 0 = off, 1 = system, 2 = custom
        if (proxyType == 0)
            return null;

        Uri? proxyUri = null;
        if (proxyType == 2)
        {
            string address = settings.GetTextOption(
                "SystemHttpProxy",
                LauncherSettingDefaults.GetText("SystemHttpProxy"));
            if (Uri.TryCreate(address, UriKind.Absolute, out Uri? custom) &&
                !string.IsNullOrWhiteSpace(custom.Host) &&
                custom.Port > 0)
            {
                proxyUri = custom;
            }
        }
        else
        {
            // Follow system proxy — resolve against a real HTTPS origin NeoForge will hit.
            try
            {
                IWebProxy systemProxy = HttpClient.DefaultProxy;
                Uri probe = new("https://maven.neoforged.net/");
                Uri? candidate = systemProxy.GetProxy(probe);
                if (candidate is not null &&
                    !string.Equals(candidate.Host, probe.Host, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(candidate.Host) &&
                    candidate.Port > 0)
                {
                    proxyUri = candidate;
                }
            }
            catch (Exception)
            {
                // Fall through — installer runs without explicit proxy flags.
            }
        }

        if (proxyUri is null)
            return null;

        PortableLog.Info(
            "MinecraftInstall",
            $"加载器安装器将使用代理 {proxyUri.Host}:{proxyUri.Port}（设置类型={proxyType}）。");
        return $"-Dhttp.proxyHost={proxyUri.Host} -Dhttp.proxyPort={proxyUri.Port} " +
               $"-Dhttps.proxyHost={proxyUri.Host} -Dhttps.proxyPort={proxyUri.Port} " +
               "-Djava.net.useSystemProxies=true";
    }

    private static bool TryResolveExistingJava(string? path, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (path is "java" or "javaw" or "java.exe" or "javaw.exe")
            return false;
        return JavaRuntimeCatalog.TryResolveExistingJavaPath(path, out resolved);
    }

    private static JavaVersionRange GuessJavaRangeForMinecraft(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            return JavaVersionRange.Any;

        // Mirror JavaRuntimeRequirementResolver Minecraft base rules for installer selection.
        if (Version.TryParse(minecraftVersion.Split('-', 2)[0], out Version? version))
        {
            if (version >= new Version(1, 20, 5))
                return new JavaVersionRange(JavaVersionRange.ForMajor(21), JavaVersionRange.Any.Maximum);
            if (version >= new Version(1, 18))
                return new JavaVersionRange(JavaVersionRange.ForMajor(17), JavaVersionRange.Any.Maximum);
            if (version >= new Version(1, 17))
                return new JavaVersionRange(JavaVersionRange.ForMajor(16), JavaVersionRange.Any.Maximum);
            if (version >= new Version(1, 13))
                return new JavaVersionRange(JavaVersionRange.ForMajor(8), JavaVersionRange.Any.Maximum);
        }

        return JavaVersionRange.Any;
    }
}
