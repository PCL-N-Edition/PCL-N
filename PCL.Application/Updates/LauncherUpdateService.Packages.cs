// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text.Json;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

public sealed partial class LauncherUpdateService
{
    private async Task<LauncherUpdatePackage> ResolveUpdatePackageAsync(
        string targetTag,
        UpdateChannel channel,
        LauncherBuildIdentity identity,
        CancellationToken cancellationToken)
    {
        LauncherUpdatePackage fallback = BuildFullPackage(targetTag, channel, identity);
        LauncherBuildIdentity targetIdentity = ResolvePublishedIdentity(identity);
        LoadedPatchIndex? targetIndex = await TryLoadPatchIndexAsync(targetTag, cancellationToken).ConfigureAwait(false);
        if (targetIndex is null)
        {
            PortableLog.Warn("Update", $"发布 {targetTag} 没有可读的补丁索引，将使用完整包。");
            return fallback;
        }

        LauncherPatchVariantDto? targetVariant = FindVariant(targetIndex.Index, targetIdentity);
        if (targetVariant is null || string.IsNullOrWhiteSpace(targetVariant.TargetAssetName))
        {
            PortableLog.Warn(
                "Update",
                $"补丁索引没有匹配变体：{targetIdentity.RuntimeId}/{targetIdentity.NormalizedRuntimeVariant}，将使用完整包。");
            return fallback;
        }

        string normalizedCurrent = NormalizeVersion(identity.Version);
        string normalizedTarget = NormalizeVersion(targetIndex.Index.TargetVersion ?? targetTag);
        List<LoadedPatchIndex> indexes = [targetIndex];
        // Patch graph preserves the plugin sidecar runtime choice
        // (SelfContained / NoRuntime); the host itself is always NativeAOT.
        bool canPatchCurrentBuild = true;
        List<LauncherUpdatePatchStep> path = FindPatchPath(indexes, targetIdentity, normalizedCurrent, normalizedTarget);

        // Format 2 keeps only a direct window. Walk backwards through each index's oldest
        // selected tag until a route is found; this implements the documented 1→11→21 plan.
        HashSet<string> loadedTags = new(StringComparer.OrdinalIgnoreCase) { targetTag };
        for (int hop = 0; canPatchCurrentBuild && path.Count == 0 && hop < 12; hop++)
        {
            string? previousTag = indexes[^1].Index.Strategy?.SelectedFromTags?
                .FirstOrDefault(static tag => !string.IsNullOrWhiteSpace(tag));
            if (string.IsNullOrWhiteSpace(previousTag) || !loadedTags.Add(previousTag))
                break;

            LoadedPatchIndex? previous = await TryLoadPatchIndexAsync(previousTag, cancellationToken).ConfigureAwait(false);
            if (previous is null)
                break;
            indexes.Add(previous);
            path = FindPatchPath(indexes, targetIdentity, normalizedCurrent, normalizedTarget);

            string previousTarget = NormalizeVersion(previous.Index.TargetVersion ?? previousTag);
            if (CompareVersions(previousTarget, normalizedCurrent) <= 0 && path.Count == 0)
                break;
        }

        string assetName = targetVariant.TargetAssetName!;
        string fullUrl = BuildReleaseAssetUrl(targetTag, assetName);
        long patchBytes = path.Sum(static step => step.Size);
        long? fullPackageBytes = path.Count > 0
            ? await TryGetContentLengthAsync(fullUrl, cancellationToken).ConfigureAwait(false)
            : null;
        bool patchNotWorthwhile = path.Count > 0 &&
            ((fullPackageBytes is > 0 && patchBytes >= fullPackageBytes.Value) ||
             (fullPackageBytes is null && targetVariant.TargetArchiveSize > 0 &&
              patchBytes >= targetVariant.TargetArchiveSize));
        if (patchNotWorthwhile)
        {
            PortableLog.Info(
                "Update",
                fullPackageBytes is > 0
                    ? $"补丁链大小 {patchBytes} 不小于完整包 {fullPackageBytes.Value}，改用完整包。"
                    : $"补丁链大小 {patchBytes} 不小于完整包索引大小 {targetVariant.TargetArchiveSize}，改用完整包。");
            path = [];
        }

        if (path.Count > 0)
        {
            PortableLog.Info(
                "Update",
                $"已规划 {path.Count} 段补丁：{normalizedCurrent} → {normalizedTarget}；总大小={patchBytes}。");
        }

        return new LauncherUpdatePackage(
            normalizedTarget,
            targetTag,
            fullUrl,
            assetName,
            string.IsNullOrWhiteSpace(targetVariant.TargetBinaryName)
                ? DefaultBinaryName(identity.RuntimeId)
                : targetVariant.TargetBinaryName!,
            targetVariant.TargetSha256,
            targetVariant.TargetSize > 0 ? targetVariant.TargetSize : null,
            path,
            targetIdentity.RuntimeId,
            targetIdentity.NormalizedRuntimeVariant,
            LauncherBuildIdentity.NormalizeConfiguration(targetVariant.Configuration),
            fullUrl + ".asc",
            fullUrl + ".binary.asc");
    }

    private async Task<LoadedPatchIndex?> TryLoadPatchIndexAsync(
        string tag,
        CancellationToken cancellationToken)
    {
        foreach (string asset in new[] { "patch-index.json", "index.json" })
        {
            string[] urls = [BuildReleaseAssetUrl(tag, asset), BuildGitHubReleaseAssetUrl(tag, asset)];
            foreach (string url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    continue;
                if (!response.IsSuccessStatusCode)
                {
                    PortableLog.Debug("Update", $"补丁索引不可用：{url}；HTTP={(int)response.StatusCode}。");
                    continue;
                }

                try
                {
                    await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    LauncherPatchIndexDto? index = await JsonSerializer.DeserializeAsync(
                            stream,
                            LauncherUpdateJsonContext.Default.LauncherPatchIndexDto,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (index is not null && index.FormatVersion is >= 1 and <= 3 && index.Variants is { Count: > 0 })
                        return new LoadedPatchIndex(tag, index);
                }
                catch (JsonException ex)
                {
                    PortableLog.Warn(ex, "Update", $"补丁索引格式无效：{url}。");
                }
            }
        }

        return null;
    }

    private List<LauncherUpdatePatchStep> FindPatchPath(
        IReadOnlyList<LoadedPatchIndex> indexes,
        LauncherBuildIdentity identity,
        string currentVersion,
        string targetVersion)
    {
        Dictionary<string, List<PatchEdge>> edges = new(StringComparer.OrdinalIgnoreCase);
        foreach (LoadedPatchIndex loaded in indexes)
        {
            LauncherPatchVariantDto? variant = FindVariant(loaded.Index, identity);
            if (variant?.Patches is null || string.IsNullOrWhiteSpace(loaded.Index.TargetVersion) ||
                string.IsNullOrWhiteSpace(variant.TargetSha256))
            {
                continue;
            }

            string edgeTarget = NormalizeVersion(loaded.Index.TargetVersion!);
            foreach (LauncherPatchDto patch in variant.Patches)
            {
                bool supportedAlgorithm =
                    string.Equals(patch.Algorithm, "hdiffpatch", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(patch.Algorithm, "hdiffpatch-scatter-v1", StringComparison.OrdinalIgnoreCase);
                if (!supportedAlgorithm ||
                    string.IsNullOrWhiteSpace(patch.FromVersion) ||
                    string.IsNullOrWhiteSpace(patch.FileName) ||
                    string.IsNullOrWhiteSpace(patch.Sha256) ||
                    string.IsNullOrWhiteSpace(patch.FromSha256) ||
                    patch.Size <= 0)
                {
                    continue;
                }

                string from = NormalizeVersion(patch.FromVersion!);
                string patchAsset = Path.GetFileName(patch.FileName!.Replace('\\', '/'));
                string downloadUrl = string.IsNullOrWhiteSpace(patch.DownloadUrl)
                    ? BuildReleaseAssetUrl(loaded.ReleaseTag, patchAsset)
                    : patch.DownloadUrl!;
                PatchEdge edge = new(
                    from,
                    edgeTarget,
                    new LauncherUpdatePatchStep(
                        from,
                        edgeTarget,
                        downloadUrl,
                        patch.Sha256!,
                        patch.Size,
                        patch.FromSha256!,
                        patch.FromSize,
                        variant.TargetSha256!,
                        variant.TargetSize,
                        patch.Algorithm!,
                        patch.FromManifestSha256,
                        patch.TargetManifestSha256));
                if (!edges.TryGetValue(from, out List<PatchEdge>? list))
                {
                    list = [];
                    edges.Add(from, list);
                }
                list.Add(edge);
            }
        }

        List<LauncherUpdatePatchStep> scatter = FindCheapestPatchPath(
            edges,
            currentVersion,
            targetVersion,
            scatterBundle: true);
        List<LauncherUpdatePatchStep> legacy = FindCheapestPatchPath(
            edges,
            currentVersion,
            targetVersion,
            scatterBundle: false);
        if (scatter.Count == 0)
            return legacy;
        if (legacy.Count == 0)
            return scatter;
        return scatter.Sum(static step => step.Size) <= legacy.Sum(static step => step.Size)
            ? scatter
            : legacy;
    }

    private static List<LauncherUpdatePatchStep> FindCheapestPatchPath(
        IReadOnlyDictionary<string, List<PatchEdge>> edges,
        string currentVersion,
        string targetVersion,
        bool scatterBundle)
    {
        PriorityQueue<(string Version, List<LauncherUpdatePatchStep> Steps), long> queue = new();
        Dictionary<string, long> best = new(StringComparer.OrdinalIgnoreCase)
        {
            [currentVersion] = 0
        };
        queue.Enqueue((currentVersion, []), 0);
        while (queue.TryDequeue(out (string Version, List<LauncherUpdatePatchStep> Steps) state, out long cost))
        {
            if (best.TryGetValue(state.Version, out long known) && cost > known)
                continue;
            if (string.Equals(state.Version, targetVersion, StringComparison.OrdinalIgnoreCase))
                return state.Steps;
            if (!edges.TryGetValue(state.Version, out List<PatchEdge>? next))
                continue;
            foreach (PatchEdge edge in next.Where(edge => edge.Step.IsScatterBundle == scatterBundle))
            {
                if (edge.Step.Size <= 0 || cost > long.MaxValue - edge.Step.Size)
                    continue;
                long nextCost = cost + edge.Step.Size;
                if (best.TryGetValue(edge.ToVersion, out long previous) && previous <= nextCost)
                    continue;
                best[edge.ToVersion] = nextCost;
                queue.Enqueue((edge.ToVersion, [.. state.Steps, edge.Step]), nextCost);
            }
        }

        return [];
    }

    private static LauncherPatchVariantDto? FindVariant(
        LauncherPatchIndexDto index,
        LauncherBuildIdentity identity) => index.Variants?.FirstOrDefault(variant =>
            string.Equals(variant.RuntimeId, identity.RuntimeId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                LauncherBuildIdentity.NormalizeRuntimeVariant(variant.RuntimeVariant),
                identity.NormalizedRuntimeVariant,
                StringComparison.OrdinalIgnoreCase));

    private LauncherUpdatePackage BuildFullPackage(
        string tag,
        UpdateChannel channel,
        LauncherBuildIdentity identity)
    {
        string config = channel switch
        {
            UpdateChannel.Beta => "Beta",
            UpdateChannel.CI or UpdateChannel.Dev => "CI",
            _ => "Release"
        };
        string ext = identity.RuntimeId.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? "zip" : "tar.gz";
        LauncherBuildIdentity targetIdentity = ResolvePublishedIdentity(identity);
        string variant = channel is UpdateChannel.CI or UpdateChannel.Dev
            ? "SelfContained"
            : targetIdentity.NormalizedRuntimeVariant;
        string resolvedRuntimeVariant = channel is UpdateChannel.CI or UpdateChannel.Dev
            ? "SelfContained"
            : targetIdentity.NormalizedRuntimeVariant;
        string assetName = $"PCL_N_{config}_{identity.RuntimeId}_{variant}.{ext}";
        return new LauncherUpdatePackage(
            NormalizeVersion(tag),
            tag,
            BuildReleaseAssetUrl(tag, assetName),
            assetName,
            DefaultBinaryName(identity.RuntimeId),
            null,
            null,
            [],
            identity.RuntimeId,
            resolvedRuntimeVariant,
            config,
            BuildReleaseAssetUrl(tag, assetName + ".asc"),
            BuildReleaseAssetUrl(tag, assetName + ".binary.asc"));
    }

    private static LauncherBuildIdentity ResolvePublishedIdentity(LauncherBuildIdentity identity)
    {
        string runtime = identity.NormalizedRuntimeVariant.StartsWith(
            "NoRuntime",
            StringComparison.OrdinalIgnoreCase)
            ? "NoRuntime"
            : "SelfContained";
        // New packages drop the WithPlugin/NoPlugin suffix. SelfContained /
        // NoRuntime now describes the plugin sidecar runtime payload.
        return identity with { RuntimeVariant = runtime };
    }

    private static string GetPackageStem(string assetName) =>
        assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? assetName[..^7]
            : Path.GetFileNameWithoutExtension(assetName);

    private string BuildReleaseAssetUrl(string tag, string assetName) =>
        $"{_distributionBaseUrl}/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";

    private string BuildGitHubReleaseAssetUrl(string tag, string assetName) =>
        $"https://github.com/{_owner}/{_repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";

    private static string DefaultBinaryName(string runtimeId) =>
        runtimeId.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? "PCL-N-Edition.exe" : "PCL-N-Edition";

    private sealed record LoadedPatchIndex(string ReleaseTag, LauncherPatchIndexDto Index);

    private sealed record PatchEdge(string FromVersion, string ToVersion, LauncherUpdatePatchStep Step);
}

