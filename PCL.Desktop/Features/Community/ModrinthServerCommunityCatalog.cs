// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Text.Json;
using PCL.Core.IO.Net;
using PCL.Core.Logging;

namespace PCL.Desktop.Features.Community;

/// <summary>
/// Adapts Modrinth Java-server projects to the reusable community resource browser.
/// Search is provided by v2; server address, live player counts and associated
/// modpack/version metadata are supplied by the v3 project payload.
/// </summary>
public sealed class ModrinthServerCommunityCatalog : ICommunityResourceCatalog, IDisposable
{
    private const int DetailConcurrency = 8;
    private const string UserAgent = "PCL-N/1 (+https://github.com/PCL-N-Edition/PCL-N)";
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public ModrinthServerCommunityCatalog()
        : this(PortableHttp.Client)
    {
    }

    internal ModrinthServerCommunityCatalog(HttpClient client, bool ownsClient = false)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.ownsClient = ownsClient;
    }

    public async Task<IReadOnlyList<CommunityResourceEntry>> SearchAsync(
        CommunityResourceCategory category,
        string query,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (category != CommunityResourceCategory.Server)
            return [];

        options ??= new CommunitySearchOptions();
        const string facets = "[[\"project_type:minecraft_java_server\"]]";
        string index = options.Sort switch
        {
            CommunityResourceSort.Downloads => "downloads",
            CommunityResourceSort.Updated => "updated",
            _ => "relevance"
        };
        string url = "https://api.modrinth.com/v2/search?limit=50&index=" + index +
                     "&query=" + Uri.EscapeDataString(query?.Trim() ?? string.Empty) +
                     "&facets=" + Uri.EscapeDataString(facets);

        using HttpRequestMessage request = CreateRequest(url);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument search = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!search.RootElement.TryGetProperty("hits", out JsonElement hits) ||
            hits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        JsonElement[] hitItems = hits.EnumerateArray()
            .Where(static hit => hit.ValueKind == JsonValueKind.Object)
            .Select(static hit => hit.Clone())
            .ToArray();
        using SemaphoreSlim gate = new(DetailConcurrency);
        CommunityResourceEntry?[] resolved = await Task.WhenAll(hitItems.Select(hit =>
            ResolveEntryAsync(hit, gate, cancellationToken))).ConfigureAwait(false);
        return resolved.Where(static entry => entry?.Server is { Address.Length: > 0 }).Select(static entry => entry!).ToArray();
    }

    public Task<CommunityResourceDownloadFile?> ResolveDownloadAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default) => Task.FromResult<CommunityResourceDownloadFile?>(null);

    public Task<IReadOnlyList<CommunityResourceVersion>> GetVersionsAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CommunityResourceVersion>>([]);

    public Task<CommunityResourceEntry?> GetProjectAsync(
        CommunityResourceSource source,
        string projectId,
        CancellationToken cancellationToken = default) => Task.FromResult<CommunityResourceEntry?>(null);

    public Task<CommunityResourceFileIdentity?> LookupFileBySha1Async(
        string sha1Hex,
        CancellationToken cancellationToken = default) => Task.FromResult<CommunityResourceFileIdentity?>(null);

    public Task<CommunityResourceVersion?> GetLatestVersionAsync(
        string projectId,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default) => Task.FromResult<CommunityResourceVersion?>(null);

    public void Dispose()
    {
        if (ownsClient)
            client.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<CommunityResourceEntry?> ResolveEntryAsync(
        JsonElement hit,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        string projectId = ReadString(hit, "project_id");
        if (string.IsNullOrWhiteSpace(projectId))
            return null;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string url = "https://api.modrinth.com/v3/project/" + Uri.EscapeDataString(projectId);
            using HttpRequestMessage request = CreateRequest(url);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument detail = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return ParseEntry(hit, detail.RootElement);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PortableLog.Warn(exception, "CommunityServer", $"读取 Modrinth 服务器详情失败：{projectId}");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static CommunityResourceEntry? ParseEntry(JsonElement hit, JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object ||
            !detail.TryGetProperty("minecraft_java_server", out JsonElement server) ||
            server.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string projectId = Coalesce(ReadString(detail, "id"), ReadString(hit, "project_id"));
        string address = ReadString(server, "address");
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(address))
            return null;

        string slug = Coalesce(ReadString(detail, "slug"), ReadString(hit, "slug"));
        string title = Coalesce(ReadString(detail, "name"), ReadString(detail, "title"), ReadString(hit, "title"), slug);
        string description = Coalesce(ReadString(detail, "summary"), ReadString(detail, "description"), ReadString(hit, "description"));
        string? iconUrl = NullIfWhiteSpace(Coalesce(ReadString(detail, "icon_url"), ReadString(hit, "icon_url")));
        IReadOnlyList<string> versions = ReadStringArray(detail, "game_versions");
        if (versions.Count == 0)
            versions = ReadStringArray(hit, "versions");

        JsonElement pingData = default;
        if (server.TryGetProperty("ping", out JsonElement ping) && ping.ValueKind == JsonValueKind.Object)
            ping.TryGetProperty("data", out pingData);

        JsonElement content = default;
        if (!server.TryGetProperty("content", out content) || content.ValueKind != JsonValueKind.Object)
            content = default;

        CommunityServerTarget target = new(
            address.Trim(),
            versions,
            ReadInt32(pingData, "players_online"),
            ReadInt32(pingData, "players_max"),
            NullIfWhiteSpace(ReadString(content, "kind")),
            NullIfWhiteSpace(ReadString(content, "project_id")),
            NullIfWhiteSpace(ReadString(content, "version_id")),
            NullIfWhiteSpace(ReadString(content, "project_name")),
            NullIfWhiteSpace(ReadString(content, "project_icon")));

        return new CommunityResourceEntry(
            projectId,
            slug,
            title,
            description,
            "minecraft_java_server",
            iconUrl,
            ReadInt64(hit, "downloads"),
            ReadDateTimeOffset(detail, "updated") ?? ReadDateTimeOffset(hit, "date_modified"))
        {
            Source = CommunityResourceSource.Modrinth,
            ProjectUrl = "https://modrinth.com/server/" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(slug) ? projectId : slug),
            Tags = ReadStringArray(hit, "categories"),
            Server = target
        };
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    private static string Coalesce(params string[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ReadInt32(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            return 0;
        return value.TryGetInt32(out int result) ? result : 0;
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            return 0;
        return value.TryGetInt64(out long result) ? result : 0;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name)
    {
        string value = ReadString(element, name);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result)
            ? result
            : null;
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
