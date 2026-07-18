// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

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
        IReadOnlyList<McModIndexEntry> matches = useChineseIndex &&
                                                 category is CommunityResourceCategory.Mod or CommunityResourceCategory.DataPack
            ? index.SearchChinese(query, 8)
            : [];
        List<string> terms = [query];
        foreach (McModIndexEntry match in matches)
        {
            IEnumerable<string> slugs = options.Source switch
            {
                CommunityResourceSource.CurseForge => [match.CurseForgeSlug ?? string.Empty],
                CommunityResourceSource.Modrinth => [match.ModrinthSlug ?? string.Empty],
                _ => match.Slugs
            };
            foreach (string slug in slugs.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!terms.Contains(slug, StringComparer.OrdinalIgnoreCase))
                    terms.Add(slug);
            }
        }

        List<CommunityResourceEntry> combined = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string term in terms.Take(9))
        {
            IReadOnlyList<CommunityResourceEntry> result =
                await catalog.SearchAsync(category, term, options, cancellationToken).ConfigureAwait(false);
            foreach (CommunityResourceEntry raw in result)
            {
                CommunityResourceEntry entry = index.Decorate(raw);
                if (seen.Add(entry.Source + ":" + entry.ProjectId))
                    combined.Add(entry);
            }
        }

        return combined
            .OrderBy(entry => Rank(entry, query, matches))
            .ThenByDescending(static entry => entry.Downloads)
            .ToArray();
    }

    private static int Rank(
        CommunityResourceEntry entry,
        string query,
        IReadOnlyList<McModIndexEntry> matches)
    {
        if (!string.IsNullOrWhiteSpace(entry.ChineseName))
        {
            if (entry.ChineseName.Equals(query, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (entry.ChineseName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 1;
            if (entry.ChineseName.Contains(query, StringComparison.OrdinalIgnoreCase))
                return 2;
        }
        if (matches.Any(match => match.Slugs.Contains(entry.Slug, StringComparer.OrdinalIgnoreCase)))
            return 3;
        return 4;
    }
}
