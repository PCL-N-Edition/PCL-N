// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;

namespace PCL.Desktop.Features.Community;

internal static class CommunityResourceMerge
{
    public static CommunityResourceEntry MergeKnownProjectPair(
        CommunityResourceEntry modrinth,
        CommunityResourceEntry curseForge,
        McModIndex? index = null) =>
        MergeProject(
            PrepareProject(modrinth, CommunityResourceSource.Modrinth, index),
            PrepareProject(curseForge, CommunityResourceSource.CurseForge, index));

    public static IReadOnlyList<CommunityResourceEntry> MergeProjects(
        IReadOnlyList<CommunityResourceEntry> modrinthResults,
        IReadOnlyList<CommunityResourceEntry> curseForgeResults,
        McModIndex? index = null)
    {
        ArgumentNullException.ThrowIfNull(modrinthResults);
        ArgumentNullException.ThrowIfNull(curseForgeResults);

        CommunityResourceEntry[] modrinth = modrinthResults
            .Select(entry => PrepareProject(entry, CommunityResourceSource.Modrinth, index))
            .ToArray();
        CommunityResourceEntry[] curseForge = curseForgeResults
            .Select(entry => PrepareProject(entry, CommunityResourceSource.CurseForge, index))
            .ToArray();
        bool[] pairedCurseForge = new bool[curseForge.Length];
        List<RankedProject> projects = [];

        for (int modrinthRank = 0; modrinthRank < modrinth.Length; modrinthRank++)
        {
            CommunityResourceEntry modrinthEntry = modrinth[modrinthRank];
            int curseForgeRank = FindPair(modrinthEntry, curseForge, pairedCurseForge);
            if (curseForgeRank >= 0)
            {
                pairedCurseForge[curseForgeRank] = true;
                projects.Add(new RankedProject(
                    MergeProject(modrinthEntry, curseForge[curseForgeRank]),
                    modrinthRank + curseForgeRank,
                    Math.Min(modrinthRank, curseForgeRank)));
            }
            else
            {
                projects.Add(new RankedProject(
                    modrinthEntry,
                    modrinthRank + curseForge.Length,
                    modrinthRank));
            }
        }

        for (int curseForgeRank = 0; curseForgeRank < curseForge.Length; curseForgeRank++)
        {
            if (pairedCurseForge[curseForgeRank])
                continue;
            projects.Add(new RankedProject(
                curseForge[curseForgeRank],
                modrinth.Length + curseForgeRank,
                curseForgeRank));
        }

        return projects
            .OrderBy(static project => project.CombinedRank)
            .ThenBy(static project => project.BestRank)
            .ThenByDescending(static project => project.Entry.Downloads)
            .ThenByDescending(static project => project.Entry.UpdatedAt ?? DateTimeOffset.MinValue)
            .Select(static project => project.Entry)
            .ToArray();
    }

    public static IReadOnlyList<CommunityResourceVersion> MergeVersions(
        IReadOnlyList<CommunityResourceVersion> modrinthResults,
        IReadOnlyList<CommunityResourceVersion> curseForgeResults)
    {
        ArgumentNullException.ThrowIfNull(modrinthResults);
        ArgumentNullException.ThrowIfNull(curseForgeResults);

        List<CommunityResourceVersion> merged = modrinthResults
            .Select(version => PrepareVersion(version, CommunityResourceSource.Modrinth))
            .ToList();
        HashSet<string> seenCurseForgeIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (CommunityResourceVersion rawVersion in curseForgeResults)
        {
            CommunityResourceVersion curseForgeVersion = PrepareVersion(
                rawVersion,
                CommunityResourceSource.CurseForge);
            if (!seenCurseForgeIds.Add(curseForgeVersion.VersionId))
                continue;

            int duplicateIndex = merged.FindIndex(existing =>
                existing.Source != CommunityResourceSource.CurseForge &&
                IsSamePublishedArtifact(existing, curseForgeVersion));
            if (duplicateIndex >= 0)
                merged[duplicateIndex] = MergeDuplicateVersions(merged[duplicateIndex], curseForgeVersion);
            else
                merged.Add(curseForgeVersion);
        }

        return merged
            .OrderByDescending(static version => version.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(static version => SourceOrder(version.Source))
            .ThenBy(static version => version.VersionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string? NormalizeSha256(string? value)
        => NormalizeHash(value, 64);

    internal static string? NormalizeSha1(string? value)
        => NormalizeHash(value, 40);

    private static string? NormalizeHash(string? value, int expectedLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == expectedLength && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }

    private static CommunityResourceEntry PrepareProject(
        CommunityResourceEntry entry,
        CommunityResourceSource source,
        McModIndex? index)
    {
        CommunityResourceEntry prepared = entry.Source == source ? entry : entry with { Source = source };
        prepared = index?.Decorate(prepared) ?? prepared;
        CommunityResourceProjectReference reference = new(
            source,
            prepared.ProjectId,
            prepared.Slug,
            prepared.WebsiteUrl);
        return source == CommunityResourceSource.CurseForge
            ? prepared with { CurseForgeProject = prepared.CurseForgeProject ?? reference }
            : prepared with { ModrinthProject = prepared.ModrinthProject ?? reference };
    }

    private static int FindPair(
        CommunityResourceEntry modrinth,
        CommunityResourceEntry[] curseForge,
        bool[] paired)
    {
        int bestIndex = -1;
        int bestConfidence = 0;
        for (int index = 0; index < curseForge.Length; index++)
        {
            if (paired[index])
                continue;
            int confidence = GetMatchConfidence(modrinth, curseForge[index]);
            if (confidence <= bestConfidence)
                continue;
            bestIndex = index;
            bestConfidence = confidence;
        }
        return bestIndex;
    }

    private static int GetMatchConfidence(
        CommunityResourceEntry modrinth,
        CommunityResourceEntry curseForge)
    {
        if (!string.Equals(modrinth.ProjectType, curseForge.ProjectType, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (modrinth.WikiId is > 0 && curseForge.WikiId is > 0 && modrinth.WikiId != curseForge.WikiId)
            return 0;
        if (modrinth.WikiId is > 0 && modrinth.WikiId == curseForge.WikiId)
            return 4;

        string modrinthSlug = NormalizeIdentity(modrinth.Slug);
        string curseForgeSlug = NormalizeIdentity(curseForge.Slug);
        if (modrinthSlug.Length > 0 && modrinthSlug == curseForgeSlug)
            return 3;
        if (!UpdatesAreCompatible(modrinth.UpdatedAt, curseForge.UpdatedAt))
            return 0;

        string modrinthTitle = NormalizeIdentity(modrinth.Title);
        string curseForgeTitle = NormalizeIdentity(curseForge.Title);
        if (modrinthTitle.Length > 0 && modrinthTitle == curseForgeTitle)
            return 2;

        string modrinthDescription = NormalizeIdentity(modrinth.Description);
        string curseForgeDescription = NormalizeIdentity(curseForge.Description);
        return modrinthDescription.Length >= 16 && modrinthDescription == curseForgeDescription ? 1 : 0;
    }

    private static CommunityResourceEntry MergeProject(
        CommunityResourceEntry modrinth,
        CommunityResourceEntry curseForge)
    {
        long downloads = modrinth.Downloads > long.MaxValue - curseForge.Downloads
            ? long.MaxValue
            : modrinth.Downloads + curseForge.Downloads;
        return modrinth with
        {
            Title = FirstNotEmpty(modrinth.Title, curseForge.Title),
            Description = FirstNotEmpty(modrinth.Description, curseForge.Description),
            IconUrl = FirstNotEmpty(modrinth.IconUrl, curseForge.IconUrl),
            Downloads = downloads,
            UpdatedAt = Max(modrinth.UpdatedAt, curseForge.UpdatedAt),
            Source = CommunityResourceSource.Modrinth,
            ProjectUrl = modrinth.WebsiteUrl,
            WikiId = modrinth.WikiId ?? curseForge.WikiId,
            ChineseName = FirstNotEmpty(modrinth.ChineseName, curseForge.ChineseName),
            OriginalTitle = FirstNotEmpty(modrinth.OriginalTitle, curseForge.OriginalTitle),
            Tags = modrinth.Tags
                .Concat(curseForge.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ModrinthProject = modrinth.GetProjectReference(CommunityResourceSource.Modrinth),
            CurseForgeProject = curseForge.GetProjectReference(CommunityResourceSource.CurseForge)
        };
    }

    private static CommunityResourceVersion PrepareVersion(
        CommunityResourceVersion version,
        CommunityResourceSource source) =>
        version with
        {
            Source = source,
            Files = version.Files
                .Select(file => file with
                {
                    Source = source,
                    Sha1 = NormalizeSha1(file.Sha1),
                    Sha256 = NormalizeSha256(file.Sha256)
                })
                .ToArray()
        };

    private static bool IsSamePublishedArtifact(
        CommunityResourceVersion left,
        CommunityResourceVersion right)
    {
        if (left.PublishedAt is not { } leftPublished || right.PublishedAt is not { } rightPublished ||
            leftPublished.ToUniversalTime() != rightPublished.ToUniversalTime())
        {
            return false;
        }

        return left.Files.Any(leftFile =>
            right.Files.Any(rightFile => HasSameContentHash(leftFile, rightFile)));
    }

    private static CommunityResourceVersion MergeDuplicateVersions(
        CommunityResourceVersion modrinth,
        CommunityResourceVersion curseForge)
    {
        List<CommunityResourceDownloadFile> files = [.. modrinth.Files];
        foreach (CommunityResourceDownloadFile curseForgeFile in curseForge.Files)
        {
            int duplicateIndex = files.FindIndex(file => HasSameContentHash(file, curseForgeFile));
            if (duplicateIndex < 0)
            {
                files.Add(curseForgeFile);
                continue;
            }

            CommunityResourceDownloadFile preferred = files[duplicateIndex];
            files[duplicateIndex] = preferred with
            {
                Source = CommunityResourceSource.All,
                CandidateUrls = preferred.CandidateUrls
                    .Append(preferred.Url)
                    .Concat(curseForgeFile.CandidateUrls)
                    .Append(curseForgeFile.Url)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }

        List<CommunityResourceDependency> dependencies = [];
        foreach (CommunityResourceDependency dependency in modrinth.Dependencies.Concat(curseForge.Dependencies))
        {
            if (dependencies.Any(existing =>
                existing.Source == dependency.Source &&
                string.Equals(existing.ProjectId, dependency.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.VersionId, dependency.VersionId, StringComparison.OrdinalIgnoreCase) &&
                existing.Type == dependency.Type))
            {
                continue;
            }
            dependencies.Add(dependency);
        }

        return modrinth with
        {
            Name = FirstNotEmpty(modrinth.Name, curseForge.Name),
            VersionNumber = FirstNotEmpty(modrinth.VersionNumber, curseForge.VersionNumber),
            Changelog = FirstNotEmpty(modrinth.Changelog, curseForge.Changelog),
            GameVersions = modrinth.GameVersions
                .Concat(curseForge.GameVersions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Loaders = modrinth.Loaders
                .Concat(curseForge.Loaders)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Files = files,
            Dependencies = dependencies,
            Source = CommunityResourceSource.All
        };
    }

    private static bool HasSameContentHash(
        CommunityResourceDownloadFile left,
        CommunityResourceDownloadFile right)
    {
        if (left.Sha256 is not null && right.Sha256 is not null)
        {
            return string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        return left.Sha1 is not null && right.Sha1 is not null &&
               string.Equals(left.Sha1, right.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        StringBuilder normalized = new(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
                normalized.Append(char.ToLowerInvariant(character));
        }
        return normalized.ToString();
    }

    private static bool UpdatesAreCompatible(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null || right is null || Math.Abs((left.Value - right.Value).TotalDays) <= 7d;

    private static string FirstNotEmpty(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback ?? string.Empty : preferred;

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return left >= right ? left : right;
    }

    private static int SourceOrder(CommunityResourceSource source) => source switch
    {
        CommunityResourceSource.All => 0,
        CommunityResourceSource.Modrinth => 1,
        _ => 2
    };

    private sealed record RankedProject(
        CommunityResourceEntry Entry,
        int CombinedRank,
        int BestRank);
}
