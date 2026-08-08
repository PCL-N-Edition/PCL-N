// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Application.Online;

namespace PCL.Application.Updates;

/// <summary>
/// Checks GitHub's non-rate-limited Atom surface for release discovery, while
/// package and patch distribution use the Cloudflare/R2 update gateway.
///
/// Sources:
/// <list type="bullet">
/// <item>Atom feed: https://github.com/{owner}/{repo}/releases.atom</item>
/// <item>Latest redirect: https://github.com/{owner}/{repo}/releases/latest</item>
/// <item>Download gateway: https://api.pcln.top/v1/updates/releases/{tag}/{asset}</item>
/// </list>
/// </summary>
public sealed partial class LauncherUpdateService : IDisposable
{
    public const string DefaultOwner = "MuXue1230-owo";
    public const string DefaultRepo = "PCL-N";
    /// <summary>Rolling tag rewritten on every successful non-PR CI build. No patches are generated for this tag.</summary>
    public const string CiRollingTag = "ci-latest";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _distributionBaseUrl;
    private bool _disposed;

    public LauncherUpdateService(HttpClient? httpClient = null, string? owner = null, string? repo = null)
    {
        if (httpClient is null)
        {
            // The same handler discovers GitHub releases and authenticates Cloudflare update requests.
            // Do not follow redirects automatically so we can read Location for /releases/latest.
            _httpClient = PclnApiHttpClientFactory.Create(
                allowAutoRedirect: false,
                timeout: TimeSpan.FromSeconds(30));
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }

        _owner = string.IsNullOrWhiteSpace(owner) ? DefaultOwner : owner.Trim();
        _repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
        string? configuredDistribution = Environment.GetEnvironmentVariable("PCLN_UPDATE_DISTRIBUTION_BASE_URL");
        _distributionBaseUrl = !string.IsNullOrWhiteSpace(configuredDistribution)
            ? configuredDistribution.TrimEnd('/')
            : string.Equals(_owner, DefaultOwner, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(_repo, DefaultRepo, StringComparison.OrdinalIgnoreCase)
                ? "https://api.pcln.top/v1/updates/releases"
                : $"https://github.com/{_owner}/{_repo}/releases/download";

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
        LauncherUpdatePackage fallbackPackage = BuildFullPackage(release.Tag, channel, identity);
        LauncherBuildMetadataDto? metadata = await TryLoadVersionedMetadataAsync(
                fallbackPackage,
                channel,
                release.Tag,
                cancellationToken)
            .ConfigureAwait(false);
        string remoteCommit = NormalizeCommit(metadata?.Commit ?? ExtractCommitSha(release.Notes, release.Title));
        bool isSameSourceCommit = AreKnownCommitsEqual(currentCommitSha, remoteCommit);
        bool isVersionNewer = CompareVersions(remoteVersion, localVersion) > 0;
        bool isNewer = isVersionNewer && !isSameSourceCommit;
        LauncherUpdatePackage package = isNewer
            ? await ResolveUpdatePackageAsync(release.Tag, channel, identity, cancellationToken).ConfigureAwait(false)
            : fallbackPackage;
        if (isVersionNewer && isSameSourceCommit)
        {
            PortableLog.Info(
                "Update",
                $"目标 {channel} {remoteVersion} 与当前构建来自同一提交 {remoteCommit}，忽略跨通道版本升级。");
        }
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
            RemoteCommitSha: remoteCommit.Length > 0 ? remoteCommit : null,
            PublishedAt: release.Updated,
            Package: package);
        PortableLog.Info("Update", $"更新检查完成；通道={channel}；最新版本={remoteVersion}；有更新={isNewer}。");
        return result;
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
