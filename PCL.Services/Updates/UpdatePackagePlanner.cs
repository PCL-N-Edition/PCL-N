namespace PCL.Services.Updates;

/// <summary>
/// The pure core of update package planning: choose the variant for a build identity, plan
/// the cheapest patch path across the loaded indexes, decide patch-versus-full by size, and
/// compose asset/URL/blockmap addresses. Transport (fetching indexes, HEAD requests) stays in
/// the orchestration layer, which feeds this planner.
/// </summary>
public sealed class UpdatePackagePlanner
{
    /// <summary>Last release line that dual-publishes v1 block maps; from 1.4.8 on only v2.</summary>
    public const string LastV1BlockMapVersion = "1.4.7";

    private const string DefaultAssetNamePrefix = "PCL_N_";
    private const string DefaultBinaryBaseName = "PCL-N-Edition";

    private readonly UpdatePlannerOptions _options;

    public UpdatePackagePlanner(UpdatePlannerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Plans the plain full-package update for a tag. This is the always-available fallback;
    /// scatter patch indexes must never be applied to a single-file layout, so the caller
    /// stops here for portable installations.
    /// </summary>
    public UpdatePackage PlanFull(string tag, UpdateChannel channel, UpdateBuildIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string config = channel switch
        {
            UpdateChannel.Beta => "Beta",
            UpdateChannel.CI => "CI",
            _ => "Release",
        };
        string ext = identity.RuntimeId.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? "zip" : "tar.gz";
        UpdateBuildIdentity targetIdentity = ResolvePublishedIdentity(identity);
        string variant = channel == UpdateChannel.CI
            ? "SelfContained"
            : targetIdentity.NormalizedRuntimeVariant;
        bool singleFile = identity.DistributionLayout == UpdateDistributionLayout.SingleFile &&
                          identity.RuntimeId.StartsWith("win-", StringComparison.OrdinalIgnoreCase);
        string assetName = singleFile
            ? $"{_options.AssetNamePrefix}{config}_{identity.RuntimeId}_{variant}_Portable.exe"
            : $"{_options.AssetNamePrefix}{config}_{identity.RuntimeId}_{variant}.{ext}";
        string fullUrl = BuildReleaseAssetUrl(tag, assetName);
        string signatureUrl = fullUrl + ".asc";
        bool supportsBlockMap = _options.CloudflareOnly;
        return new UpdatePackage(
            NormalizeVersion(tag),
            tag,
            fullUrl,
            assetName,
            DefaultBinaryBaseName + (identity.RuntimeId.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? ".exe" : string.Empty),
            null,
            null,
            [],
            identity.RuntimeId,
            variant,
            config,
            signatureUrl,
            singleFile ? signatureUrl : fullUrl + ".binary.asc",
            BlockMapUrl: supportsBlockMap
                ? BuildReleaseAssetUrl(tag, GetPackageStem(assetName) + ".blockmap.v2.json")
                : null,
            BlockMapSignatureUrl: supportsBlockMap
                ? BuildReleaseAssetUrl(tag, GetPackageStem(assetName) + ".blockmap.v2.json.asc")
                : null,
            BlockMapFallbackUrl: supportsBlockMap && EmitsV1BlockMap(tag)
                ? BuildReleaseAssetUrl(tag, GetPackageStem(assetName) + ".blockmap.json")
                : null,
            BlockMapFallbackSignatureUrl: supportsBlockMap && EmitsV1BlockMap(tag)
                ? BuildReleaseAssetUrl(tag, GetPackageStem(assetName) + ".blockmap.json.asc")
                : null);
    }

    /// <summary>
    /// Plans the block/patch update for one target release from the given loaded indexes. The
    /// first source is the target release; hop sources discovered by walking backwards follow
    /// in walk order. Returns null when no index or no matching variant is usable and the
    /// caller must fall back to <see cref="PlanFull"/>. <paramref name="fullPackageBytes"/> is
    /// the HEAD size of the full package when known; a patch chain that is not cheaper than
    /// the full package is dropped in favor of the full download.
    /// </summary>
    public UpdatePackage? PlanFromIndex(
        string targetTag,
        UpdateBuildIdentity identity,
        string currentVersion,
        IReadOnlyList<UpdatePatchIndexSource> indexes,
        long? fullPackageBytes = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(indexes);
        if (indexes.Count == 0)
        {
            return null;
        }

        UpdatePatchIndexSource target = indexes[0];
        UpdatePatchVariantDto? targetVariant = FindVariant(target.Index, identity);
        if (targetVariant is null || string.IsNullOrWhiteSpace(targetVariant.TargetAssetName))
        {
            return null;
        }

        string normalizedCurrent = NormalizeVersion(currentVersion);
        string normalizedTarget = NormalizeVersion(target.Index.TargetVersion ?? targetTag);
        List<UpdatePatchStep> path = BuildPatchPath(indexes, identity, normalizedCurrent, normalizedTarget);

        string assetName = targetVariant.TargetAssetName!;
        string fullUrl = BuildReleaseAssetUrl(target.ReleaseTag, assetName);
        long patchBytes = path.Sum(static step => step.Size);
        bool patchNotWorthwhile = path.Count > 0 &&
            ((fullPackageBytes is > 0 && patchBytes >= fullPackageBytes.Value) ||
             (fullPackageBytes is null && targetVariant.TargetArchiveSize > 0 &&
              patchBytes >= targetVariant.TargetArchiveSize));
        if (patchNotWorthwhile)
        {
            path = [];
        }

        return new UpdatePackage(
            normalizedTarget,
            target.ReleaseTag,
            fullUrl,
            assetName,
            string.IsNullOrWhiteSpace(targetVariant.TargetBinaryName)
                ? DefaultBinaryBaseName + (identity.RuntimeId.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? ".exe" : string.Empty)
                : targetVariant.TargetBinaryName!,
            targetVariant.TargetSha256,
            targetVariant.TargetSize > 0 ? targetVariant.TargetSize : null,
            path,
            identity.RuntimeId,
            identity.NormalizedRuntimeVariant,
            UpdateBuildIdentity.NormalizeConfiguration(targetVariant.Configuration),
            fullUrl + ".asc",
            fullUrl + ".binary.asc",
            BlockMapUrl: _options.CloudflareOnly
                ? BuildReleaseAssetUrl(target.ReleaseTag, GetPackageStem(assetName) + ".blockmap.v2.json")
                : null,
            BlockMapSignatureUrl: _options.CloudflareOnly
                ? BuildReleaseAssetUrl(target.ReleaseTag, GetPackageStem(assetName) + ".blockmap.v2.json.asc")
                : null,
            BlockMapFallbackUrl: _options.CloudflareOnly && EmitsV1BlockMap(targetTag)
                ? BuildReleaseAssetUrl(target.ReleaseTag, GetPackageStem(assetName) + ".blockmap.json")
                : null,
            BlockMapFallbackSignatureUrl: _options.CloudflareOnly && EmitsV1BlockMap(targetTag)
                ? BuildReleaseAssetUrl(target.ReleaseTag, GetPackageStem(assetName) + ".blockmap.json.asc")
                : null);
    }

    /// <summary>
    /// Finds the variant of a patch index matching a build identity: runtime id compares
    /// case-insensitively and the runtime variant is normalized on both sides.
    /// </summary>
    public static UpdatePatchVariantDto? FindVariant(UpdatePatchIndexDto index, UpdateBuildIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(identity);
        return index.Variants?.FirstOrDefault(variant =>
            string.Equals(variant.RuntimeId, identity.RuntimeId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                UpdateBuildIdentity.NormalizeRuntimeVariant(variant.RuntimeVariant),
                identity.NormalizedRuntimeVariant,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the cheapest patch path from the current version to the index target across all
    /// loaded indexes. Scatter bundles and legacy per-file patches are planned separately and
    /// the cheaper chain wins; ties go to scatter.
    /// </summary>
    public List<UpdatePatchStep> BuildPatchPath(
        IReadOnlyList<UpdatePatchIndexSource> indexes,
        UpdateBuildIdentity identity,
        string currentVersion,
        string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(identity);
        Dictionary<string, List<PatchEdge>> edges = new(StringComparer.OrdinalIgnoreCase);
        foreach (UpdatePatchIndexSource loaded in indexes)
        {
            UpdatePatchVariantDto? variant = FindVariant(loaded.Index, identity);
            if (variant?.Patches is null || string.IsNullOrWhiteSpace(loaded.Index.TargetVersion) ||
                string.IsNullOrWhiteSpace(variant.TargetSha256))
            {
                continue;
            }

            string edgeTarget = NormalizeVersion(loaded.Index.TargetVersion!);
            foreach (UpdatePatchDto patch in variant.Patches)
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
                    ? BuildAssetUrl(_options.DistributionBaseUrl, loaded.ReleaseTag, patchAsset)
                    : patch.DownloadUrl!;
                PatchEdge edge = new(
                    from,
                    edgeTarget,
                    new UpdatePatchStep(
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

        List<UpdatePatchStep> scatter = FindCheapestPatchPath(edges, currentVersion, targetVersion, scatterBundle: true);
        List<UpdatePatchStep> legacy = FindCheapestPatchPath(edges, currentVersion, targetVersion, scatterBundle: false);
        if (scatter.Count == 0)
        {
            return legacy;
        }

        if (legacy.Count == 0)
        {
            return scatter;
        }

        return scatter.Sum(static step => step.Size) <= legacy.Sum(static step => step.Size)
            ? scatter
            : legacy;
    }

    /// <summary>
    /// Whether this target tag still ships a dual-publish v1 map: v1 maps stop at 1.4.7, and
    /// CI/latest tags never had them. Unknown tag shapes keep the fallback URL so older
    /// dual-publish tags still work.
    /// </summary>
    public static bool EmitsV1BlockMap(string tagOrVersion)
    {
        string normalized = NormalizeVersion(tagOrVersion);
        if (normalized.StartsWith("ci", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (UpdateVersion.TryParse(normalized, out UpdateVersion version))
        {
            return version.CompareTo(new UpdateVersion(1, 4, 7, UpdateVersionStage.Stable, 0)) <= 0;
        }

        if (!Version.TryParse(GetVersionCore(normalized), out Version? current))
        {
            return true;
        }

        return current.CompareTo(new Version(LastV1BlockMapVersion)) <= 0;
    }

    /// <summary>
    /// Whether the running version predates the Cloudflare block-update baseline (1.4.3).
    /// Such installations may only take full packages.
    /// </summary>
    public static bool IsBeforeBlockUpdaterBaseline(string version)
    {
        if (UpdateVersion.TryParse(version, out UpdateVersion parsed))
        {
            return parsed.CompareTo(new UpdateVersion(1, 4, 3, UpdateVersionStage.Stable, 0)) < 0;
        }

        return !Version.TryParse(GetVersionCore(NormalizeVersion(version)), out Version? current) ||
               current < new Version(1, 4, 3);
    }

    /// <summary>
    /// Normalizes display versions and tags to a comparable form: `v1.1.8-release`,
    /// `1.1.8 release`, `1.1.8` all become stable-core `1.1.8` (plus a lowercase prerelease).
    /// </summary>
    public static string NormalizeVersion(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        int plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        trimmed = trimmed.Replace('_', '-');
        int space = trimmed.IndexOf(' ');
        if (space > 0)
        {
            trimmed = trimmed[..space] + "-" + trimmed[(space + 1)..].Replace(' ', '-');
        }

        return trimmed;
    }

    public static string GetVersionCore(string normalized)
    {
        int dash = normalized.IndexOf('-');
        return dash > 0 ? normalized[..dash] : normalized;
    }

    private static string BuildAssetUrl(string baseUrl, string tag, string assetName) =>
        $"{baseUrl}/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";

    private string BuildReleaseAssetUrl(string tag, string assetName) =>
        BuildAssetUrl(_options.DistributionBaseUrl, tag, assetName);

    private static List<UpdatePatchStep> FindCheapestPatchPath(
        IReadOnlyDictionary<string, List<PatchEdge>> edges,
        string currentVersion,
        string targetVersion,
        bool scatterBundle)
    {
        PriorityQueue<(string Version, List<UpdatePatchStep> Steps), long> queue = new();
        Dictionary<string, long> best = new(StringComparer.OrdinalIgnoreCase)
        {
            [currentVersion] = 0,
        };
        queue.Enqueue((currentVersion, []), 0);
        while (queue.TryDequeue(out (string Version, List<UpdatePatchStep> Steps) state, out long cost))
        {
            if (best.TryGetValue(state.Version, out long known) && cost > known)
            {
                continue;
            }

            if (string.Equals(state.Version, targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                return state.Steps;
            }

            if (!edges.TryGetValue(state.Version, out List<PatchEdge>? next))
            {
                continue;
            }

            foreach (PatchEdge edge in next.Where(edge => edge.Step.IsScatterBundle == scatterBundle))
            {
                if (edge.Step.Size <= 0 || cost > long.MaxValue - edge.Step.Size)
                {
                    continue;
                }

                long nextCost = cost + edge.Step.Size;
                if (best.TryGetValue(edge.ToVersion, out long previous) && previous <= nextCost)
                {
                    continue;
                }

                best[edge.ToVersion] = nextCost;
                queue.Enqueue((edge.ToVersion, [.. state.Steps, edge.Step]), nextCost);
            }
        }

        return [];
    }

    private static UpdateBuildIdentity ResolvePublishedIdentity(UpdateBuildIdentity identity)
    {
        string runtime = identity.NormalizedRuntimeVariant.StartsWith("NoRuntime", StringComparison.OrdinalIgnoreCase)
            ? "NoRuntime"
            : "SelfContained";
        return identity with { RuntimeVariant = runtime };
    }

    private static string GetPackageStem(string assetName) =>
        assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? assetName[..^7]
            : Path.GetFileNameWithoutExtension(assetName);

    private sealed record PatchEdge(string FromVersion, string ToVersion, UpdatePatchStep Step);
}

/// <summary>
/// Deployment-side inputs of the planner: where release assets live and whether that
/// distribution endpoint is the signed Cloudflare origin (GitHub fallbacks then never apply).
/// </summary>
public sealed record UpdatePlannerOptions(
    string DistributionBaseUrl,
    bool CloudflareOnly,
    string AssetNamePrefix = "PCL_N_");
