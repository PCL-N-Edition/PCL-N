// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using PCL.Core.Utils;

namespace PCL.Desktop.Features.Community;

internal static class McModCommunitySearch
{
    internal static async Task<IReadOnlyList<CommunityResourceEntry>> SearchAsync(
        ICommunityResourceCatalog catalog,
        McModIndex index,
        CommunityResourceCategory category,
        string query,
        CommunitySearchOptions options,
        bool useChineseIndex,
        CancellationToken cancellationToken)
    {
        List<SearchEntry<McModIndexEntry>> matches = useChineseIndex &&
                                                      category is CommunityResourceCategory.Mod or
                                                          CommunityResourceCategory.DataPack
            ? FindChineseMatches(index, query)
            : [];
        SearchPlan plan = matches.Count == 0
            ? new SearchPlan(query, null)
            : BuildSearchPlan(matches, query);
        SearchRequest[] requests = BuildRequests(plan, options);
        Task<SearchResponse>[] tasks = requests
            .Select(request => SearchSourceAsync(
                catalog,
                category,
                request.Query,
                request.Options,
                cancellationToken))
            .ToArray();
        SearchResponse[] responses = await Task.WhenAll(tasks).ConfigureAwait(false);

        IReadOnlyList<CommunityResourceEntry> combined;
        SearchResponse? aggregate = responses.FirstOrDefault(static response =>
            response.Source == CommunityResourceSource.All);
        if (aggregate is not null)
        {
            combined = aggregate.Entries
                .Select(index.Decorate)
                .DistinctBy(GetProjectKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            CommunityResourceEntry[] modrinth = responses
                .Where(static response => response.Source == CommunityResourceSource.Modrinth)
                .SelectMany(static response => response.Entries)
                .ToArray();
            CommunityResourceEntry[] curseForge = responses
                .Where(static response => response.Source == CommunityResourceSource.CurseForge)
                .SelectMany(static response => response.Entries)
                .ToArray();
            combined = CommunityResourceMerge.MergeProjects(modrinth, curseForge, index);
        }

        if (combined.Count == 0)
        {
            Exception[] errors = responses
                .Select(static response => response.Error)
                .OfType<Exception>()
                .ToArray();
            if (errors.Length == 1)
                throw errors[0];
            if (errors.Length > 1)
                throw new AggregateException("社区资源搜索失败。", errors);
        }

        return combined;
    }

    private static List<SearchEntry<McModIndexEntry>> FindChineseMatches(
        McModIndex index,
        string query)
    {
        List<SearchEntry<McModIndexEntry>> candidates = [];
        foreach (McModIndexEntry entry in index.FindChineseCandidates(query))
        {
            if (entry.ChineseName.Contains("动态的树", StringComparison.Ordinal))
                continue;

            List<KeyValuePair<string, double>> sources = [];
            string canonical = BeforeFirst(entry.ChineseName, " (");
            foreach (string alias in canonical.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sources.Add(new KeyValuePair<string, double>(alias, 1d));
            sources.Add(new KeyValuePair<string, double>(
                AfterFirst(entry.ChineseName, " (") +
                (entry.CurseForgeSlug ?? string.Empty) +
                (entry.ModrinthSlug ?? string.Empty),
                0.5d));
            candidates.Add(new SearchEntry<McModIndexEntry>(entry, sources));
        }

        return SimilaritySearch.Search(candidates, query, 40, 0.2d);
    }

    private static SearchPlan BuildSearchPlan(
        IReadOnlyList<SearchEntry<McModIndexEntry>> matches,
        string query)
    {
        Dictionary<string, HashSet<int>> wordProjects = new(StringComparer.OrdinalIgnoreCase);
        foreach (SearchEntry<McModIndexEntry> match in matches)
        {
            foreach (string word in ExtractWords(match.Item))
            {
                if (!wordProjects.TryGetValue(word, out HashSet<int>? projects))
                {
                    projects = [];
                    wordProjects[word] = projects;
                }
                projects.Add(match.Item.WikiId);
            }
        }

        if (wordProjects.Count == 0)
            return new SearchPlan(query, null);

        string normalizedQuery = NormalizeName(query);
        SearchEntry<McModIndexEntry>[] exactNameEntries = matches
            .Where(match => NormalizeName(BeforeFirst(match.Item.ChineseName, " (")) == normalizedQuery)
            .ToArray();
        int exactProjects = exactNameEntries
            .Select(static match => match.Item.WikiId)
            .Distinct()
            .Count();

        string primary;
        string? curseForge = null;
        if (exactProjects == 1)
        {
            SearchEntry<McModIndexEntry> canonical = exactNameEntries
                .OrderByDescending(static match => match.AbsoluteRight)
                .ThenByDescending(static match => match.Similarity)
                .ThenBy(static match => GetShortestIdentityLength(match.Item))
                .First();
            string[] words = ExtractWords(canonical.Item);
            primary = words.Length > 0
                ? string.Join(' ', words)
                : string.Join(
                    ' ',
                    wordProjects
                        .OrderByDescending(static pair => pair.Value.Count)
                        .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                        .Take(2)
                        .Select(static pair => pair.Key));

            string[] curseForgeSlugs = exactNameEntries
                .Select(static match => match.Item.CurseForgeSlug)
                .Where(static slug => !string.IsNullOrWhiteSpace(slug))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (curseForgeSlugs.Length == 1)
                curseForge = curseForgeSlugs[0];
        }
        else
        {
            int maxProjects = wordProjects.Values.Max(static projects => projects.Count);
            if (maxProjects <= 1)
            {
                SearchEntry<McModIndexEntry> best = matches
                    .OrderByDescending(static match => match.AbsoluteRight)
                    .ThenByDescending(static match => match.Similarity)
                    .ThenBy(static match => GetShortestIdentityLength(match.Item))
                    .First();
                primary = string.Join(' ', ExtractWords(best.Item));
            }
            else
            {
                KeyValuePair<string, HashSet<int>>[] tied = wordProjects
                    .Where(pair => pair.Value.Count == maxProjects)
                    .OrderBy(static pair => pair.Key.Length)
                    .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToArray();
                HashSet<int> anchorProjects = tied[0].Value;
                primary = string.Join(
                    ' ',
                    tied
                        .Where(pair => pair.Value.Overlaps(anchorProjects))
                        .Take(3)
                        .Select(static pair => pair.Key));
            }
        }

        return new SearchPlan(string.IsNullOrWhiteSpace(primary) ? query : primary, curseForge);
    }

    private static SearchRequest[] BuildRequests(SearchPlan plan, CommunitySearchOptions options)
    {
        if (options.Source != CommunityResourceSource.All)
        {
            string query = options.Source == CommunityResourceSource.CurseForge &&
                           !string.IsNullOrWhiteSpace(plan.CurseForgeQuery)
                ? plan.CurseForgeQuery
                : plan.PrimaryQuery;
            return [new SearchRequest(query, options)];
        }

        if (string.IsNullOrWhiteSpace(plan.CurseForgeQuery))
            return [new SearchRequest(plan.PrimaryQuery, options)];

        return
        [
            new SearchRequest(
                plan.PrimaryQuery,
                options with { Source = CommunityResourceSource.Modrinth }),
            new SearchRequest(
                plan.CurseForgeQuery,
                options with { Source = CommunityResourceSource.CurseForge })
        ];
    }

    private static async Task<SearchResponse> SearchSourceAsync(
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
            return new SearchResponse(entries, options.Source, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SearchResponse([], options.Source, ex);
        }
    }

    private static string GetProjectKey(CommunityResourceEntry entry)
    {
        CommunityResourceProjectReference? modrinth =
            entry.GetProjectReference(CommunityResourceSource.Modrinth);
        CommunityResourceProjectReference? curseForge =
            entry.GetProjectReference(CommunityResourceSource.CurseForge);
        return (modrinth is null ? string.Empty : "M:" + modrinth.ProjectId) +
               (curseForge is null ? string.Empty : "|C:" + curseForge.ProjectId);
    }

    private static string[] ExtractWords(McModIndexEntry entry)
    {
        string value = string.Join(' ', new[]
        {
            entry.CurseForgeSlug?.Replace('-', ' ').Replace('/', ' '),
            entry.ModrinthSlug?.Replace('-', ' ').Replace('/', ' '),
            ExtractEnglishSuffix(entry.ChineseName)
        }.Where(static item => !string.IsNullOrWhiteSpace(item)));
        return value
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static word => word.TrimStart('{', '[', '(').TrimEnd('}', ']', ')'))
            .Where(IsUsefulWord)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ExtractEnglishSuffix(string chineseName)
    {
        int start = chineseName.LastIndexOf(" (", StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;
        string value = chineseName[(start + 2)..].TrimEnd(')', ' ');
        int separator = value.IndexOf(" - ", StringComparison.Ordinal);
        if (separator >= 0)
            value = value[..separator];
        return value
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .Replace('-', ' ')
            .Replace('/', ' ');
    }

    private static bool IsUsefulWord(string word)
    {
        if (word.Length <= 1 || !word.Any(char.IsLetterOrDigit))
            return false;
        if (new[] { "the", "of", "for", "mod", "and", "forge", "fabric", "quilt", "neoforge" }
            .Contains(word, StringComparer.Ordinal))
        {
            return false;
        }
        return !double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static int GetShortestIdentityLength(McModIndexEntry entry) =>
        (entry.CurseForgeSlug ?? entry.ModrinthSlug ?? entry.ChineseName).Length;

    private static string NormalizeName(string value) =>
        new(value.Where(static character => !char.IsWhiteSpace(character) && !char.IsSurrogate(character)).ToArray());

    private static string BeforeFirst(string value, string separator)
    {
        int index = value.IndexOf(separator, StringComparison.Ordinal);
        return index < 0 ? value : value[..index];
    }

    private static string AfterFirst(string value, string separator)
    {
        int index = value.IndexOf(separator, StringComparison.Ordinal);
        return index < 0 ? string.Empty : value[(index + separator.Length)..];
    }

    private sealed record SearchPlan(string PrimaryQuery, string? CurseForgeQuery);

    private sealed record SearchRequest(string Query, CommunitySearchOptions Options);

    private sealed record SearchResponse(
        IReadOnlyList<CommunityResourceEntry> Entries,
        CommunityResourceSource Source,
        Exception? Error);
}
