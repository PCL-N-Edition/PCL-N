// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCL.Core.App;

namespace PCL.Application.Updates;

/// <summary>
/// Checks GitHub for a newer PCL N desktop build.
/// Release/Beta: versioned GitHub Releases (patches optional via separate index).
/// CI: rolling <c>ci-latest</c> release published by the Build (CI) workflow — full packages only, never patches.
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

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _owner;
    private readonly string _repo;
    private bool _disposed;

    public LauncherUpdateService(HttpClient? httpClient = null, string? owner = null, string? repo = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<LauncherUpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        string currentVersion,
        bool preferPluginBuild = true,
        string? currentCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        if (channel is UpdateChannel.CI or UpdateChannel.Dev)
            return await CheckCiAsync(currentVersion, currentCommitSha, preferPluginBuild, cancellationToken)
                .ConfigureAwait(false);

        string api = channel == UpdateChannel.Release
            ? $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest"
            : $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=20";

        using HttpResponseMessage response = await _httpClient.GetAsync(api, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return LauncherUpdateCheckResult.Failed(
                $"GitHub API {(int)response.StatusCode}: {Truncate(body, 200)}");
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        GitHubReleaseDto? release = channel == UpdateChannel.Release
            ? JsonSerializer.Deserialize(json, LauncherUpdateJsonContext.Default.GitHubReleaseDto)
            : SelectChannelRelease(
                JsonSerializer.Deserialize(json, LauncherUpdateJsonContext.Default.ListGitHubReleaseDto),
                channel);

        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            return LauncherUpdateCheckResult.Failed("未找到可用的发布版本。");

        // Never treat the CI rolling tag as a Release/Beta channel hit.
        if (string.Equals(release.TagName, CiRollingTag, StringComparison.OrdinalIgnoreCase))
            return LauncherUpdateCheckResult.Failed("当前通道没有可用的版本化发布。");

        string remoteVersion = NormalizeVersion(release.TagName);
        string localVersion = NormalizeVersion(currentVersion);
        bool isNewer = CompareVersions(remoteVersion, localVersion) > 0;
        string? assetUrl = SelectAssetUrl(release.Assets, preferPluginBuild);
        string htmlUrl = release.HtmlUrl
            ?? $"https://github.com/{_owner}/{_repo}/releases/tag/{Uri.EscapeDataString(release.TagName)}";

        return new LauncherUpdateCheckResult(
            Success: true,
            IsUpdateAvailable: isNewer,
            CurrentVersion: localVersion,
            LatestVersion: remoteVersion,
            ReleaseName: release.Name ?? release.TagName,
            ReleaseNotes: release.Body,
            ReleaseUrl: htmlUrl,
            PreferredAssetUrl: assetUrl,
            ErrorMessage: null,
            Channel: channel,
            SupportsPatches: true,
            RemoteCommitSha: ExtractCommitSha(release),
            PublishedAt: release.PublishedAt);
    }

    private async Task<LauncherUpdateCheckResult> CheckCiAsync(
        string currentVersion,
        string? currentCommitSha,
        bool preferPluginBuild,
        CancellationToken cancellationToken)
    {
        string api = $"https://api.github.com/repos/{_owner}/{_repo}/releases/tags/{CiRollingTag}";
        using HttpResponseMessage response = await _httpClient.GetAsync(api, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return LauncherUpdateCheckResult.Failed(
                "尚未发布 CI 滚动包（ci-latest）。请等待 dev 分支 CI 成功后再试。");
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return LauncherUpdateCheckResult.Failed(
                $"GitHub API {(int)response.StatusCode}: {Truncate(body, 200)}");
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        GitHubReleaseDto? release = JsonSerializer.Deserialize(json, LauncherUpdateJsonContext.Default.GitHubReleaseDto);
        if (release is null)
            return LauncherUpdateCheckResult.Failed("无法解析 CI 滚动发布。");

        string? remoteCommit = ExtractCommitSha(release);
        string localCommit = NormalizeCommit(currentCommitSha);
        string remoteCommitNorm = NormalizeCommit(remoteCommit);

        bool hasAsset = SelectCiAssetUrl(release.Assets, preferPluginBuild) is not null;
        bool isNewer;
        if (remoteCommitNorm.Length > 0 && localCommit.Length > 0)
            isNewer = !CommitsMatch(localCommit, remoteCommitNorm);
        else if (remoteCommitNorm.Length > 0 && localCommit.Length == 0)
            isNewer = hasAsset;
        else
            isNewer = hasAsset;

        string? assetUrl = SelectCiAssetUrl(release.Assets, preferPluginBuild);
        string htmlUrl = release.HtmlUrl
            ?? $"https://github.com/{_owner}/{_repo}/releases/tag/{CiRollingTag}";
        string latestLabel = remoteCommitNorm.Length > 0
            ? $"ci-{remoteCommitNorm[..Math.Min(7, remoteCommitNorm.Length)]}"
            : (release.Name ?? CiRollingTag);

        return new LauncherUpdateCheckResult(
            Success: true,
            IsUpdateAvailable: isNewer && hasAsset,
            CurrentVersion: NormalizeVersion(currentVersion),
            LatestVersion: latestLabel,
            ReleaseName: release.Name ?? "CI rolling build",
            ReleaseNotes: release.Body,
            ReleaseUrl: htmlUrl,
            PreferredAssetUrl: assetUrl,
            ErrorMessage: null,
            Channel: UpdateChannel.CI,
            SupportsPatches: false,
            RemoteCommitSha: remoteCommitNorm,
            PublishedAt: release.PublishedAt);
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

    private static GitHubReleaseDto? SelectChannelRelease(List<GitHubReleaseDto>? releases, UpdateChannel channel)
    {
        if (releases is null || releases.Count == 0)
            return null;

        IEnumerable<GitHubReleaseDto> filtered = releases.Where(static r =>
            !r.Draft &&
            !string.Equals(r.TagName, CiRollingTag, StringComparison.OrdinalIgnoreCase));
        filtered = channel switch
        {
            UpdateChannel.Beta => filtered.Where(static r => r.Prerelease),
            UpdateChannel.CI or UpdateChannel.Dev => filtered,
            _ => filtered.Where(static r => !r.Prerelease)
        };
        return filtered.FirstOrDefault();
    }

    private static string? SelectAssetUrl(List<GitHubAssetDto>? assets, bool preferPluginBuild)
    {
        if (assets is null || assets.Count == 0)
            return null;

        string rid = ResolveRuntimeId();
        string pluginToken = preferPluginBuild ? "WithPlugin" : "NoPlugin";
        const string selfContainedToken = "SelfContained";

        GitHubAssetDto? match = assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains(selfContainedToken, StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains(pluginToken, StringComparison.OrdinalIgnoreCase) &&
            (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
             a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)));

        match ??= assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains(pluginToken, StringComparison.OrdinalIgnoreCase));

        match ??= assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase));

        return match?.BrowserDownloadUrl;
    }

    private static string? SelectCiAssetUrl(List<GitHubAssetDto>? assets, bool preferPluginBuild)
    {
        if (assets is null || assets.Count == 0)
            return null;

        string rid = ResolveRuntimeId();
        string pluginToken = preferPluginBuild ? "WithPlugin" : "NoPlugin";

        GitHubAssetDto? match = assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Contains("CI", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains(pluginToken, StringComparison.OrdinalIgnoreCase) &&
            (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
             a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)));

        match ??= assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Contains("CI", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("SelfContained", StringComparison.OrdinalIgnoreCase));

        match ??= assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase));

        return match?.BrowserDownloadUrl;
    }

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

    private static string? ExtractCommitSha(GitHubReleaseDto release)
    {
        if (!string.IsNullOrWhiteSpace(release.TargetCommitish) &&
            Regex.IsMatch(release.TargetCommitish, "^[0-9a-f]{7,40}$", RegexOptions.IgnoreCase))
            return release.TargetCommitish.Trim();

        if (!string.IsNullOrWhiteSpace(release.Body))
        {
            Match m = CommitLineRegex.Match(release.Body);
            if (m.Success)
                return m.Groups[1].Value;
        }

        if (!string.IsNullOrWhiteSpace(release.Name))
        {
            Match m = Regex.Match(release.Name, @"\b([0-9a-f]{7,40})\b", RegexOptions.IgnoreCase);
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

    private static string NormalizeVersion(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        int plus = trimmed.IndexOf('+');
        if (plus >= 0)
            trimmed = trimmed[..plus];
        return trimmed;
    }

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(StripPrerelease(left), out Version? l) &&
            Version.TryParse(StripPrerelease(right), out Version? r))
            return l.CompareTo(r);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripPrerelease(string version)
    {
        int dash = version.IndexOf('-');
        return dash > 0 ? version[..dash] : version;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
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
    DateTimeOffset? PublishedAt = null)
{
    public static LauncherUpdateCheckResult Failed(string message) =>
        new(false, false, null, null, null, null, null, null, message);
}
