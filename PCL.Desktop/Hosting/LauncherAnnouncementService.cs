// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PCL.Application.Online;
using PCL.Core.Utils;

namespace PCL.Desktop.Hosting;

internal sealed record LauncherAnnouncement(
    string Id,
    string SeenKey,
    string Severity,
    string Title,
    string Markdown,
    string PrimaryLabel,
    string? ActionLabel,
    Uri? ActionUri,
    bool Dismissible);

internal sealed partial class LauncherAnnouncementService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;

    public LauncherAnnouncementService(HttpClient? httpClient = null, Uri? endpoint = null)
    {
        _httpClient = httpClient ?? PclnApiHttpClientFactory.Create(
            allowAutoRedirect: false,
            timeout: TimeSpan.FromSeconds(15));
        _ownsClient = httpClient is null;
        _endpoint = endpoint ?? ResolveEndpoint();
    }

    public async Task<IReadOnlyList<LauncherAnnouncement>> FetchEligibleAsync(
        string launcherVersion,
        string channel,
        string platform,
        string culture,
        int activityMode,
        IReadOnlySet<string> seen,
        CancellationToken cancellationToken = default)
    {
        if (activityMode >= 2)
            return [];
        using HttpRequestMessage request = new(HttpMethod.Get, _endpoint);
        request.Headers.UserAgent.ParseAdd("PCL-N-Desktop/" + launcherVersion);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        LauncherAnnouncementResponse? payload = await response.Content.ReadFromJsonAsync(
            AnnouncementJsonContext.Default.LauncherAnnouncementResponse,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LauncherAnnouncementDto> source = ResolvePayload(payload, culture);
        return SelectEligible(
            source,
            launcherVersion,
            channel,
            platform,
            culture,
            activityMode,
            seen);
    }

    internal static IReadOnlyList<LauncherAnnouncementDto> ResolvePayload(
        LauncherAnnouncementResponse? payload,
        string culture)
    {
        if (payload?.Announcements is { Count: > 0 } announcements)
            return announcements;
        if (payload?.Items is not { Count: > 0 } items)
            return payload?.Announcements ?? [];

        string locale = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture;
        return items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id) &&
                                  !string.IsNullOrWhiteSpace(item.Title) &&
                                  !string.IsNullOrWhiteSpace(item.Body))
            .Select(item => new LauncherAnnouncementDto(
                item.Id,
                "info",
                0,
                null,
                null,
                [],
                [],
                new Dictionary<string, LauncherAnnouncementContent>(StringComparer.OrdinalIgnoreCase)
                {
                    [locale] = new(item.Title, item.Body)
                },
                Dismissible: true,
                UpdatedAt: item.UpdatedAt ?? item.PublishedAt ?? DateTimeOffset.UnixEpoch))
            .ToArray();
    }

    internal static IReadOnlyList<LauncherAnnouncement> SelectEligible(
        IEnumerable<LauncherAnnouncementDto> source,
        string launcherVersion,
        string channel,
        string platform,
        string culture,
        int activityMode,
        IReadOnlySet<string> seen)
    {
        if (activityMode >= 2 || !SemVer.TryParse(NormalizeVersion(launcherVersion), out SemVer? current))
            return [];
        List<LauncherAnnouncement> results = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (LauncherAnnouncementDto item in source)
        {
            if (item.StartsAt is { } startsAt && startsAt > now)
                continue;
            if (item.EndsAt is { } endsAt && endsAt <= now)
                continue;
            if (activityMode == 1 && item.Severity is not ("important" or "security"))
                continue;
            if (item.Channels.Count > 0 && !item.Channels.Contains(channel, StringComparer.OrdinalIgnoreCase))
                continue;
            if (item.Platforms.Count > 0 && !item.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase))
                continue;
            if (!VersionMatches(current!, item.MinimumVersion, item.MaximumVersionExclusive))
                continue;
            string seenKey = item.Id + "@" + item.UpdatedAt.ToUniversalTime().ToString("O");
            if (item.Dismissible && seen.Contains(seenKey))
                continue;
            LauncherAnnouncementContent? content = SelectContent(item.LocalizedContent, culture);
            if (content is null || string.IsNullOrWhiteSpace(content.Title) || string.IsNullOrWhiteSpace(content.Body))
                continue;
            Uri? actionUri = Uri.TryCreate(content.ActionUrl, UriKind.Absolute, out Uri? parsed) &&
                             parsed.Scheme is "https" or "http"
                ? parsed
                : null;
            results.Add(new LauncherAnnouncement(
                item.Id,
                seenKey,
                item.Severity,
                content.Title.Trim(),
                content.Body,
                string.IsNullOrWhiteSpace(content.PrimaryLabel)
                    ? culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "知道了" : "Got it"
                    : content.PrimaryLabel.Trim(),
                actionUri is null || string.IsNullOrWhiteSpace(content.ActionLabel) ? null : content.ActionLabel.Trim(),
                actionUri,
                item.Dismissible));
        }
        return results;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private static LauncherAnnouncementContent? SelectContent(
        IReadOnlyDictionary<string, LauncherAnnouncementContent> values,
        string culture)
    {
        if (values.TryGetValue(culture, out LauncherAnnouncementContent? exact))
            return exact;
        string language = culture.Split('-', 2)[0];
        LauncherAnnouncementContent? languageMatch = values
            .Where(pair => pair.Key.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Value)
            .FirstOrDefault();
        if (languageMatch is not null)
            return languageMatch;
        if (values.TryGetValue("zh-CN", out LauncherAnnouncementContent? chinese))
            return chinese;
        if (values.TryGetValue("en-US", out LauncherAnnouncementContent? english))
            return english;
        return values.Values.FirstOrDefault();
    }

    private static bool VersionMatches(SemVer current, string? minimum, string? maximumExclusive)
    {
        if (!string.IsNullOrWhiteSpace(minimum) &&
            (!SemVer.TryParse(NormalizeVersion(minimum), out SemVer? min) || current < min))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(maximumExclusive) &&
            (!SemVer.TryParse(NormalizeVersion(maximumExclusive), out SemVer? max) || current >= max))
        {
            return false;
        }
        return true;
    }

    private static string NormalizeVersion(string value)
    {
        string normalized = value.Trim();
        return normalized.StartsWith('v') ? normalized[1..] : normalized;
    }

    private static Uri ResolveEndpoint()
    {
        string root = Environment.GetEnvironmentVariable("PCLN_PLUGIN_API_URL")?.Trim() ??
                      "https://api.pcln.top/v1/";
        if (!root.EndsWith('/')) root += "/";
        return new Uri(new Uri(root, UriKind.Absolute), "announcements");
    }

    internal sealed record LauncherAnnouncementResponse(
        [property: JsonPropertyName("announcements")] IReadOnlyList<LauncherAnnouncementDto>? Announcements,
        [property: JsonPropertyName("items")] IReadOnlyList<CloudflareAnnouncementDto>? Items);

    internal sealed record CloudflareAnnouncementDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt);

    internal sealed record LauncherAnnouncementDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("minimum_version")] string? MinimumVersion,
        [property: JsonPropertyName("maximum_version_exclusive")] string? MaximumVersionExclusive,
        [property: JsonPropertyName("channels")] IReadOnlyList<string> Channels,
        [property: JsonPropertyName("platforms")] IReadOnlyList<string> Platforms,
        [property: JsonPropertyName("localized_content")] IReadOnlyDictionary<string, LauncherAnnouncementContent> LocalizedContent,
        [property: JsonPropertyName("dismissible")] bool Dismissible,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("starts_at")] DateTimeOffset? StartsAt = null,
        [property: JsonPropertyName("ends_at")] DateTimeOffset? EndsAt = null);

    internal sealed record LauncherAnnouncementContent(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("primaryLabel")] string? PrimaryLabel = null,
        [property: JsonPropertyName("actionLabel")] string? ActionLabel = null,
        [property: JsonPropertyName("actionUrl")] string? ActionUrl = null);

    [JsonSerializable(typeof(LauncherAnnouncementResponse))]
    private sealed partial class AnnouncementJsonContext : JsonSerializerContext;
}
