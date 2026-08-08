// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

public sealed partial class LauncherUpdateService
{
    private static readonly Regex CommitLineRegex = new(
        @"^\s*commit\s*[:=]\s*([0-9a-f]{7,40})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex TagFromUrlRegex = new(
        @"/releases/tag/(?<tag>[^/?#\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private async Task<LauncherUpdateCheckResult> CheckCiAsync(
        LauncherBuildIdentity identity,
        string? currentCommitSha,
        CancellationToken cancellationToken)
    {
        LauncherUpdatePackage package = BuildFullPackage(CiRollingTag, UpdateChannel.CI, identity);
        LauncherBuildMetadataDto? metadata = await TryLoadCiMetadataAsync(package, cancellationToken)
            .ConfigureAwait(false);
        if (metadata is not null)
        {
            string packageSha256 = NormalizeSha256(metadata.PackageSha256);
            package = package with
            {
                FullPackageSha256 = IsValidSha256(packageSha256) ? packageSha256 : null,
                FullPackageSize = metadata.PackageSize is > 0 ? metadata.PackageSize : null
            };
        }

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
        if (IsValidCommit(remoteCommitNorm) && IsValidCommit(localCommit))
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

    private async Task<LauncherBuildMetadataDto?> TryLoadVersionedMetadataAsync(
        LauncherUpdatePackage package,
        UpdateChannel channel,
        string releaseTag,
        CancellationToken cancellationToken)
    {
        string expectedChannel = channel == UpdateChannel.Release ? "Release" : "Beta";
        return await TryLoadBuildMetadataAsync(
                package,
                expectedChannel,
                releaseTag,
                ".build.json",
                requireVersionedFormat: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LauncherBuildMetadataDto?> TryLoadCiMetadataAsync(
        LauncherUpdatePackage package,
        CancellationToken cancellationToken)
        => await TryLoadBuildMetadataAsync(
                package,
                "CI",
                CiRollingTag,
                ".ci.json",
                requireVersionedFormat: false,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<LauncherBuildMetadataDto?> TryLoadBuildMetadataAsync(
        LauncherUpdatePackage package,
        string expectedChannel,
        string expectedTag,
        string metadataSuffix,
        bool requireVersionedFormat,
        CancellationToken cancellationToken)
    {
        string artifact = GetPackageStem(package.TargetAssetName);
        string metadataAsset = artifact + metadataSuffix;
        string url = BuildReleaseAssetUrl(expectedTag, metadataAsset) +
                     $"?check={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        try
        {
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                PortableLog.Debug("Update", $"构建元数据不可用：{metadataAsset}；HTTP={(int)response.StatusCode}。");
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            LauncherBuildMetadataDto? metadata = await JsonSerializer.DeserializeAsync(
                    stream,
                    LauncherUpdateJsonContext.Default.LauncherBuildMetadataDto,
                    cancellationToken)
                .ConfigureAwait(false);
            string commit = NormalizeCommit(metadata?.Commit);
            if (metadata is null ||
                (requireVersionedFormat && metadata.FormatVersion != 1) ||
                !string.Equals(metadata.Channel, expectedChannel, StringComparison.OrdinalIgnoreCase) ||
                (requireVersionedFormat && !string.Equals(metadata.Tag, expectedTag, StringComparison.Ordinal)) ||
                !string.Equals(metadata.Artifact, artifact, StringComparison.Ordinal) ||
                (string.Equals(expectedChannel, "CI", StringComparison.Ordinal) && metadata.SupportsPatches) ||
                !IsValidCommit(commit))
            {
                PortableLog.Warn("Update", $"构建元数据无效或与目标发布不匹配：{metadataAsset}。");
                return null;
            }

            metadata.Commit = commit;
            return metadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PortableLog.Warn(ex, "Update", $"读取构建元数据失败：{metadataAsset}。");
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

    private static string NormalizeSha256(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? string.Empty : sha.Trim().ToLowerInvariant();

    private static bool IsValidSha256(string sha) =>
        sha.Length == 64 && sha.All(Uri.IsHexDigit);

    private static bool IsValidCommit(string commit) =>
        commit.Length is >= 7 and <= 40 && commit.All(Uri.IsHexDigit);

    private static bool AreKnownCommitsEqual(string? currentCommit, string? remoteCommit)
    {
        string current = NormalizeCommit(currentCommit);
        string remote = NormalizeCommit(remoteCommit);
        return IsValidCommit(current) && IsValidCommit(remote) && CommitsMatch(current, remote);
    }

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

}

