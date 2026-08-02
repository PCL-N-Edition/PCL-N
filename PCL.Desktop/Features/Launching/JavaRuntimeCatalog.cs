// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PCL.Application.Minecraft.Launch;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Domain.Minecraft.Java;
using PCL.Domain.Minecraft.Launch;
using PCL.Platform.Java;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Shared Java discovery used by Settings and launch so custom roots / disabled
/// flags stay consistent with <see cref="Features.Settings.Views.PageSetupJava"/>.
/// </summary>
internal static class JavaRuntimeCatalog
{
    private static readonly ConcurrentDictionary<string, JavaRuntimeCatalogCache> CacheStores =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async Task<IReadOnlyList<JavaRuntimeCandidate>> LoadAsync(
        LauncherSettings settings,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        long startedAt = Stopwatch.GetTimestamp();

        string[] customRoots = ReadCustomJavaRoots(settings);
        string cachePath = Path.Combine(
            PCL.Desktop.Paths.LauncherPathLayout.ResolveCacheDirectory(),
            "java",
            "runtimes-v1.json");
        JavaRuntimeCatalogCache cache = CacheStores.GetOrAdd(
            cachePath,
            static path => new JavaRuntimeCatalogCache(path));
        JavaRuntimeCatalogLoadResult loaded = await cache.GetOrScanAsync(
                CreateFingerprint(customRoots),
                forceRefresh,
                token => ScanAsync(customRoots, token),
                cancellationToken)
            .ConfigureAwait(false);
        PortableLog.Info(
            "Java",
            loaded.FromCache
                ? $"已从缓存加载 {loaded.Candidates.Count} 个 Java，耗时 {(long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds} ms。"
                : $"Java 扫描完成并已更新缓存，共 {loaded.Candidates.Count} 个候选项，耗时 {(long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds} ms。");

        Dictionary<string, JavaRuntimeCandidate> merged = new(GetPathComparer());
        foreach (JavaRuntimeCandidate candidate in loaded.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = candidate.Installation.JavaExecutablePath;
            bool disabled = settings.GetBooleanOption(
                LauncherSettingKeys.JavaDisabled(candidate.Installation.JavaExecutablePath));
            JavaRuntimeCandidate withState = candidate with
            {
                IsEnabled = !disabled && candidate.IsEnabled,
                IsAvailable = candidate.IsAvailable
            };

            if (!merged.TryGetValue(key, out JavaRuntimeCandidate? existing) ||
                withState.Source == JavaSource.ManualAdded ||
                existing.Source != JavaSource.ManualAdded)
            {
                merged[key] = withState;
            }
        }

        return merged.Values
            .OrderByDescending(static c => c.IsEnabled)
            .ThenByDescending(static c => c.Installation.MajorVersion)
            .ThenBy(static c => c.Installation.Brand.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static c => c.Installation.JavaHome, GetPathComparer())
            .ToArray();
    }

    public static Task<IReadOnlyList<JavaRuntimeCandidate>> LoadAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken = default) =>
        LoadAsync(settings, forceRefresh: false, cancellationToken);

    private static async Task<IReadOnlyList<JavaRuntimeCandidate>> ScanAsync(
        string[] customRoots,
        CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<JavaRuntimeCandidate>> automatic =
            new FileSystemJavaLocator().FindAllAsync(cancellationToken).AsTask();
        Task<IReadOnlyList<JavaRuntimeCandidate>> manual = customRoots.Length == 0
            ? Task.FromResult<IReadOnlyList<JavaRuntimeCandidate>>([])
            : new FileSystemJavaLocator(customRoots).FindAllAsync(cancellationToken).AsTask();
        await Task.WhenAll(automatic, manual).ConfigureAwait(false);

        Dictionary<string, JavaRuntimeCandidate> merged = new(GetPathComparer());
        foreach (JavaRuntimeCandidate candidate in automatic.Result)
            merged[candidate.Installation.JavaExecutablePath] = candidate;
        foreach (JavaRuntimeCandidate candidate in manual.Result)
        {
            JavaRuntimeCandidate manualCandidate = candidate with { Source = JavaSource.ManualAdded };
            merged[manualCandidate.Installation.JavaExecutablePath] = manualCandidate;
        }

        return merged.Values.ToArray();
    }

    private static string CreateFingerprint(IEnumerable<string> customRoots)
    {
        StringBuilder value = new()
        {
            Capacity = 1024
        };
        value.Append(Environment.OSVersion.Platform).Append('|')
            .Append(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture).Append('|')
            .Append(Environment.GetEnvironmentVariable("JAVA_HOME")).Append('|')
            .Append(Environment.GetEnvironmentVariable("PATH"));

        foreach (string root in customRoots.OrderBy(static root => root, GetPathComparer()))
            value.Append('\n').Append(root).Append('|').Append(GetDirectoryStamp(root));

        string managedRuntimeRoot = Path.Combine(
            PCL.Desktop.Paths.LauncherPathLayout.ResolveDataDirectory(),
            "runtime");
        value.Append('\n').Append(managedRuntimeRoot).Append('|').Append(GetDirectoryStamp(managedRuntimeRoot));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static long GetDirectoryStamp(string path)
    {
        try
        {
            DirectoryInfo directory = new(path);
            return directory.Exists ? directory.LastWriteTimeUtc.Ticks : 0L;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0L;
        }
    }

    public static JavaRuntimeCandidate? SelectBest(
        IEnumerable<JavaRuntimeCandidate> candidates,
        JavaVersionRange range) =>
        JavaSelectionService.SelectBestCandidate(
            candidates.Where(static c => c.IsAvailable && c.IsEnabled),
            range);

    public static string[] ReadCustomJavaRoots(LauncherSettings settings)
    {
        if (!settings.TryGetTextOption(LauncherSettingKeys.JavaCustomRoots, out string? raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Distinct(GetPathComparer())
            .ToArray();
    }

    public static bool IsJavaPathEnabled(LauncherSettings settings, string javaExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath))
            return false;
        return !settings.GetBooleanOption(LauncherSettingKeys.JavaDisabled(javaExecutablePath));
    }

    public static bool TryResolveExistingJavaPath(string? path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string trimmed = path.Trim();
        if (File.Exists(trimmed))
        {
            resolvedPath = Path.GetFullPath(trimmed);
            return true;
        }

        // Settings may store java.exe while PreferJavaExecutable wants javaw.exe (or reverse).
        string? directory = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        foreach (string name in OperatingSystem.IsWindows()
                     ? new[] { "javaw.exe", "java.exe" }
                     : new[] { "java" })
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                resolvedPath = Path.GetFullPath(candidate);
                return true;
            }
        }

        return false;
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
