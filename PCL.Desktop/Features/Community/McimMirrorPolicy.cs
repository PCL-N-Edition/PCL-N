// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Features.Community;

internal static class McimMirrorPolicy
{
    private const string Root = "https://mod.mcimirror.top";

    internal static DownloadSourcePreference CurrentPreference =>
        LauncherSettingsPageBinder.LoadSettings().DownloadSource;

    internal static IReadOnlyList<string> ApiCandidates(
        string officialUrl,
        CommunityResourceSource source,
        DownloadSourcePreference preference)
    {
        string mirror = source == CommunityResourceSource.CurseForge
            ? ReplaceRoot(officialUrl, "https://api.curseforge.com", Root + "/curseforge")
            : ReplaceRoot(officialUrl, "https://api.modrinth.com", Root + "/modrinth");
        return Order(officialUrl, mirror, preference);
    }

    internal static IReadOnlyList<string> DownloadCandidates(
        string officialUrl,
        CommunityResourceSource source,
        DownloadSourcePreference preference)
    {
        string mirror = source == CommunityResourceSource.CurseForge
            ? ReplaceCurseForgeCdn(officialUrl)
            : ReplaceRoot(officialUrl, "https://cdn.modrinth.com", Root);
        return Order(officialUrl, mirror, preference);
    }

    private static IReadOnlyList<string> Order(
        string official,
        string mirror,
        DownloadSourcePreference preference) => preference switch
    {
        DownloadSourcePreference.MirrorOnly => [mirror, official],
        DownloadSourcePreference.PreferOfficialWithMirrorFallback => [official, mirror],
        _ => [official]
    };

    private static string ReplaceCurseForgeCdn(string url)
    {
        foreach (string root in new[]
                 {
                     "https://edge.forgecdn.net",
                     "https://media.forgecdn.net",
                     "https://mediafilez.forgecdn.net"
                 })
        {
            if (url.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Root + url[root.Length..];
        }
        return url;
    }

    private static string ReplaceRoot(string url, string officialRoot, string mirrorRoot) =>
        url.StartsWith(officialRoot, StringComparison.OrdinalIgnoreCase)
            ? mirrorRoot + url[officialRoot.Length..]
            : url;
}
