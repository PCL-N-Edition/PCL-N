// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Application.Accounts;

public sealed record MinecraftOwnedCape(
    string Id,
    string Name,
    string TextureAddress,
    bool IsActive);

public interface IMinecraftCapeService
{
    Task<IReadOnlyList<MinecraftOwnedCape>> GetOwnedCapesAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task SetActiveCapeAsync(
        string accessToken,
        string capeId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Manages capes owned by a Microsoft Minecraft account. The service deliberately
/// verifies the requested cape against the authenticated profile before activating
/// it, so callers cannot submit an arbitrary texture or a cape copied from another
/// account.
/// </summary>
public sealed class MinecraftCapeService : IMinecraftCapeService
{
    private static readonly Uri ProfileEndpoint =
        new("https://api.minecraftservices.com/minecraft/profile");
    private static readonly Uri ActiveCapeEndpoint =
        new("https://api.minecraftservices.com/minecraft/profile/capes/active");

    private readonly HttpClient _client;

    public MinecraftCapeService()
        : this(CreateClient())
    {
    }

    public MinecraftCapeService(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<MinecraftOwnedCape>> GetOwnedCapesAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        EnsureAccessToken(accessToken);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, ProfileEndpoint, accessToken);
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, body, "读取正版账户披风失败");
        using JsonDocument document = JsonDocument.Parse(body);
        return ParseOwnedCapes(document.RootElement);
    }

    public async Task SetActiveCapeAsync(
        string accessToken,
        string capeId,
        CancellationToken cancellationToken = default)
    {
        EnsureAccessToken(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(capeId);
        string normalizedId = capeId.Trim();
        IReadOnlyList<MinecraftOwnedCape> owned =
            await GetOwnedCapesAsync(accessToken, cancellationToken).ConfigureAwait(false);
        if (!owned.Any(cape =>
                string.Equals(cape.Id, normalizedId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "所选披风不属于当前正版账户，Minecraft 不允许应用未获得的披风。");
        }

        // Build JSON via JsonObject so AOT/trimming analysis does not need reflection
        // over an anonymous type (IL2026 / IL3050).
        JsonObject payload = new() { ["capeId"] = normalizedId };
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Put,
            ActiveCapeEndpoint,
            accessToken);
        request.Content = content;
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        string responseBody = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, responseBody, "更换正版披风失败");
    }

    internal static IReadOnlyList<MinecraftOwnedCape> ParseOwnedCapes(JsonElement profile)
    {
        if (!profile.TryGetProperty("capes", out JsonElement capes) ||
            capes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<MinecraftOwnedCape> result = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement cape in capes.EnumerateArray())
        {
            if (cape.ValueKind != JsonValueKind.Object)
                continue;
            string id = ReadString(cape, "id");
            string address = ReadString(cape, "url");
            if (string.IsNullOrWhiteSpace(id) ||
                !ids.Add(id) ||
                !Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            string alias = ReadString(cape, "alias");
            result.Add(new MinecraftOwnedCape(
                id,
                string.IsNullOrWhiteSpace(alias) ? id : alias,
                uri.AbsoluteUri,
                string.Equals(
                    ReadString(cape, "state"),
                    "ACTIVE",
                    StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri endpoint,
        string accessToken)
    {
        HttpRequestMessage request = new(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void EnsureAccessToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("当前正版档案缺少访问令牌，请重新登录。");
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string responseBody,
        string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        string detail = string.IsNullOrWhiteSpace(responseBody)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
            : responseBody.Trim();
        throw new HttpRequestException($"{operation}：{detail}", null, response.StatusCode);
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/1.0");
        return client;
    }
}
