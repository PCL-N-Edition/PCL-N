// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

/// <summary>
/// Checks GitHub for a newer PCL N desktop build without using the rate-limited
/// REST API. The complete release body comes from the Atom feed and is converted
/// locally from GitHub's rendered HTML to Markdown.
///
/// Sources:
/// <list type="bullet">
/// <item>Atom feed: https://github.com/{owner}/{repo}/releases.atom</item>
/// <item>Latest redirect: https://github.com/{owner}/{repo}/releases/latest</item>
/// <item>Download URLs built by convention (no assets listing API)</item>
/// </list>
/// </summary>
public sealed class LauncherUpdateService : IDisposable
{
    public const string DefaultOwner = "MuXue1230-owo";
    public const string DefaultRepo = "PCL-N";
    /// <summary>Rolling tag rewritten on every successful non-PR CI build. No patches are generated for this tag.</summary>
    public const string CiRollingTag = "ci-latest";

    private static readonly Regex CommitLineRegex = new(
        @"^\s*commit\s*[:=]\s*([0-9a-f]{7,40})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex TagFromUrlRegex = new(
        @"/releases/tag/(?<tag>[^/?#\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _owner;
    private readonly string _repo;
    private bool _disposed;

    public LauncherUpdateService(HttpClient? httpClient = null, string? owner = null, string? repo = null)
    {
        if (httpClient is null)
        {
            // Do not follow redirects automatically so we can read Location for /releases/latest.
            HttpClientHandler handler = new()
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }

        _owner = string.IsNullOrWhiteSpace(owner) ? DefaultOwner : owner.Trim();
        _repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCL-N", "1.0"));
        // All discovery requests use HTML or Atom and do not consume REST API quota.
        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
    }

    public async Task<LauncherUpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        string currentVersion,
        bool preferPluginBuild = true,
        string? currentCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        _ = preferPluginBuild; // Host-only releases; legacy WithPlugin/NoPlugin names still resolve below.
        const string variant = "SelfContained";
        LauncherBuildIdentity identity = new(
            currentVersion,
            ResolveRuntimeId(),
            variant,
            channel == UpdateChannel.Beta ? "Beta" : channel == UpdateChannel.Release ? "Release" : "CI");
        return await CheckAsync(channel, identity, currentCommitSha, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LauncherUpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        LauncherBuildIdentity identity,
        string? currentCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Version);
        PortableLog.Info("Update", $"开始检查启动器更新；目标通道={channel}；当前版本={identity.Version}。");
        PortableLog.Debug(
            "Update",
            $"更新身份：Repository={_owner}/{_repo}；RuntimeId={identity.RuntimeId}；RuntimeVariant={identity.NormalizedRuntimeVariant}；BuildConfiguration={identity.Configuration}；CurrentCommit={currentCommitSha ?? "(无)"}。");

        if (channel is UpdateChannel.CI or UpdateChannel.Dev)
            return await CheckCiAsync(identity, currentCommitSha, cancellationToken)
                .ConfigureAwait(false);

        IReadOnlyList<AtomReleaseEntry> feed = await FetchReleaseFeedAsync(cancellationToken).ConfigureAwait(false);

        AtomReleaseEntry? release = channel == UpdateChannel.Release
            ? await ResolveStableReleaseAsync(feed, cancellationToken).ConfigureAwait(false)
            : ResolveBetaRelease(feed);

        if (release is null || string.IsNullOrWhiteSpace(release.Tag))
            return LauncherUpdateCheckResult.Failed("未找到可用的发布版本。");

        if (string.Equals(release.Tag, CiRollingTag, StringComparison.OrdinalIgnoreCase))
            return LauncherUpdateCheckResult.Failed("当前通道没有可用的版本化发布。");

        string remoteVersion = NormalizeVersion(release.Tag);
        string localVersion = NormalizeVersion(identity.Version);
        bool isNewer = CompareVersions(remoteVersion, localVersion) > 0;
        LauncherUpdatePackage package = await ResolveUpdatePackageAsync(
                release.Tag,
                channel,
                identity,
                cancellationToken)
            .ConfigureAwait(false);
        string htmlUrl = release.HtmlUrl
            ?? $"https://github.com/{_owner}/{_repo}/releases/tag/{Uri.EscapeDataString(release.Tag)}";
        LauncherUpdateCheckResult result = new(
            Success: true,
            IsUpdateAvailable: isNewer,
            CurrentVersion: localVersion,
            LatestVersion: remoteVersion,
            ReleaseName: release.Title ?? release.Tag,
            ReleaseNotes: release.Notes,
            ReleaseUrl: htmlUrl,
            PreferredAssetUrl: package.FullPackageUrl,
            ErrorMessage: null,
            Channel: channel,
            SupportsPatches: package.UsesPatch,
            RemoteCommitSha: ExtractCommitSha(release.Notes, release.Title),
            PublishedAt: release.Updated,
            Package: package);
        PortableLog.Info("Update", $"更新检查完成；通道={channel}；最新版本={remoteVersion}；有更新={isNewer}。");
        return result;
    }

    private async Task<LauncherUpdateCheckResult> CheckCiAsync(
        LauncherBuildIdentity identity,
        string? currentCommitSha,
        CancellationToken cancellationToken)
    {
        LauncherUpdatePackage package = BuildFullPackage(CiRollingTag, UpdateChannel.CI, identity);
        LauncherCiMetadataDto? metadata = await TryLoadCiMetadataAsync(package, cancellationToken)
            .ConfigureAwait(false);

        // Release Atom is useful for display text, but it can lag behind a rolling release edit.
        // CI identity therefore comes from the per-artifact .ci.json uploaded in the same job.
        IReadOnlyList<AtomReleaseEntry> feed;
        try
        {
            feed = await FetchReleaseFeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PortableLog.Warn(ex, "Update", "CI 发布订阅不可用，将使用 CI 构建元数据继续检查更新。");
            feed = [];
        }
        AtomReleaseEntry? release = feed.FirstOrDefault(static e =>
            string.Equals(e.Tag, CiRollingTag, StringComparison.OrdinalIgnoreCase));

        if (release is null && metadata is null)
        {
            // Lightweight existence probe (HTML, not REST API).
            string pageUrl = $"https://github.com/{_owner}/{_repo}/releases/tag/{CiRollingTag}";
            using HttpResponseMessage probe = await GetAsyncSafe(pageUrl, cancellationToken).ConfigureAwait(false);
            if (probe.StatusCode == HttpStatusCode.NotFound)
            {
                return LauncherUpdateCheckResult.Failed(
                    "尚未发布 CI 滚动包（ci-latest）。请等待 dev 分支 CI 成功后再试。");
            }

            if (!IsSuccessOrRedirect(probe.StatusCode))
            {
                return LauncherUpdateCheckResult.Failed(
                    $"无法访问 CI 发布页 ({(int)probe.StatusCode})。");
            }

            release = new AtomReleaseEntry(CiRollingTag, "CI rolling build", pageUrl, null, null);
        }

        string? remoteCommit = metadata?.Commit ?? ExtractCommitSha(release?.Notes, release?.Title);
        string localCommit = NormalizeCommit(currentCommitSha);
        string remoteCommitNorm = NormalizeCommit(remoteCommit);
        string assetUrl = package.FullPackageUrl;
        bool hasAsset = !string.IsNullOrWhiteSpace(assetUrl);

        bool isNewer;
        if (remoteCommitNorm.Length > 0 && localCommit.Length > 0)
            isNewer = !CommitsMatch(localCommit, remoteCommitNorm);
        else if (remoteCommitNorm.Length > 0 && localCommit.Length == 0)
            isNewer = hasAsset;
        else
            isNewer = hasAsset;

        string htmlUrl = release?.HtmlUrl
            ?? $"https://github.com/{_owner}/{_repo}/releases/tag/{CiRollingTag}";
        string latestLabel = remoteCommitNorm.Length > 0
            ? $"ci-{remoteCommitNorm[..Math.Min(7, remoteCommitNorm.Length)]}"
            : (release?.Title ?? CiRollingTag);
        LauncherUpdateCheckResult result = new(
            Success: true,
            IsUpdateAvailable: isNewer && hasAsset,
            CurrentVersion: NormalizeVersion(identity.Version),
            LatestVersion: latestLabel,
            ReleaseName: release?.Title ?? "CI rolling build",
            ReleaseNotes: release?.Notes,
            ReleaseUrl: htmlUrl,
            PreferredAssetUrl: assetUrl,
            ErrorMessage: null,
            Channel: UpdateChannel.CI,
            SupportsPatches: false,
            RemoteCommitSha: remoteCommitNorm,
            PublishedAt: metadata?.BuiltAt ?? release?.Updated,
            Package: package);
        PortableLog.Info("Update", $"CI 更新检查完成；最新={latestLabel}；有更新={result.IsUpdateAvailable}；远端提交={remoteCommitNorm}。");
        return result;
    }

    private async Task<LauncherCiMetadataDto?> TryLoadCiMetadataAsync(
        LauncherUpdatePackage package,
        CancellationToken cancellationToken)
    {
        string artifact = GetPackageStem(package.TargetAssetName);
        string metadataAsset = artifact + ".ci.json";
        string url = BuildReleaseAssetUrl(CiRollingTag, metadataAsset) +
                     $"?check={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        try
        {
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                PortableLog.Debug("Update", $"CI 构建元数据不可用：{metadataAsset}；HTTP={(int)response.StatusCode}。");
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            LauncherCiMetadataDto? metadata = await JsonSerializer.DeserializeAsync(
                    stream,
                    LauncherUpdateJsonContext.Default.LauncherCiMetadataDto,
                    cancellationToken)
                .ConfigureAwait(false);
            string commit = NormalizeCommit(metadata?.Commit);
            if (metadata is null ||
                !string.Equals(metadata.Channel, "CI", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.Artifact, artifact, StringComparison.Ordinal) ||
                metadata.SupportsPatches ||
                commit.Length is < 7 or > 40 ||
                !commit.All(Uri.IsHexDigit))
            {
                PortableLog.Warn("Update", $"CI 构建元数据无效或与当前平台不匹配：{metadataAsset}。");
                return null;
            }

            metadata.Commit = commit;
            return metadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PortableLog.Warn(ex, "Update", $"读取 CI 构建元数据失败：{metadataAsset}。");
            return null;
        }
    }

    private async Task<AtomReleaseEntry?> ResolveStableReleaseAsync(
        IReadOnlyList<AtomReleaseEntry> feed,
        CancellationToken cancellationToken)
    {
        // Prefer /releases/latest redirect (GitHub only points at latest non-prerelease).
        string? latestTag = await ResolveLatestStableTagAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(latestTag))
        {
            AtomReleaseEntry? fromFeed = feed.FirstOrDefault(e =>
                string.Equals(e.Tag, latestTag, StringComparison.OrdinalIgnoreCase));
            if (fromFeed is not null)
                return fromFeed;
            return new AtomReleaseEntry(
                latestTag!,
                latestTag,
                $"https://github.com/{_owner}/{_repo}/releases/tag/{Uri.EscapeDataString(latestTag!)}",
                null,
                null);
        }

        return feed.FirstOrDefault(static e => IsStableTag(e.Tag));
    }

    private static AtomReleaseEntry? ResolveBetaRelease(IReadOnlyList<AtomReleaseEntry> feed) =>
        feed.FirstOrDefault(static e => IsBetaTag(e.Tag))
        ?? feed.FirstOrDefault(static e => !string.Equals(e.Tag, CiRollingTag, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> ResolveLatestStableTagAsync(CancellationToken cancellationToken)
    {
        string url = $"https://github.com/{_owner}/{_repo}/releases/latest";
        using HttpResponseMessage response = await GetAsyncSafe(url, cancellationToken).ConfigureAwait(false);
        if (response.Headers.Location is { } location)
        {
            Match m = TagFromUrlRegex.Match(location.OriginalString);
            if (m.Success)
                return Uri.UnescapeDataString(m.Groups["tag"].Value);
        }

        // Some environments follow redirects despite handler setting.
        if (response.RequestMessage?.RequestUri is { } finalUri)
        {
            Match m = TagFromUrlRegex.Match(finalUri.AbsoluteUri);
            if (m.Success)
                return Uri.UnescapeDataString(m.Groups["tag"].Value);
        }

        return null;
    }

    private async Task<IReadOnlyList<AtomReleaseEntry>> FetchReleaseFeedAsync(CancellationToken cancellationToken)
    {
        string url = $"https://github.com/{_owner}/{_repo}/releases.atom";
        using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string message = $"无法读取发布订阅 ({(int)response.StatusCode}): {Truncate(body, 160)}";
            PortableLog.Error("Update", message);
            throw new InvalidOperationException(message);
        }

        string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseAtomFeed(xml);
    }

    internal static IReadOnlyList<AtomReleaseEntry> ParseAtomFeed(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return [];

        XDocument doc = XDocument.Parse(xml, LoadOptions.None);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.Get("http://www.w3.org/2005/Atom");
        List<AtomReleaseEntry> list = [];
        foreach (XElement entry in doc.Root?.Elements(ns + "entry") ?? [])
        {
            string? href = entry.Element(ns + "link")?.Attribute("href")?.Value
                ?? entry.Elements(ns + "link")
                    .Select(static e => e.Attribute("href")?.Value)
                    .FirstOrDefault(static h => !string.IsNullOrWhiteSpace(h));
            string? tag = null;
            if (!string.IsNullOrWhiteSpace(href))
            {
                Match m = TagFromUrlRegex.Match(href);
                if (m.Success)
                    tag = Uri.UnescapeDataString(m.Groups["tag"].Value);
            }

            // id form: tag:github.com,2008:Repository/123/v1.1.6-release
            if (string.IsNullOrWhiteSpace(tag))
            {
                string? id = entry.Element(ns + "id")?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    int slash = id.LastIndexOf('/');
                    if (slash >= 0 && slash < id.Length - 1)
                        tag = id[(slash + 1)..];
                }
            }

            if (string.IsNullOrWhiteSpace(tag))
                continue;

            string? title = entry.Element(ns + "title")?.Value;
            string? content = entry.Element(ns + "content")?.Value
                ?? entry.Element(ns + "summary")?.Value;
            DateTimeOffset? updated = null;
            if (DateTimeOffset.TryParse(entry.Element(ns + "updated")?.Value, out DateTimeOffset ts))
                updated = ts;

            list.Add(new AtomReleaseEntry(
                tag!,
                title,
                href,
                HtmlToMarkdown(content),
                updated));
        }

        return list;
    }

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
        // Patch graph uses host-only variants (SelfContained / NoRuntime).
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
             (fullPackageBytes is null && targetVariant.TargetSize > 0 && patchBytes >= targetVariant.TargetSize * 0.9));
        if (patchNotWorthwhile)
        {
            PortableLog.Info(
                "Update",
                fullPackageBytes is > 0
                    ? $"补丁链大小 {patchBytes} 不小于完整包 {fullPackageBytes.Value}，改用完整包。"
                    : $"补丁链大小 {patchBytes} 不小于目标文件的 90%，改用完整包。");
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
            string url = BuildReleaseAssetUrl(tag, asset);
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
                if (index is not null && index.FormatVersion is 1 or 2 && index.Variants is { Count: > 0 })
                    return new LoadedPatchIndex(tag, index);
            }
            catch (JsonException ex)
            {
                PortableLog.Warn(ex, "Update", $"补丁索引格式无效：{url}。");
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
                if (!string.Equals(patch.Algorithm, "hdiffpatch", StringComparison.OrdinalIgnoreCase) ||
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
                        variant.TargetSize));
                if (!edges.TryGetValue(from, out List<PatchEdge>? list))
                {
                    list = [];
                    edges.Add(from, list);
                }
                list.Add(edge);
            }
        }

        Queue<(string Version, List<LauncherUpdatePatchStep> Steps)> queue = new();
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { currentVersion };
        queue.Enqueue((currentVersion, []));
        while (queue.Count > 0)
        {
            (string version, List<LauncherUpdatePatchStep> steps) = queue.Dequeue();
            if (string.Equals(version, targetVersion, StringComparison.OrdinalIgnoreCase))
                return steps;
            if (!edges.TryGetValue(version, out List<PatchEdge>? next))
                continue;
            foreach (PatchEdge edge in next.OrderBy(static edge => edge.Step.Size))
            {
                if (!visited.Add(edge.ToVersion))
                    continue;
                List<LauncherUpdatePatchStep> branch = [.. steps, edge.Step];
                queue.Enqueue((edge.ToVersion, branch));
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
        // New packages drop the WithPlugin/NoPlugin suffix; keep identity on SelfContained/NoRuntime.
        return identity with { RuntimeVariant = runtime };
    }

    private static string GetPackageStem(string assetName) =>
        assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? assetName[..^7]
            : Path.GetFileNameWithoutExtension(assetName);

    private string BuildReleaseAssetUrl(string tag, string assetName) =>
        $"https://github.com/{_owner}/{_repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";

    private static string DefaultBinaryName(string runtimeId) =>
        runtimeId.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? "PCL.Desktop.exe" : "PCL.Desktop";

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        string url,
        CancellationToken cancellationToken)
    {
        string current = url;
        for (int redirect = 0; redirect < 6; redirect++)
        {
            HttpResponseMessage response = await GetAsyncSafe(current, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                return response;
            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(new Uri(current), response.Headers.Location);
            if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(next.UserInfo))
            {
                response.Dispose();
                throw new InvalidOperationException("更新元数据重定向到了不安全的地址。");
            }
            response.Dispose();
            current = next.AbsoluteUri;
        }

        throw new InvalidOperationException("补丁下载地址重定向次数过多。");
    }

    private async Task<long?> TryGetContentLengthAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PortableLog.Debug("Update", $"无法读取完整包大小，将使用补丁协议阈值：{ex.Message}");
            return null;
        }
    }

    private async Task<HttpResponseMessage> GetAsyncSafe(string url, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            PortableLog.Debug("Update", $"请求更新元数据：{url}");
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            PortableLog.Debug("Update", $"更新元数据响应：{url}；HTTP={(int)response.StatusCode}。");
            return response;
        }
        catch (ObjectDisposedException ex)
        {
            PortableLog.Error(ex, "Update", "更新检查服务已关闭。");
            throw new InvalidOperationException("更新检查服务已关闭，请重新打开软件更新页后再试。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsClient)
            _httpClient.Dispose();
    }

    /// <summary>Legacy alias used by older call sites.</summary>
    public void DisposeHttp() => Dispose();

    public static string ResolveRuntimeId()
    {
        if (OperatingSystem.IsWindows())
            return Environment.Is64BitProcess ? "win-x64" : "win-x86";
        if (OperatingSystem.IsLinux())
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                is System.Runtime.InteropServices.Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64";
        if (OperatingSystem.IsMacOS())
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                is System.Runtime.InteropServices.Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
        return "win-x64";
    }

    private static bool IsSuccessOrRedirect(HttpStatusCode code) =>
        ((int)code is >= 200 and < 300) ||
        IsRedirect(code);

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            or HttpStatusCode.Found or HttpStatusCode.SeeOther;

    private static bool IsStableTag(string tag)
    {
        if (string.Equals(tag, CiRollingTag, StringComparison.OrdinalIgnoreCase))
            return false;
        string n = NormalizeVersion(tag);
        return !IsBetaTag(tag) && !n.Contains("alpha", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBetaTag(string tag)
    {
        if (string.Equals(tag, CiRollingTag, StringComparison.OrdinalIgnoreCase))
            return false;
        string n = NormalizeVersion(tag);
        return n.Contains("beta", StringComparison.OrdinalIgnoreCase)
               || n.Contains("-rc", StringComparison.OrdinalIgnoreCase)
               || n.Contains("preview", StringComparison.OrdinalIgnoreCase)
               || n.Contains("pre", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractCommitSha(string? notes, string? title)
    {
        if (!string.IsNullOrWhiteSpace(notes))
        {
            Match m = CommitLineRegex.Match(notes);
            if (m.Success)
                return m.Groups[1].Value;
            m = Regex.Match(notes, @"\b([0-9a-f]{7,40})\b", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            Match m = Regex.Match(title, @"\b([0-9a-f]{7,40})\b", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value;
        }

        return null;
    }

    private static string NormalizeCommit(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? string.Empty : sha.Trim().ToLowerInvariant();

    private static bool CommitsMatch(string a, string b)
    {
        if (a.Length >= 7 && b.Length >= 7)
        {
            return a.StartsWith(b[..Math.Min(7, b.Length)], StringComparison.Ordinal) ||
                   b.StartsWith(a[..Math.Min(7, a.Length)], StringComparison.Ordinal) ||
                   string.Equals(a, b, StringComparison.Ordinal);
        }

        return string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalize display versions and tags to a comparable form.
    /// e.g. "v1.1.8-release", "1.1.8 release", "1.1.8" → stable core "1.1.8" (+ pre if any).
    /// </summary>
    internal static string NormalizeVersion(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        int plus = trimmed.IndexOf('+');
        if (plus >= 0)
            trimmed = trimmed[..plus];

        // DisplayVersion uses space: "1.1.8 release" → "1.1.8-release"
        trimmed = trimmed.Replace('_', '-');
        int space = trimmed.IndexOf(' ');
        if (space > 0)
            trimmed = trimmed[..space] + "-" + trimmed[(space + 1)..].Replace(' ', '-');

        return trimmed;
    }

    /// <summary>Returns &gt;0 if <paramref name="left"/> is newer than <paramref name="right"/>.</summary>
    internal static int CompareVersions(string left, string right)
    {
        string ln = NormalizeVersion(left);
        string rn = NormalizeVersion(right);
        string lc = GetVersionCore(ln);
        string rc = GetVersionCore(rn);

        if (Version.TryParse(lc, out Version? lv) && Version.TryParse(rc, out Version? rv))
        {
            int core = lv.CompareTo(rv);
            if (core != 0)
                return core;

            // Same numeric core: stable (empty / release) ranks above beta/rc/ci.
            string lp = GetVersionPre(ln);
            string rp = GetVersionPre(rn);
            if (lp.Length == 0 && rp.Length == 0)
                return 0;
            if (lp.Length == 0)
                return 1;
            if (rp.Length == 0)
                return -1;
            return string.Compare(lp, rp, StringComparison.OrdinalIgnoreCase);
        }

        return string.Compare(ln, rn, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetVersionCore(string normalized)
    {
        int dash = normalized.IndexOf('-');
        return dash > 0 ? normalized[..dash] : normalized;
    }

    private static string GetVersionPre(string normalized)
    {
        int dash = normalized.IndexOf('-');
        if (dash <= 0 || dash >= normalized.Length - 1)
            return string.Empty;
        string pre = normalized[(dash + 1)..].Trim().ToLowerInvariant();
        // Treat these as the stable channel (same rank as no suffix).
        if (pre is "release" or "stable" or "final" or "ga")
            return string.Empty;
        return pre;
    }

    internal static string? HtmlToMarkdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        string text = Regex.Replace(
            html,
            @"<pre\b[^>]*>\s*(?:<code\b[^>]*>)?(?<code>.*?)(?:</code>)?\s*</pre>",
            static match => "\n```\n" + match.Groups["code"].Value.Trim('\r', '\n') + "\n```\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(
            text,
            @"<img\b[^>]*?src\s*=\s*[""'](?<url>[^""']+)[""'][^>]*?(?:alt\s*=\s*[""'](?<alt>[^""']*)[""'])?[^>]*>",
            static match => $"![{WebUtility.HtmlDecode(match.Groups["alt"].Value)}]({WebUtility.HtmlDecode(match.Groups["url"].Value)})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(
            text,
            @"<a\b[^>]*?href\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>(?<label>.*?)</a>",
            static match =>
                $"[{DecodeInlineHtml(match.Groups["label"].Value)}]({WebUtility.HtmlDecode(match.Groups["url"].Value)})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(
            text,
            @"<code\b[^>]*>(?<code>.*?)</code>",
            static match => "`" + match.Groups["code"].Value.Trim() + "`",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        for (int level = 6; level >= 1; level--)
        {
            text = Regex.Replace(text, $@"<h{level}\b[^>]*>", "\n" + new string('#', level) + " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, $@"</h{level}\s*>", "\n", RegexOptions.IgnoreCase);
        }

        text = Regex.Replace(text, @"<(?:strong|b)\b[^>]*>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(?:strong|b)\s*>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(?:em|i)\b[^>]*>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(?:em|i)\s*>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(?:del|s|strike)\b[^>]*>", "~~", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(?:del|s|strike)\s*>", "~~", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<blockquote\b[^>]*>", "\n> ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</blockquote\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li\b[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</li\s*>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<hr\b[^>]*>", "\n---\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(?:p|div|section|article|ul|ol|details)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(?:p|div|section|article|ul|ol|details)\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<summary\b[^>]*>", "\n**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</summary\s*>", "**\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<tr\b[^>]*>", "\n| ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</tr\s*>", "|\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(?:th|td)\b[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(?:th|td)\s*>", " | ", RegexOptions.IgnoreCase);
        text = HtmlTagRegex.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string DecodeInlineHtml(string html) =>
        WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, string.Empty)).Trim();

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    internal sealed record AtomReleaseEntry(
        string Tag,
        string? Title,
        string? HtmlUrl,
        string? Notes,
        DateTimeOffset? Updated);

    private sealed record LoadedPatchIndex(string ReleaseTag, LauncherPatchIndexDto Index);

    private sealed record PatchEdge(string FromVersion, string ToVersion, LauncherUpdatePatchStep Step);
}

public sealed record LauncherUpdateCheckResult(
    bool Success,
    bool IsUpdateAvailable,
    string? CurrentVersion,
    string? LatestVersion,
    string? ReleaseName,
    string? ReleaseNotes,
    string? ReleaseUrl,
    string? PreferredAssetUrl,
    string? ErrorMessage,
    UpdateChannel Channel = UpdateChannel.Release,
    bool SupportsPatches = true,
    string? RemoteCommitSha = null,
    DateTimeOffset? PublishedAt = null,
    LauncherUpdatePackage? Package = null)
{
    public static LauncherUpdateCheckResult Failed(string message) =>
        new(false, false, null, null, null, null, null, null, message);
}
