using System.Text.Json;

namespace PCL.Services.Updates;

/// <summary>
/// The outcome of one update discovery. The one-way gate speaks first: only an
/// <see cref="UpdateEligibilityDecision.Allowed"/> decision carries a package, and that
/// package is either the plain full fallback or an index-planned patch/block package.
/// </summary>
/// <param name="Decision">The eligibility verdict for the candidate.</param>
/// <param name="Package">The planned package, or null when the candidate is not offered.</param>
public sealed record UpdateDiscoveryResult(UpdateEligibilityDecision Decision, UpdatePackage? Package)
{
    public bool IsAllowed => Decision == UpdateEligibilityDecision.Allowed && Package is not null;
}

/// <summary>
/// Update discovery and transport: fetches patch indexes over the distribution endpoint,
/// walks the documented multi-tag hop chain backwards, HEAD-probes the full package size, and
/// hands everything to the planner — with the one-way eligibility gate applied before any
/// network happens. The HttpClient is owned by the caller and never modified here, so tests
/// substitute a stub handler instead of a network.
/// </summary>
public sealed class UpdateDiscoveryService
{
    private const int MaxIndexHops = 12;
    private static readonly string[] PatchIndexFileNames = ["patch-index.json", "index.json"];

    private readonly HttpClient _httpClient;
    private readonly UpdateDistributionOptions _options;
    private readonly UpdatePackagePlanner _planner;

    public UpdateDiscoveryService(HttpClient httpClient, UpdateDistributionOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _planner = new UpdatePackagePlanner(new UpdatePlannerOptions(
            options.DistributionBaseUrl,
            options.CloudflareOnly,
            options.AssetNamePrefix));
    }

    /// <summary>
    /// Resolves the update for one target release. The candidate version is what discovery
    /// learned about the target (the channel feed or index); when it is unrecognized or not
    /// newer, nothing is offered and no network request is made.
    /// </summary>
    public async Task<UpdateDiscoveryResult> ResolveAsync(
        string targetTag,
        string? candidateVersion,
        UpdateChannel channel,
        UpdateBuildIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        UpdatePackage full = _planner.PlanFull(targetTag, channel, identity);

        UpdateEligibilityResult eligibility = UpdateEligibility.Evaluate(
            identity.Version,
            candidateVersion ?? full.TargetVersion);
        if (!eligibility.IsAllowed)
        {
            return new UpdateDiscoveryResult(eligibility.Decision, null);
        }

        if (identity.DistributionLayout == UpdateDistributionLayout.SingleFile)
        {
            // Portable executables have their own signed one-file block map; a scatter patch
            // index must never be applied to this layout.
            return new UpdateDiscoveryResult(UpdateEligibilityDecision.Allowed, full);
        }

        if (UpdatePackagePlanner.IsBeforeBlockUpdaterBaseline(identity.Version))
        {
            return new UpdateDiscoveryResult(
                UpdateEligibilityDecision.Allowed,
                full with
                {
                    BlockMapUrl = null,
                    BlockMapSignatureUrl = null,
                    BlockMapFallbackUrl = null,
                    BlockMapFallbackSignatureUrl = null,
                });
        }

        UpdatePatchIndexSource? target = await TryLoadPatchIndexAsync(targetTag, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return new UpdateDiscoveryResult(UpdateEligibilityDecision.Allowed, full);
        }

        UpdatePatchVariantDto? targetVariant = UpdatePackagePlanner.FindVariant(target.Index, identity);
        if (targetVariant is null || string.IsNullOrWhiteSpace(targetVariant.TargetAssetName))
        {
            return new UpdateDiscoveryResult(UpdateEligibilityDecision.Allowed, full);
        }

        string normalizedCurrent = UpdatePackagePlanner.NormalizeVersion(identity.Version);
        string normalizedTarget = UpdatePackagePlanner.NormalizeVersion(target.Index.TargetVersion ?? targetTag);
        List<UpdatePatchIndexSource> indexes = [target];
        List<UpdatePatchStep> path = _planner.BuildPatchPath(indexes, identity, normalizedCurrent, normalizedTarget);

        // Format 2 keeps only a direct window; walk backwards through each index's oldest
        // selected tag until a route is found, bounded per the documented hop budget.
        HashSet<string> loadedTags = new(StringComparer.OrdinalIgnoreCase) { targetTag };
        for (int hop = 0; path.Count == 0 && hop < MaxIndexHops; hop++)
        {
            string? previousTag = indexes[^1].Index.Strategy?.SelectedFromTags?
                .FirstOrDefault(static tag => !string.IsNullOrWhiteSpace(tag));
            if (string.IsNullOrWhiteSpace(previousTag) || !loadedTags.Add(previousTag))
            {
                break;
            }

            UpdatePatchIndexSource? previous = await TryLoadPatchIndexAsync(previousTag, cancellationToken).ConfigureAwait(false);
            if (previous is null)
            {
                break;
            }

            indexes.Add(previous);
            path = _planner.BuildPatchPath(indexes, identity, normalizedCurrent, normalizedTarget);

            string previousTarget = UpdatePackagePlanner.NormalizeVersion(previous.Index.TargetVersion ?? previousTag);
            if (!IsNewer(previousTarget, normalizedCurrent) && path.Count == 0)
            {
                break;
            }
        }

        long? fullPackageBytes = path.Count > 0
            ? await TryGetContentLengthAsync(full.FullPackageUrl, cancellationToken).ConfigureAwait(false)
            : null;
        UpdatePackage? package = _planner.PlanFromIndex(
            targetTag,
            identity,
            identity.Version,
            indexes,
            fullPackageBytes);
        return new UpdateDiscoveryResult(UpdateEligibilityDecision.Allowed, package ?? full);
    }

    /// <summary>
    /// Fetches a release's patch index. `patch-index.json` is preferred, `index.json` is the
    /// legacy alias; a 404 or unusable body moves on. The index is accepted only when its
    /// format version is 1 through 3 and at least one variant is listed.
    /// </summary>
    public async Task<UpdatePatchIndexSource?> TryLoadPatchIndexAsync(
        string tag,
        CancellationToken cancellationToken = default)
    {
        foreach (string asset in PatchIndexFileNames)
        {
            string[] urls = _options.CloudflareOnly
                ? [BuildReleaseAssetUrl(_options.DistributionBaseUrl, tag, asset)]
                :
                [
                    BuildReleaseAssetUrl(_options.DistributionBaseUrl, tag, asset),
                    BuildReleaseAssetUrl(_options.GitHubReleaseBaseUrl, tag, asset),
                ];
            foreach (string url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? body = await TryGetStringAsync(url, cancellationToken).ConfigureAwait(false);
                if (body is null)
                {
                    continue;
                }

                try
                {
                    UpdatePatchIndexDto? index = JsonSerializer.Deserialize(
                        body,
                        UpdateJsonContext.Default.UpdatePatchIndexDto);
                    if (index is not null && index.FormatVersion is >= 1 and <= 3 && index.Variants is { Count: > 0 })
                    {
                        return new UpdatePatchIndexSource(tag, index);
                    }
                }
                catch (JsonException)
                {
                    // An unreadable index behaves like a missing one: fall back to the full package.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// HEAD-probes the full package size for the patch-versus-full comparison. Failures return
    /// null; the planner then compares against the index archive size instead.
    /// </summary>
    public async Task<long?> TryGetContentLengthAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Head, url);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.Headers.ContentLength;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<string?> TryGetStringAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static bool IsNewer(string candidate, string current)
    {
        if (UpdateVersion.TryParse(candidate, out UpdateVersion candidateVersion)
            && UpdateVersion.TryParse(current, out UpdateVersion currentVersion))
        {
            return candidateVersion.CompareTo(currentVersion) > 0;
        }

        return string.Compare(candidate, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string BuildReleaseAssetUrl(string baseUrl, string tag, string assetName) =>
        $"{baseUrl}/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";
}
