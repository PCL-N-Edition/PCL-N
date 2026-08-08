// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PCL.Desktop.Hosting;

internal sealed record LauncherSponsor(string Name, bool IsActive)
{
    public string Initial { get; } = FirstTextElement(Name);

    private static string FirstTextElement(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "?";
        System.Globalization.StringInfo info = new(value.Trim());
        return info.LengthInTextElements == 0 ? "?" : info.SubstringByTextElements(0, 1);
    }
}

internal sealed record LauncherSponsorSnapshot(
    IReadOnlyList<LauncherSponsor> Sponsors,
    int TotalCount,
    DateTimeOffset GeneratedAt,
    bool IsStale);

internal sealed partial class LauncherSponsorService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;

    public LauncherSponsorService(HttpClient? httpClient = null, Uri? endpoint = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _ownsClient = httpClient is null;
        _endpoint = endpoint ?? ResolveEndpoint();
    }

    public async Task<LauncherSponsorSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, _endpoint);
        request.Headers.UserAgent.ParseAdd("PCL-N-Desktop/1.0");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        LauncherSponsorResponse? payload = await response.Content.ReadFromJsonAsync(
                SponsorJsonContext.Default.LauncherSponsorResponse,
                cancellationToken)
            .ConfigureAwait(false);
        if (payload is null)
            throw new InvalidOperationException("赞助者接口返回了空响应。");

        LauncherSponsor[] sponsors = (payload.Sponsors ?? [])
            .Where(static sponsor => !string.IsNullOrWhiteSpace(sponsor.Name))
            .Take(100)
            .Select(static sponsor => new LauncherSponsor(sponsor.Name.Trim(), sponsor.IsActive))
            .ToArray();
        return new LauncherSponsorSnapshot(
            sponsors,
            Math.Max(payload.TotalCount, sponsors.Length),
            payload.GeneratedAt,
            payload.Stale);
    }

    private static Uri ResolveEndpoint()
    {
        string root = Environment.GetEnvironmentVariable("PCLN_PLUGIN_API_URL")?.Trim() ??
                      "https://api.pcln.top/v1/";
        if (!root.EndsWith('/'))
            root += "/";
        return new Uri(new Uri(root, UriKind.Absolute), "sponsors");
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }

    internal sealed record LauncherSponsorResponse(
        [property: JsonPropertyName("sponsors")] IReadOnlyList<LauncherSponsorDto>? Sponsors,
        [property: JsonPropertyName("totalCount")] int TotalCount,
        [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
        [property: JsonPropertyName("stale")] bool Stale);

    internal sealed record LauncherSponsorDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("isActive")] bool IsActive);

    [JsonSerializable(typeof(LauncherSponsorResponse))]
    private sealed partial class SponsorJsonContext : JsonSerializerContext;
}
