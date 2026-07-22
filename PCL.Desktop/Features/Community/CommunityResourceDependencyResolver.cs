// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Core.Logging;

namespace PCL.Desktop.Features.Community;

public sealed record CommunityResourceDownloadPlanItem(
    CommunityResourceEntry Entry,
    CommunityResourceVersion Version,
    CommunityResourceDownloadFile File,
    bool IsDependency);

public static class CommunityResourceDependencyResolver
{
    public static async Task<IReadOnlyList<CommunityResourceVersion>> EnrichNamesAsync(
        ICommunityResourceCatalog catalog,
        IReadOnlyList<CommunityResourceVersion> versions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Dictionary<(CommunityResourceSource Source, string ProjectId, string VersionId), string> titles = [];
        IEnumerable<(CommunityResourceSource Source, string ProjectId, string VersionId)> keys = versions
            .SelectMany(static version => version.Dependencies)
            .Where(static dependency =>
                !string.IsNullOrWhiteSpace(dependency.ProjectId) ||
                !string.IsNullOrWhiteSpace(dependency.VersionId))
            .Select(static dependency => (
                dependency.Source,
                dependency.ProjectId,
                dependency.VersionId ?? string.Empty))
            .Distinct()
            .Take(40);

        using SemaphoreSlim gate = new(4, 4);
        await Task.WhenAll(keys.Select(async key =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CommunityResourceEntry? project = !string.IsNullOrWhiteSpace(key.ProjectId)
                    ? await TryGetProjectAsync(
                            catalog,
                            key.Source,
                            key.ProjectId,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : (await TryGetVersionAsync(
                            catalog,
                            key.Source,
                            key.VersionId,
                            cancellationToken)
                        .ConfigureAwait(false))?.Entry;
                if (project is not null)
                {
                    lock (titles)
                        titles[key] = project.Title;
                }
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return versions.Select(version => version with
        {
            Dependencies = version.Dependencies.Select(dependency => dependency with
            {
                ProjectTitle = titles.GetValueOrDefault((
                    dependency.Source,
                    dependency.ProjectId,
                    dependency.VersionId ?? string.Empty))
            }).ToArray()
        }).ToArray();
    }

    public static async Task<IReadOnlyList<CommunityResourceDownloadPlanItem>> ResolveRequiredDownloadsAsync(
        ICommunityResourceCatalog catalog,
        CommunityResourceEntry rootEntry,
        CommunityResourceVersion rootVersion,
        CommunityResourceDownloadFile rootFile,
        CommunitySearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rootEntry);
        ArgumentNullException.ThrowIfNull(rootVersion);
        ArgumentNullException.ThrowIfNull(rootFile);
        ArgumentNullException.ThrowIfNull(options);
        PortableLog.Info(
            "CommunityDependency",
            $"开始解析 {rootEntry.Title} 的必需前置；版本={rootVersion.Name}；声明依赖={rootVersion.Dependencies.Count}。");

        List<CommunityResourceDownloadPlanItem> result = [];
        HashSet<(CommunityResourceSource Source, string ProjectId)> visited = [];
        await ResolveAsync(rootEntry, rootVersion, rootFile, isDependency: false).ConfigureAwait(false);
        PortableLog.Info(
            "CommunityDependency",
            $"必需前置解析完成；根资源={rootEntry.Title}；下载项={result.Count}；前置={result.Count(static item => item.IsDependency)}。");
        return result;

        async Task ResolveAsync(
            CommunityResourceEntry entry,
            CommunityResourceVersion version,
            CommunityResourceDownloadFile file,
            bool isDependency)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (CommunityResourceSource Source, string ProjectId) key = (entry.Source, entry.ProjectId);
            if (!visited.Add(key))
            {
                PortableLog.Debug("CommunityDependency", $"跳过已解析依赖：{entry.Source}/{entry.ProjectId}");
                return;
            }

            PortableLog.Debug(
                "CommunityDependency",
                $"解析节点：{entry.Title}；来源={entry.Source}；ProjectId={entry.ProjectId}；Version={version.VersionId}；依赖数={version.Dependencies.Count}。");

            foreach (CommunityResourceDependency dependency in version.Dependencies
                         .Where(static dependency => dependency.Type == CommunityResourceDependencyType.Required))
            {
                CommunityResourceEntry? dependencyEntry;
                CommunityResourceVersion? dependencyVersion = null;
                if (string.IsNullOrWhiteSpace(dependency.ProjectId))
                {
                    CommunityResourceVersionLookupResult? lookup =
                        !string.IsNullOrWhiteSpace(dependency.VersionId)
                            ? await TryGetVersionAsync(
                                    catalog,
                                    dependency.Source,
                                    dependency.VersionId,
                                    cancellationToken)
                                .ConfigureAwait(false)
                            : null;
                    dependencyEntry = lookup?.Entry;
                    dependencyVersion = lookup?.Version;
                    if (dependencyEntry is null || dependencyVersion is null)
                    {
                        throw new InvalidOperationException(
                            $"前置 {dependency.DisplayName} 缺少可解析的项目或版本标识，无法自动下载。");
                    }
                }
                else
                {
                    dependencyEntry = await TryGetProjectAsync(
                            catalog,
                            dependency.Source,
                            dependency.ProjectId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                PortableLog.Debug(
                    "CommunityDependency",
                    $"解析必需前置：{dependency.DisplayName}；来源={dependency.Source}；ProjectId={dependency.ProjectId}；指定版本={dependency.VersionId ?? "(自动)"}。");
                dependencyEntry ??= new CommunityResourceEntry(
                    dependency.ProjectId,
                    dependency.ProjectId,
                    dependency.DisplayName,
                    string.Empty,
                    "mod",
                    null,
                    0,
                    null)
                {
                    Source = dependency.Source
                };

                CommunitySearchOptions dependencyOptions = options with { Source = dependency.Source };
                if (dependencyVersion is null)
                {
                    IReadOnlyList<CommunityResourceVersion> candidates = await catalog.GetVersionsAsync(
                            dependencyEntry,
                            dependencyOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    dependencyVersion = SelectVersion(candidates, dependency.VersionId);
                    if (dependencyVersion is null && !string.IsNullOrWhiteSpace(dependency.VersionId))
                    {
                        candidates = await catalog.GetVersionsAsync(
                                dependencyEntry,
                                dependencyOptions with { GameVersion = null, Loader = null },
                                cancellationToken)
                            .ConfigureAwait(false);
                        dependencyVersion = SelectVersion(candidates, dependency.VersionId);
                    }
                }

                CommunityResourceDownloadFile? dependencyFile = dependencyVersion is { Files.Count: > 0 }
                    ? dependencyVersion.Files[0]
                    : null;
                if (dependencyVersion is null || dependencyFile is null)
                {
                    throw new InvalidOperationException(
                        $"未找到前置 {dependency.DisplayName} 的兼容下载文件。");
                }

                await ResolveAsync(
                        dependencyEntry,
                        dependencyVersion,
                        dependencyFile,
                        isDependency: true)
                    .ConfigureAwait(false);
            }

            result.Add(new CommunityResourceDownloadPlanItem(entry, version, file, isDependency));
        }
    }

    private static CommunityResourceVersion? SelectVersion(
        IReadOnlyList<CommunityResourceVersion> candidates,
        string? requiredVersionId)
    {
        if (!string.IsNullOrWhiteSpace(requiredVersionId))
        {
            return candidates.FirstOrDefault(version =>
                string.Equals(version.VersionId, requiredVersionId, StringComparison.OrdinalIgnoreCase));
        }

        return candidates
            .OrderByDescending(static version => version.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    private static async Task<CommunityResourceEntry?> TryGetProjectAsync(
        ICommunityResourceCatalog catalog,
        CommunityResourceSource source,
        string projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.GetProjectAsync(source, projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            PortableLog.Warn(ex, "CommunityDependency", $"获取前置项目详情失败，将使用依赖声明继续：{source}/{projectId}");
            return null;
        }
    }

    private static async Task<CommunityResourceVersionLookupResult?> TryGetVersionAsync(
        ICommunityResourceCatalog catalog,
        CommunityResourceSource source,
        string versionId,
        CancellationToken cancellationToken)
    {
        if (catalog is not ICommunityResourceVersionLookup lookup || string.IsNullOrWhiteSpace(versionId))
            return null;

        try
        {
            return await lookup.GetVersionAsync(source, versionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            PortableLog.Warn(ex, "CommunityDependency", $"按版本解析前置失败：{source}/{versionId}");
            return null;
        }
    }
}
