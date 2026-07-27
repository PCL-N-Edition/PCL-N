// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;

namespace PCL.Desktop.Features.Launching.Appearance;

public sealed record SkinSiteDescriptor(
    string Id,
    string DisplayName,
    Uri BaseUri,
    Uri DocumentationUri,
    string SvgIcon);

public sealed record SkinSiteItem(
    long TextureId,
    string Name,
    string Uploader,
    string Model,
    int Likes,
    bool IsHighDefinition,
    string SkinAddress,
    Uri DetailsUri);

public sealed record SkinSitePage(
    string SiteName,
    string ServerVersion,
    int Page,
    bool HasPreviousPage,
    bool HasNextPage,
    IReadOnlyList<SkinSiteItem> Items);

public interface ISkinSiteCatalog
{
    SkinSiteDescriptor Descriptor { get; }

    Task<SkinSitePage> GetPageAsync(
        int page,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// LittleSkin adapter. All endpoint knowledge is kept out of the Avalonia page so
/// future skin sites can be added without branching the UI.
/// </summary>
public sealed class LittleSkinCatalog : ISkinSiteCatalog
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<int, CacheEntry> _cache = new();

    public LittleSkinCatalog(HttpClient? client = null)
    {
        _client = client ?? SharedClient;
    }

    public SkinSiteDescriptor Descriptor { get; } = new(
        "littleskin",
        "LittleSkin",
        new Uri("https://littleskin.cn/"),
        new Uri("https://manual.littlesk.in/advanced/api"),
        "lucide/shirt");

    public async Task<SkinSitePage> GetPageAsync(
        int page,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        if (_cache.TryGetValue(page, out CacheEntry? cached) &&
            DateTimeOffset.UtcNow - cached.CreatedUtc < CacheLifetime)
        {
            return cached.Page;
        }

        Task<SiteIdentity> identityTask = GetIdentityAsync(cancellationToken);
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(Descriptor.BaseUri, "skinlib/list?page=" + page));
        AddJsonHeaders(request);
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        CatalogList list = ParseCatalogList(document.RootElement, page);

        using SemaphoreSlim detailSlots = new(4, 4);
        Task<SkinSiteItem?>[] detailTasks = list.Items
            .Select(item => ResolveItemAsync(item, detailSlots, cancellationToken))
            .ToArray();
        SkinSiteItem?[] details = await Task.WhenAll(detailTasks).ConfigureAwait(false);
        SiteIdentity identity = await identityTask.ConfigureAwait(false);
        SkinSitePage result = new(
            identity.SiteName,
            identity.Version,
            list.Page,
            list.HasPrevious,
            list.HasNext,
            details.Where(static item => item is not null).Cast<SkinSiteItem>().ToArray());
        _cache[page] = new CacheEntry(DateTimeOffset.UtcNow, result);
        return result;
    }

    internal static CatalogList ParseCatalogList(JsonElement root, int requestedPage)
    {
        int page = root.TryGetProperty("current_page", out JsonElement pageElement) &&
                   pageElement.TryGetInt32(out int parsedPage)
            ? Math.Max(1, parsedPage)
            : Math.Max(1, requestedPage);
        List<CatalogListItem> items = [];
        if (root.TryGetProperty("data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("tid", out JsonElement idElement) ||
                    !idElement.TryGetInt64(out long textureId))
                {
                    continue;
                }

                items.Add(new CatalogListItem(
                    textureId,
                    ReadString(item, "name", "Texture " + textureId),
                    ReadString(item, "nickname", string.Empty),
                    ReadString(item, "type", "steve"),
                    item.TryGetProperty("likes", out JsonElement likesElement) &&
                    likesElement.TryGetInt32(out int likes)
                        ? likes
                        : 0,
                    item.TryGetProperty("hd", out JsonElement hdElement) &&
                    hdElement.ValueKind == JsonValueKind.True));
            }
        }

        return new CatalogList(
            page,
            root.TryGetProperty("prev_page_url", out JsonElement previous) &&
            previous.ValueKind == JsonValueKind.String,
            root.TryGetProperty("next_page_url", out JsonElement next) &&
            next.ValueKind == JsonValueKind.String,
            items);
    }

    internal static string? ParseTextureHash(JsonElement root)
    {
        if (!root.TryGetProperty("hash", out JsonElement hashElement))
            return null;
        string? hash = hashElement.GetString();
        return hash is { Length: 64 } &&
               hash.All(static character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            ? hash.ToLowerInvariant()
            : null;
    }

    private async Task<SkinSiteItem?> ResolveItemAsync(
        CatalogListItem item,
        SemaphoreSlim detailSlots,
        CancellationToken cancellationToken)
    {
        await detailSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                new Uri(Descriptor.BaseUri, "skinlib/info/" + item.TextureId));
            AddJsonHeaders(request);
            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            string? hash = ParseTextureHash(document.RootElement);
            if (hash is null)
                return null;

            return new SkinSiteItem(
                item.TextureId,
                item.Name,
                item.Uploader,
                item.Model,
                item.Likes,
                item.IsHighDefinition,
                new Uri(Descriptor.BaseUri, "textures/" + hash).AbsoluteUri,
                new Uri(Descriptor.BaseUri, "skinlib/show/" + item.TextureId));
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            IOException or
            JsonException)
        {
            return null;
        }
        finally
        {
            detailSlots.Release();
        }
    }

    private async Task<SiteIdentity> GetIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                new Uri(Descriptor.BaseUri, "api"));
            AddJsonHeaders(request);
            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new SiteIdentity(Descriptor.DisplayName, string.Empty);

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new SiteIdentity(
                ReadString(document.RootElement, "site_name", Descriptor.DisplayName),
                ReadString(document.RootElement, "blessing_skin", string.Empty));
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            IOException or
            JsonException)
        {
            return new SiteIdentity(Descriptor.DisplayName, string.Empty);
        }
    }

    private static string ReadString(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : fallback;

    private static void AddJsonHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/1.0");
        return client;
    }

    private sealed record CacheEntry(DateTimeOffset CreatedUtc, SkinSitePage Page);

    private sealed record SiteIdentity(string SiteName, string Version);

    internal sealed record CatalogList(
        int Page,
        bool HasPrevious,
        bool HasNext,
        IReadOnlyList<CatalogListItem> Items);

    internal sealed record CatalogListItem(
        long TextureId,
        string Name,
        string Uploader,
        string Model,
        int Likes,
        bool IsHighDefinition);
}
