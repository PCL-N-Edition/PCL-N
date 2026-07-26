// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Launching;
using PCL.Core.Logging;

namespace PCL.Desktop.Features.Launching.Views;

public sealed record LaunchInstanceInfo(string Name, string VersionJsonPath, string InstanceDirectory);

public sealed record LaunchInstanceDiscoveryProgress(
    string Stage,
    int Current,
    int Total,
    int Found,
    string? RootDirectory = null);

public static class LaunchInstanceDiscovery
{
    public static Task<IReadOnlyList<LaunchInstanceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Discover(GetCandidateRoots(), cancellationToken), cancellationToken);

    public static Task<IReadOnlyList<LaunchInstanceInfo>> DiscoverAsync(
        IEnumerable<string> candidateRoots,
        CancellationToken cancellationToken = default)
        => DiscoverAsync(candidateRoots, progress: null, cancellationToken);

    public static Task<IReadOnlyList<LaunchInstanceInfo>> DiscoverAsync(
        IEnumerable<string> candidateRoots,
        IProgress<LaunchInstanceDiscoveryProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateRoots);
        string[] roots = candidateRoots.ToArray();
        return Task.Run(() => Discover(roots, progress, cancellationToken), cancellationToken);
    }

    public static IReadOnlyList<LaunchInstanceInfo> Discover(
        IEnumerable<string> candidateRoots,
        CancellationToken cancellationToken = default)
        => Discover(candidateRoots, progress: null, cancellationToken);

    public static IReadOnlyList<LaunchInstanceInfo> Discover(
        IEnumerable<string> candidateRoots,
        IProgress<LaunchInstanceDiscoveryProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        string[] roots = candidateRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PortableLog.Info("InstanceDiscovery", $"开始扫描 Minecraft 实例；候选根目录={roots.Length}。");
        PortableLog.Debug("InstanceDiscovery", "扫描根目录：" + string.Join(" | ", roots));
        List<(LaunchInstanceInfo Instance, DateTime LastWriteTimeUtc)> result = [];
        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = roots[rootIndex];
            PortableLog.RealTime("InstanceDiscovery", $"扫描根目录；Index={rootIndex + 1}/{roots.Length}；Root={root}；Found={result.Count}。");
            progress?.Report(new LaunchInstanceDiscoveryProgress(
                "正在扫描游戏文件夹",
                rootIndex,
                roots.Length,
                result.Count,
                root));
            string versionsRoot = Path.Combine(root, "versions");
            if (!Directory.Exists(versionsRoot))
                continue;

            DirectoryInfo[] versionDirectories;
            try
            {
                versionDirectories = new DirectoryInfo(versionsRoot).GetDirectories();
            }
            catch (IOException ex)
            {
                PortableLog.Warn(ex, "InstanceDiscovery", $"无法读取版本目录，将跳过：{versionsRoot}");
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                PortableLog.Warn(ex, "InstanceDiscovery", $"无权读取版本目录，将跳过：{versionsRoot}");
                continue;
            }

            progress?.Report(new LaunchInstanceDiscoveryProgress(
                "正在检查游戏版本",
                0,
                versionDirectories.Length,
                result.Count,
                root));
            for (int versionIndex = 0; versionIndex < versionDirectories.Length; versionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectoryInfo versionDirectory = versionDirectories[versionIndex];
                PortableLog.RealTime(
                    "InstanceDiscovery",
                    $"检查版本目录；Root={root}；Index={versionIndex + 1}/{versionDirectories.Length}；Directory={versionDirectory.FullName}。");
                string name = versionDirectory.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    string? jsonPath = MinecraftVersionFileResolver.FindPrimaryJson(versionDirectory.FullName);
                    if (jsonPath is not null)
                    {
                        result.Add((
                            new LaunchInstanceInfo(name, jsonPath, versionDirectory.FullName),
                            versionDirectory.LastWriteTimeUtc));
                    }
                }

                progress?.Report(new LaunchInstanceDiscoveryProgress(
                    "正在检查游戏版本",
                    versionIndex + 1,
                    versionDirectories.Length,
                    result.Count,
                    root));
            }
        }

        progress?.Report(new LaunchInstanceDiscoveryProgress(
            "游戏版本检查完成",
            roots.Length,
            roots.Length,
            result.Count));
        LaunchInstanceInfo[] instances = result
            .OrderByDescending(entry => entry.LastWriteTimeUtc)
            .ThenBy(entry => entry.Instance.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Instance)
            .ToArray();
        PortableLog.Info("InstanceDiscovery", $"Minecraft 实例扫描完成；根目录={roots.Length}；实例={instances.Length}。");
        return instances;
    }

    public static IReadOnlyList<string> GetCandidateRoots()
    {
        List<string> roots = [];
        string? configuredRoots = Environment.GetEnvironmentVariable("PCLN_MINECRAFT_ROOTS");
        if (!string.IsNullOrWhiteSpace(configuredRoots))
        {
            foreach (string root in configuredRoots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddIfUsable(roots, root);
        }

        // Always surface the three preset roots even when the path does not exist yet.
        AddIfUsable(roots, GetCurrentMinecraftRoot());
        AddIfUsable(roots, GetOfficialMinecraftRoot());
        AddIfUsable(roots, GetUserMinecraftRoot());

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string GetCurrentMinecraftRoot() =>
        Path.Combine(Program.GetLauncherDirectory(), ".minecraft");

    public static string? GetOfficialMinecraftRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, ".minecraft");
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            return null;

        return OperatingSystem.IsMacOS()
            ? Path.Combine(userProfile, "Library", "Application Support", "minecraft")
            : Path.Combine(userProfile, ".minecraft");
    }

    /// <summary>
    /// Per-user home <c>.minecraft</c>. On Linux/macOS this often coincides with the official root.
    /// </summary>
    public static string? GetUserMinecraftRoot()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            return null;

        string candidate = Path.Combine(userProfile, ".minecraft");
        string? official = NormalizePath(GetOfficialMinecraftRoot());
        string? normalized = NormalizePath(candidate);
        if (official is not null &&
            normalized is not null &&
            string.Equals(official, normalized, StringComparison.OrdinalIgnoreCase))
        {
            // Official already covers this path on non-Windows platforms.
            return null;
        }

        return candidate;
    }

    public static string GetMinecraftRootDisplayName(string rootDirectory)
    {
        string normalized = NormalizePath(rootDirectory) ?? rootDirectory;
        string? current = NormalizePath(GetCurrentMinecraftRoot());
        if (current is not null && string.Equals(normalized, current, StringComparison.OrdinalIgnoreCase))
            return "当前文件夹";

        string? official = NormalizePath(GetOfficialMinecraftRoot());
        if (official is not null && string.Equals(normalized, official, StringComparison.OrdinalIgnoreCase))
            return "官方启动器文件夹";

        string? user = NormalizePath(GetUserMinecraftRoot());
        if (user is not null && string.Equals(normalized, user, StringComparison.OrdinalIgnoreCase))
            return "用户文件夹";

        // Fallback for a raw %USERPROFILE%\.minecraft that wasn't classified above.
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string? profileMinecraft = NormalizePath(Path.Combine(userProfile, ".minecraft"));
            if (profileMinecraft is not null &&
                string.Equals(normalized, profileMinecraft, StringComparison.OrdinalIgnoreCase))
            {
                return "用户文件夹";
            }
        }

        string trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        if (string.Equals(leaf, ".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(trimmed);
            string parentName = string.IsNullOrWhiteSpace(parent) ? string.Empty : Path.GetFileName(parent);
            return string.IsNullOrWhiteSpace(parentName) ? "Minecraft" : parentName;
        }

        return string.IsNullOrWhiteSpace(leaf) ? rootDirectory : leaf;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            PortableLog.Warn(ex, "InstanceDiscovery", $"无法规范化路径，将忽略：{path}");
            return null;
        }
    }

    private static void AddIfUsable(List<string> roots, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            roots.Add(path);
    }
}
