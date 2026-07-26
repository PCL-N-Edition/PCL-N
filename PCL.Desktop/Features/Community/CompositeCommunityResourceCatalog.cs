// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using PCL.Core.Logging;

namespace PCL.Desktop.Features.Community;

public sealed class CompositeCommunityResourceCatalog :
    ICommunityResourceCatalog,
    ICommunityResourceVersionLookup,
    IDisposable
{
    private readonly ICommunityResourceCatalog _modrinth;
    private readonly ICommunityResourceCatalog _curseForge;
    private readonly bool _ownsCatalogs;

    public CompositeCommunityResourceCatalog()
        : this(CommunityOnlineProviderRegistry.CreateCatalogs())
    {
    }

    private CompositeCommunityResourceCatalog(
        (ICommunityResourceCatalog Modrinth, ICommunityResourceCatalog CurseForge) catalogs)
        : this(catalogs.Modrinth, catalogs.CurseForge, ownsCatalogs: true)
    {
    }

    public CompositeCommunityResourceCatalog(
        ICommunityResourceCatalog modrinth,
        ICommunityResourceCatalog curseForge,
        bool ownsCatalogs = false)
    {
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
        _curseForge = curseForge ?? throw new ArgumentNullException(nameof(curseForge));
        _ownsCatalogs = ownsCatalogs;
    }

    public async Task<IReadOnlyList<CommunityResourceEntry>> SearchAsync(
        CommunityResourceCategory category,
        string query,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CommunitySearchOptions();
        PortableLog.Info("Community", $"开始搜索社区资源；分类={category}；来源={options.Source}；关键词={query}。");
        PortableLog.Debug(
            "Community",
            $"搜索参数：Sort={options.Sort}；GameVersion={options.GameVersion ?? "(全部)"}；Loader={options.Loader ?? "(全部)"}；Tag={options.Tag ?? "(无)"}。");
        if (options.Source == CommunityResourceSource.Modrinth)
            return await _modrinth.SearchAsync(category, query, options, cancellationToken).ConfigureAwait(false);
        if (options.Source == CommunityResourceSource.CurseForge)
        {
            // Do not swallow CurseForge-only failures (missing API key, 400, etc.).
            return await _curseForge.SearchAsync(category, query, options, cancellationToken).ConfigureAwait(false);
        }

        Task<(IReadOnlyList<CommunityResourceEntry> Entries, Exception? Error)> modrinthTask =
            TrySearchWithErrorAsync(_modrinth, category, query, options, cancellationToken);
        Task<(IReadOnlyList<CommunityResourceEntry> Entries, Exception? Error)> curseForgeTask =
            TrySearchWithErrorAsync(_curseForge, category, query, options, cancellationToken);
        await Task.WhenAll(modrinthTask, curseForgeTask).ConfigureAwait(false);
        (IReadOnlyList<CommunityResourceEntry> modrinth, Exception? modrinthError) =
            await modrinthTask.ConfigureAwait(false);
        (IReadOnlyList<CommunityResourceEntry> curseForge, Exception? curseError) =
            await curseForgeTask.ConfigureAwait(false);
        if (modrinth.Count == 0 && curseForge.Count == 0)
        {
            // Prefer surfacing CurseForge config/network errors when both sources fail,
            // otherwise re-run Modrinth so the user still sees a concrete message.
            if (curseError is not null && modrinthError is not null)
                throw new AggregateException("社区资源搜索失败。", curseError, modrinthError);
            if (curseError is not null)
                throw curseError;
            if (modrinthError is not null)
                throw modrinthError;
            return await _modrinth.SearchAsync(category, query, options, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<CommunityResourceEntry> combined = CommunityResourceMerge.MergeProjects(
            modrinth,
            curseForge,
            McModIndex.Current);
        PortableLog.Info("Community", $"社区资源搜索完成；Modrinth={modrinth.Count}；CurseForge={curseForge.Count}；合并后={combined.Count}。");
        return combined;
    }

    public async Task<CommunityResourceDownloadFile?> ResolveDownloadAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CommunityResourceVersion> versions = await GetVersionsAsync(
                entry,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        return versions.SelectMany(static version => version.Files).FirstOrDefault();
    }

    public async Task<IReadOnlyList<CommunityResourceVersion>> GetVersionsAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new CommunitySearchOptions();
        bool hasBothSources = entry.GetProjectReference(CommunityResourceSource.Modrinth) is not null &&
                              entry.GetProjectReference(CommunityResourceSource.CurseForge) is not null;
        if (options.Source != CommunityResourceSource.All || !hasBothSources)
        {
            CommunityResourceSource source = options.Source != CommunityResourceSource.All
                ? options.Source
                : entry.Source;
            CommunityResourceEntry? sourceEntry = CreateSourceEntry(entry, source);
            if (sourceEntry is null)
                return [];
            return await Select(source)
                .GetVersionsAsync(sourceEntry, options with { Source = source }, cancellationToken)
                .ConfigureAwait(false);
        }

        CommunityResourceEntry? modrinthEntry = CreateSourceEntry(entry, CommunityResourceSource.Modrinth);
        CommunityResourceEntry? curseForgeEntry = CreateSourceEntry(entry, CommunityResourceSource.CurseForge);
        Task<(IReadOnlyList<CommunityResourceVersion> Versions, Exception? Error)> modrinthTask =
            TryGetVersionsWithErrorAsync(
                _modrinth,
                modrinthEntry,
                options with { Source = CommunityResourceSource.Modrinth },
                cancellationToken);
        Task<(IReadOnlyList<CommunityResourceVersion> Versions, Exception? Error)> curseForgeTask =
            TryGetVersionsWithErrorAsync(
                _curseForge,
                curseForgeEntry,
                options with { Source = CommunityResourceSource.CurseForge },
                cancellationToken);
        await Task.WhenAll(modrinthTask, curseForgeTask).ConfigureAwait(false);
        (IReadOnlyList<CommunityResourceVersion> modrinth, Exception? modrinthError) =
            await modrinthTask.ConfigureAwait(false);
        (IReadOnlyList<CommunityResourceVersion> curseForge, Exception? curseForgeError) =
            await curseForgeTask.ConfigureAwait(false);
        if (modrinth.Count == 0 && curseForge.Count == 0)
        {
            if (modrinthError is not null && curseForgeError is not null)
                throw new AggregateException("社区资源版本加载失败。", modrinthError, curseForgeError);
            if (modrinthError is not null)
                throw modrinthError;
            if (curseForgeError is not null)
                throw curseForgeError;
        }

        IReadOnlyList<CommunityResourceVersion> merged = CommunityResourceMerge.MergeVersions(
            modrinth,
            curseForge);
        PortableLog.Info(
            "Community",
            $"社区资源版本加载完成；Modrinth={modrinth.Count}；CurseForge={curseForge.Count}；合并后={merged.Count}。");
        return merged;
    }

    public Task<CommunityResourceEntry?> GetProjectAsync(
        CommunityResourceSource source,
        string projectId,
        CancellationToken cancellationToken = default) =>
        (source == CommunityResourceSource.CurseForge ? _curseForge : _modrinth)
        .GetProjectAsync(source, projectId, cancellationToken);

    public Task<CommunityResourceVersionLookupResult?> GetVersionAsync(
        CommunityResourceSource source,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ICommunityResourceCatalog catalog = source == CommunityResourceSource.CurseForge
            ? _curseForge
            : _modrinth;
        return catalog is ICommunityResourceVersionLookup lookup
            ? lookup.GetVersionAsync(source, versionId, cancellationToken)
            : Task.FromResult<CommunityResourceVersionLookupResult?>(null);
    }

    public Task<CommunityResourceFileIdentity?> LookupFileBySha1Async(
        string sha1Hex,
        CancellationToken cancellationToken = default) =>
        _modrinth.LookupFileBySha1Async(sha1Hex, cancellationToken);

    public Task<CommunityResourceFileIdentity?> LookupFileByFingerprintAsync(
        uint fingerprint,
        CancellationToken cancellationToken = default) =>
        _curseForge is ICommunityResourceFingerprintLookup lookup
            ? lookup.LookupFileByFingerprintAsync(fingerprint, cancellationToken)
            : Task.FromResult<CommunityResourceFileIdentity?>(null);

    public async Task<CommunityResourceFileMatches> LookupFilesAsync(
        string sha1Hex,
        uint? curseForgeFingerprint,
        bool modrinthOnly = false,
        CancellationToken cancellationToken = default)
    {
        Task<CommunityResourceFileIdentity?> modrinthTask = TryLookupAsync(
            () => _modrinth.LookupFileBySha1Async(sha1Hex, cancellationToken),
            cancellationToken);
        Task<CommunityResourceFileIdentity?> curseForgeTask =
            modrinthOnly || curseForgeFingerprint is null
                ? Task.FromResult<CommunityResourceFileIdentity?>(null)
                : TryLookupAsync(
                    () => LookupFileByFingerprintAsync(curseForgeFingerprint.Value, cancellationToken),
                    cancellationToken);
        await Task.WhenAll(modrinthTask, curseForgeTask).ConfigureAwait(false);
        return new CommunityResourceFileMatches(
            await modrinthTask.ConfigureAwait(false),
            await curseForgeTask.ConfigureAwait(false));
    }

    public Task<CommunityResourceVersion?> GetLatestVersionAsync(
        string projectId,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _modrinth.GetLatestVersionAsync(projectId, options, cancellationToken);

    public void Dispose()
    {
        if (!_ownsCatalogs)
            return;
        (_modrinth as IDisposable)?.Dispose();
        (_curseForge as IDisposable)?.Dispose();
    }

    private ICommunityResourceCatalog Select(CommunityResourceSource source) =>
        source == CommunityResourceSource.CurseForge ? _curseForge : _modrinth;

    private static CommunityResourceEntry? CreateSourceEntry(
        CommunityResourceEntry entry,
        CommunityResourceSource source)
    {
        CommunityResourceProjectReference? reference = entry.GetProjectReference(source);
        return reference is null
            ? null
            : entry with
            {
                ProjectId = reference.ProjectId,
                Slug = reference.Slug,
                Source = source,
                ProjectUrl = reference.WebsiteUrl
            };
    }

    private static async Task<(IReadOnlyList<CommunityResourceEntry> Entries, Exception? Error)> TrySearchWithErrorAsync(
        ICommunityResourceCatalog catalog,
        CommunityResourceCategory category,
        string query,
        CommunitySearchOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<CommunityResourceEntry> entries =
                await catalog.SearchAsync(category, query, options, cancellationToken).ConfigureAwait(false);
            return (entries, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PortableLog.Warn(ex, "Community", $"社区资源来源 {catalog.GetType().Name} 搜索失败，将保留其他来源结果。");
            return ([], ex);
        }
    }

    private static async Task<(IReadOnlyList<CommunityResourceVersion> Versions, Exception? Error)> TryGetVersionsWithErrorAsync(
        ICommunityResourceCatalog catalog,
        CommunityResourceEntry? entry,
        CommunitySearchOptions options,
        CancellationToken cancellationToken)
    {
        if (entry is null)
            return ([], null);
        try
        {
            IReadOnlyList<CommunityResourceVersion> versions =
                await catalog.GetVersionsAsync(entry, options, cancellationToken).ConfigureAwait(false);
            return (versions, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or IOException or TimeoutException)
        {
            PortableLog.Warn(ex, "Community", $"{entry.Source} 资源版本加载失败，将保留其他来源结果。");
            return ([], ex);
        }
    }

    private static async Task<CommunityResourceFileIdentity?> TryLookupAsync(
        Func<Task<CommunityResourceFileIdentity?>> lookup,
        CancellationToken cancellationToken)
    {
        try
        {
            return await lookup().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or IOException or TimeoutException)
        {
            PortableLog.Warn(ex, "Community", "本地资源文件在线识别失败，将保留其他来源结果。");
            return null;
        }
    }
}
