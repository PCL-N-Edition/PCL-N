// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PCL.Core.App;

namespace PCL.Application.Updates;

/// <summary>
/// Checks GitHub for a newer PCL N desktop build without using the REST API
/// (avoids unauthenticated 60 req/hr rate limits).
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
        // Prefer HTML/Atom over GitHub REST media types (we no longer call api.github.com).
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        if (channel is UpdateChannel.CI or UpdateChannel.Dev)
            return await CheckCiAsync(currentVersion, currentCommitSha, preferPluginBuild, cancellationToken)
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
        string localVersion = NormalizeVersion(currentVersion);
        bool isNewer = CompareVersions(remoteVersion, localVersion) > 0;
        string? assetUrl = BuildPreferredAssetUrl(release.Tag, channel, preferPluginBuild);
        string htmlUrl = release.HtmlUrl
            ?? $"https://github.com/{_owner}/{_repo}/releases/tag/{Uri.EscapeDataString(release.Tag)}";

        return new LauncherUpdateCheckResult(
            Success: true,
            IsUpdateAvailable: isNewer,
            CurrentVersion: localVersion,
            LatestVersion: remoteVersion,
            ReleaseName: release.Title ?? release.Tag,
            ReleaseNotes: release.Notes,
            ReleaseUrl: htmlUrl,
            PreferredAssetUrl: assetUrl,
            ErrorMessage: null,
            Channel: channel,
            SupportsPatches: true,
            RemoteCommitSha: ExtractCommitSha(release.Notes, release.Title),
            PublishedAt: release.Updated);
    }

    private async Task<LauncherUpdateCheckResult> CheckCiAsync(
        string currentVersion,
        string? currentCommitSha,
        bool preferPluginBuild,
        CancellationToken cancellationToken)
    {
        // Prefer atom entry for ci-latest (notes/commit); fall back to known tag existence via HTML page.
        IReadOnlyList<AtomReleaseEntry> feed = await FetchReleaseFeedAsync(cancellationToken).ConfigureAwait(false);
        AtomReleaseEntry? release = feed.FirstOrDefault(static e =>
            string.Equals(e.Tag, CiRollingTag, StringComparison.OrdinalIgnoreCase));

        if (release is null)
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

        string? remoteCommit = ExtractCommitSha(release.Notes, release.Title);
        string localCommit = NormalizeCommit(currentCommitSha);
        string remoteCommitNorm = NormalizeCommit(remoteCommit);
        string? assetUrl = BuildPreferredAssetUrl(CiRollingTag, UpdateChannel.CI, preferPluginBuild);
        bool hasAsset = assetUrl is not null;

        bool isNewer;
        if (remoteCommitNorm.Length > 0 && localCommit.Length > 0)
            isNewer = !CommitsMatch(localCommit, remoteCommitNorm);
        else if (remoteCommitNorm.Length > 0 && localCommit.Length == 0)
            isNewer = hasAsset;
        else
            isNewer = hasAsset;

        string htmlUrl = release.HtmlUrl
            ?? $"https://github.com/{_owner}/{_repo}/releases/tag/{CiRollingTag}";
        string latestLabel = remoteCommitNorm.Length > 0
            ? $"ci-{remoteCommitNorm[..Math.Min(7, remoteCommitNorm.Length)]}"
            : (release.Title ?? CiRollingTag);

        return new LauncherUpdateCheckResult(
            Success: true,
            IsUpdateAvailable: isNewer && hasAsset,
            CurrentVersion: NormalizeVersion(currentVersion),
            LatestVersion: latestLabel,
            ReleaseName: release.Title ?? "CI rolling build",
            ReleaseNotes: release.Notes,
            ReleaseUrl: htmlUrl,
            PreferredAssetUrl: assetUrl,
            ErrorMessage: null,
            Channel: UpdateChannel.CI,
            SupportsPatches: false,
            RemoteCommitSha: remoteCommitNorm,
            PublishedAt: release.Updated);
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
        using HttpResponseMessage response = await GetAsyncSafe(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"无法读取发布订阅 ({(int)response.StatusCode}): {Truncate(body, 160)}");
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
                HtmlToPlainText(content),
                updated));
        }

        return list;
    }

    private string? BuildPreferredAssetUrl(string tag, UpdateChannel channel, bool preferPluginBuild)
    {
        string rid = ResolveRuntimeId();
        string config = channel switch
        {
            UpdateChannel.Beta => "Beta",
            UpdateChannel.CI or UpdateChannel.Dev => "CI",
            _ => "Release"
        };
        string plugin = preferPluginBuild ? "WithPlugin" : "NoPlugin";
        string ext = rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? "zip" : "tar.gz";

        // Prefer SelfContained; fall back to NoRuntime if naming ever differs.
        string[] variants = ["SelfContained", "NoRuntime"];
        string[] plugins = preferPluginBuild
            ? ["WithPlugin", "NoPlugin"]
            : ["NoPlugin", "WithPlugin"];

        // Return the primary candidate URL (GitHub download URLs are stable by convention).
        string primary = $"https://github.com/{_owner}/{_repo}/releases/download/{Uri.EscapeDataString(tag)}/" +
                         $"PCL_N_{config}_{rid}_{variants[0]}_{plugins[0]}.{ext}";
        return primary;
    }

    private async Task<HttpResponseMessage> GetAsyncSafe(string url, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            return await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
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

    private static string? HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        string text = WebUtility.HtmlDecode(html);
        text = text.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</li>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h2>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h3>", "\n", StringComparison.OrdinalIgnoreCase);
        text = HtmlTagRegex.Replace(text, string.Empty);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    internal sealed record AtomReleaseEntry(
        string Tag,
        string? Title,
        string? HtmlUrl,
        string? Notes,
        DateTimeOffset? Updated);
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
