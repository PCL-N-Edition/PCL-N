// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Community;

public enum CommunityResourceCategory
{
    Mod,
    Modpack,
    DataPack,
    ResourcePack,
    Shader,
    World
}

public enum CommunityResourceSort
{
    Relevance,
    Downloads,
    Updated
}

public enum CommunityResourceSource
{
    All,
    Modrinth,
    CurseForge
}

public sealed record CommunitySearchOptions(
    CommunityResourceSort Sort = CommunityResourceSort.Relevance,
    string? GameVersion = null,
    string? Loader = null,
    string? Tag = null,
    CommunityResourceSource Source = CommunityResourceSource.All);

public sealed record CommunityResourceProjectReference(
    CommunityResourceSource Source,
    string ProjectId,
    string Slug,
    string WebsiteUrl);

public sealed record CommunityResourceEntry(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string ProjectType,
    string? IconUrl,
    long Downloads,
    DateTimeOffset? UpdatedAt)
{
    public CommunityResourceSource Source { get; init; } = CommunityResourceSource.Modrinth;
    public string? ProjectUrl { get; init; }
    public int? WikiId { get; init; }
    public string? ChineseName { get; init; }
    public string? OriginalTitle { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public CommunityResourceProjectReference? ModrinthProject { get; init; }
    public CommunityResourceProjectReference? CurseForgeProject { get; init; }

    public string DisplayTitle =>
        AvaloniaLocalizationManager.CurrentLanguageCode == AvaloniaLocalizationManager.ChineseLanguage &&
        !string.IsNullOrWhiteSpace(ChineseName)
            ? ChineseName
            : Title;

    public string DisplayDescription =>
        !string.IsNullOrWhiteSpace(OriginalTitle) &&
        !string.Equals(DisplayTitle, OriginalTitle, StringComparison.Ordinal)
            ? OriginalTitle + " · " + Description
            : Description;

    public string? McModUrl => WikiId is > 0 ? $"https://www.mcmod.cn/class/{WikiId.Value}.html" : null;

    public string SourceDisplayName =>
        ModrinthProject is not null && CurseForgeProject is not null
            ? "Modrinth + CurseForge"
            : Source switch
            {
                CommunityResourceSource.All => "Modrinth + CurseForge",
                CommunityResourceSource.CurseForge => "CurseForge",
                _ => "Modrinth"
            };

    public string WebsiteUrl => ProjectUrl ?? (Source == CommunityResourceSource.CurseForge
        ? "https://www.curseforge.com/minecraft/" + CurseForgeProjectPath(ProjectType) + "/" +
          (string.IsNullOrWhiteSpace(Slug) ? ProjectId : Slug)
        : "https://modrinth.com/" + ProjectType + "/" + (string.IsNullOrWhiteSpace(Slug) ? ProjectId : Slug));

    public CommunityResourceProjectReference? GetProjectReference(CommunityResourceSource source)
    {
        CommunityResourceProjectReference? reference = source switch
        {
            CommunityResourceSource.Modrinth => ModrinthProject,
            CommunityResourceSource.CurseForge => CurseForgeProject,
            _ => null
        };
        if (reference is not null || Source != source)
            return reference;
        return new CommunityResourceProjectReference(source, ProjectId, Slug, WebsiteUrl);
    }

    private static string CurseForgeProjectPath(string projectType) => projectType.ToLowerInvariant() switch
    {
        "modpack" => "modpacks",
        "resourcepack" => "texture-packs",
        "shader" => "shaders",
        "datapack" => "data-packs",
        "world" => "worlds",
        _ => "mc-mods"
    };
}

public sealed record CommunityResourceDownloadFile(
    string FileName,
    string Url,
    long Size,
    string VersionId,
    string VersionName)
{
    public IReadOnlyList<string> CandidateUrls { get; init; } = [Url];
    public CommunityResourceSource Source { get; init; } = CommunityResourceSource.Modrinth;
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
}

public enum CommunityResourceDependencyType
{
    Required,
    Optional,
    Incompatible,
    Embedded,
    Tool,
    Unknown
}

public sealed record CommunityResourceDependency(
    string ProjectId,
    string? VersionId,
    string? FileName,
    CommunityResourceDependencyType Type,
    CommunityResourceSource Source)
{
    public string? ProjectTitle { get; init; }
    public string DisplayName => !string.IsNullOrWhiteSpace(ProjectTitle)
        ? ProjectTitle
        : !string.IsNullOrWhiteSpace(FileName) ? FileName : ProjectId;
}

public sealed record CommunityResourceVersion(
    string VersionId,
    string Name,
    string VersionNumber,
    string? Changelog,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<CommunityResourceDownloadFile> Files)
{
    public IReadOnlyList<CommunityResourceDependency> Dependencies { get; init; } = [];
    public CommunityResourceSource Source { get; init; } = CommunityResourceSource.Modrinth;
}

public sealed record CommunityResourceFileIdentity(
    string ProjectId,
    string ProjectSlug,
    string ProjectTitle,
    string ProjectType,
    string VersionId,
    string VersionNumber,
    DateTimeOffset? PublishedAt,
    string? IconUrl,
    string WebsiteUrl)
{
    public CommunityResourceSource Source { get; init; } = CommunityResourceSource.Modrinth;
    public CommunityResourceDownloadFile? CurrentFile { get; init; }
}

public sealed record CommunityResourceFileMatches(
    CommunityResourceFileIdentity? Modrinth,
    CommunityResourceFileIdentity? CurseForge);

public sealed record CommunityResourceUpdateCandidate(
    CommunityResourceFileIdentity Current,
    CommunityResourceVersion Latest,
    CommunityResourceDownloadFile PrimaryFile);

public sealed record CommunityResourceVersionLookupResult(
    CommunityResourceEntry Entry,
    CommunityResourceVersion Version);

public interface ICommunityResourceVersionLookup
{
    Task<CommunityResourceVersionLookupResult?> GetVersionAsync(
        CommunityResourceSource source,
        string versionId,
        CancellationToken cancellationToken = default);
}

public interface ICommunityResourceFingerprintLookup
{
    Task<CommunityResourceFileIdentity?> LookupFileByFingerprintAsync(
        uint fingerprint,
        CancellationToken cancellationToken = default);
}

public interface ICommunityResourceCatalog
{
    Task<IReadOnlyList<CommunityResourceEntry>> SearchAsync(
        CommunityResourceCategory category,
        string query,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<CommunityResourceDownloadFile?> ResolveDownloadAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommunityResourceVersion>> GetVersionsAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<CommunityResourceEntry?> GetProjectAsync(
        CommunityResourceSource source,
        string projectId,
        CancellationToken cancellationToken = default);

    Task<CommunityResourceFileIdentity?> LookupFileBySha1Async(
        string sha1Hex,
        CancellationToken cancellationToken = default);

    Task<CommunityResourceVersion?> GetLatestVersionAsync(
        string projectId,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record McimTranslationResult(string? Text, bool NotFound = false, bool FromCache = false);

internal interface ICommunityTranslationService
{
    Task<McimTranslationResult> GetAsync(
        CommunityResourceEntry entry,
        CancellationToken cancellationToken = default);
}

internal interface ICommunityArtifactDownloader
{
    Task DownloadAsync(
        IReadOnlyList<string> candidateUrls,
        string targetPath,
        Action<long, long?> reportProgress,
        CancellationToken cancellationToken = default);
}
