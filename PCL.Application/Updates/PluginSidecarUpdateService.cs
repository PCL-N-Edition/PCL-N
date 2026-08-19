// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using PCL.Application.Online;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

/// <summary>
/// Checks the Cloudflare <c>plugin</c> channel for independent PCL.Plugin Sidecar CAS updates.
/// Shares mTLS transport conventions with <see cref="LauncherUpdateService"/> but stays a sibling.
/// </summary>
public sealed class PluginSidecarUpdateService : IDisposable
{
    public const string DefaultChannel = "plugin";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _releasesBaseUrl;
    private readonly string _channelBaseUrl;
    private bool _disposed;

    public PluginSidecarUpdateService(HttpClient? httpClient = null, string? distributionBaseUrl = null)
    {
        if (httpClient is null)
        {
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

        string configured = distributionBaseUrl
            ?? Environment.GetEnvironmentVariable("PCLN_UPDATE_DISTRIBUTION_BASE_URL")
            ?? "https://api.pcln.top/v1/updates/releases";
        _releasesBaseUrl = configured.TrimEnd('/');
        _channelBaseUrl = _releasesBaseUrl.EndsWith("/releases", StringComparison.OrdinalIgnoreCase)
            ? _releasesBaseUrl[..^"/releases".Length] + "/channels"
            : _releasesBaseUrl + "/channels";

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCL-N", "1.0"));
        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<PluginSidecarUpdateCheckResult> CheckAsync(
        PluginSidecarInstallIdentity identity,
        string channel = DefaultChannel,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.CurrentVersion);

        string normalizedChannel = string.IsNullOrWhiteSpace(channel)
            ? DefaultChannel
            : channel.Trim().ToLowerInvariant();
        if (normalizedChannel is not ("plugin" or "plugin-beta"))
            return PluginSidecarUpdateCheckResult.Failed("不支持的 Sidecar 更新通道：" + normalizedChannel);

        string url =
            $"{_channelBaseUrl}/{normalizedChannel}?check={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        LauncherChannelReleaseDto marker;
        try
        {
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return PluginSidecarUpdateCheckResult.Failed("Cloudflare 尚未发布 Sidecar 更新通道。");
            if (!response.IsSuccessStatusCode)
                return PluginSidecarUpdateCheckResult.Failed(
                    $"Cloudflare Sidecar 更新通道不可用 ({(int)response.StatusCode})。");

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            marker = await JsonSerializer.DeserializeAsync(
                    stream,
                    LauncherUpdateJsonContext.Default.LauncherChannelReleaseDto,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Sidecar 更新通道返回了空清单。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PortableLog.Warn(ex, "PluginSidecarUpdate", "读取 Sidecar 更新通道失败。");
            return PluginSidecarUpdateCheckResult.Failed("无法读取 Sidecar 更新通道：" + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(marker.Tag) ||
            string.IsNullOrWhiteSpace(marker.Version) ||
            marker.PublishedAt is null)
        {
            return PluginSidecarUpdateCheckResult.Failed("Sidecar 更新通道清单无效。");
        }

        string remoteVersion = NormalizeVersion(marker.Version);
        string localVersion = NormalizeVersion(identity.CurrentVersion);
        string remoteCommit = NormalizeCommit(marker.CommitSha);
        string localCommit = NormalizeCommit(identity.CurrentCommitSha);
        bool sameCommit = AreKnownCommitsEqual(localCommit, remoteCommit);
        bool isNewer = CompareVersions(remoteVersion, localVersion) > 0 && !sameCommit;

        string assetStem = BuildAssetStem(identity.RuntimeId, identity.RuntimeVariant);
        string packageUrl = $"{_releasesBaseUrl}/{Uri.EscapeDataString(marker.Tag)}/{assetStem}.zip";
        // Sidecar updates are full-package only on CF (no FastCDC / block maps).
        string? notes = string.IsNullOrWhiteSpace(marker.ReleaseNotes) ? null : marker.ReleaseNotes.Trim();
        string releaseUrl = !string.IsNullOrWhiteSpace(marker.ReleaseNotesUrl) &&
                            Uri.TryCreate(marker.ReleaseNotesUrl.Trim(), UriKind.Absolute, out Uri? notesUri) &&
                            notesUri.Scheme == Uri.UriSchemeHttps
            ? notesUri.AbsoluteUri
            : $"https://github.com/PCL-N-Edition/PCL.Plugin/releases/tag/{Uri.EscapeDataString(marker.Tag)}";

        PortableLog.Info(
            "PluginSidecarUpdate",
            $"Sidecar 更新检查完成；通道={normalizedChannel}；本地={localVersion}；远端={remoteVersion}；有更新={isNewer}；交付=整包。");

        return new PluginSidecarUpdateCheckResult(
            Success: true,
            IsUpdateAvailable: isNewer,
            CurrentVersion: localVersion,
            LatestVersion: remoteVersion,
            ReleaseName: marker.Tag,
            ReleaseNotes: notes,
            ReleaseUrl: releaseUrl,
            PackageUrl: packageUrl,
            PackageSha256: null,
            PackageSize: null,
            ErrorMessage: null,
            RemoteCommitSha: string.IsNullOrWhiteSpace(remoteCommit) ? null : remoteCommit,
            PublishedAt: marker.PublishedAt,
            Channel: normalizedChannel);
    }

    public async Task DownloadPackageAsync(
        string packageUrl,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using HttpResponseMessage response = await GetFollowingRedirectsAsync(packageUrl, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"下载 Sidecar 更新包失败：{(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        long? total = response.Content.Headers.ContentLength;
        await using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using FileStream file = new(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            int read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
                break;
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (total is > 0)
                progress?.Report(Math.Clamp(copied / (double)total.Value, 0d, 1d));
        }

        progress?.Report(1d);
    }

    public static string ResolveRuntimeId()
    {
        if (OperatingSystem.IsWindows())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return "win-x64";
    }

    public static string BuildAssetStem(string runtimeId, string runtimeVariant)
    {
        string rid = string.IsNullOrWhiteSpace(runtimeId) ? ResolveRuntimeId() : runtimeId.Trim();
        string variant = string.Equals(runtimeVariant, "NoRuntime", StringComparison.OrdinalIgnoreCase)
            ? "NoRuntime"
            : "SelfContained";
        return $"PCL_Plugin_Sidecar_{rid}_{variant}";
    }

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        string url,
        CancellationToken cancellationToken)
    {
        string current = url;
        for (int redirect = 0; redirect < 6; redirect++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                return response;

            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(new Uri(current), response.Headers.Location);
            response.Dispose();
            if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sidecar 更新重定向到了非 HTTPS 地址。");
            current = next.AbsoluteUri;
        }

        throw new InvalidOperationException("Sidecar 更新地址重定向次数过多。");
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            or HttpStatusCode.Found or HttpStatusCode.SeeOther;

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;
        string trimmed = version.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? trimmed[1..] : trimmed;
    }

    private static string NormalizeCommit(string? commit) =>
        string.IsNullOrWhiteSpace(commit) ? string.Empty : commit.Trim().ToLowerInvariant();

    private static bool AreKnownCommitsEqual(string left, string right) =>
        left.Length >= 7 && right.Length >= 7 &&
        (left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal));

    private static int CompareVersions(string left, string right)
    {
        if (!Version.TryParse(StripPrerelease(left), out Version? leftVersion))
            leftVersion = new Version(0, 0, 0);
        if (!Version.TryParse(StripPrerelease(right), out Version? rightVersion))
            rightVersion = new Version(0, 0, 0);
        int core = leftVersion.CompareTo(rightVersion);
        if (core != 0)
            return core;
        bool leftPre = left.Contains('-', StringComparison.Ordinal);
        bool rightPre = right.Contains('-', StringComparison.Ordinal);
        if (leftPre == rightPre)
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return leftPre ? -1 : 1;
    }

    private static string StripPrerelease(string version)
    {
        int dash = version.IndexOf('-');
        return dash < 0 ? version : version[..dash];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsClient)
            _httpClient.Dispose();
    }
}
