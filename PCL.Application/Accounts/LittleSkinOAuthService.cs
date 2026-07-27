// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PCL.Core.IO.Net;
using PCL.Core.Logging;

namespace PCL.Application.Accounts;

public sealed record LittleSkinOAuthConfiguration(
    string ClientId,
    string ClientSecret,
    Uri RedirectUri);

public sealed record LittleSkinAuthorizationRequest(
    Uri AuthorizationUri,
    string State);

public sealed record LittleSkinOAuthTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt);

public sealed record LittleSkinProfile(
    string Username,
    string Uuid);

public sealed record LittleSkinMinecraftSession(
    string Username,
    string Uuid,
    string AccessToken,
    string ClientToken);

public sealed record LittleSkinPlayer(
    long PlayerId,
    string Username,
    long SkinTextureId,
    long CapeTextureId);

public enum LittleSkinTextureKind
{
    Skin,
    Cape
}

public sealed record LittleSkinClosetItem(
    long TextureId,
    string Name,
    string Model,
    string TextureAddress,
    LittleSkinTextureKind Kind);

public interface ILittleSkinOAuthService
{
    LittleSkinAuthorizationRequest CreateAuthorizationRequest(
        LittleSkinOAuthConfiguration configuration,
        string state);

    Task<LittleSkinOAuthTokens> ExchangeAuthorizationCodeAsync(
        LittleSkinOAuthConfiguration configuration,
        string code,
        CancellationToken cancellationToken = default);

    Task<LittleSkinOAuthTokens> RefreshOAuthTokenAsync(
        LittleSkinOAuthConfiguration configuration,
        string refreshToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LittleSkinProfile>> GetProfilesAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<LittleSkinMinecraftSession> CreateMinecraftSessionAsync(
        string accessToken,
        string uuid,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LittleSkinPlayer>> GetPlayersAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LittleSkinClosetItem>> GetClosetItemsAsync(
        string accessToken,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default);

    Task ApplyTextureAsync(
        string accessToken,
        long playerId,
        long textureId,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// LittleSkin OAuth authorization-code and Blessing Skin/Yggdrasil Connect API client.
/// OAuth access tokens are used only for LittleSkin APIs; Minecraft receives the
/// dedicated Yggdrasil token returned by <c>/api/yggdrasil/authserver/oauth</c>.
/// </summary>
public sealed class LittleSkinOAuthService : ILittleSkinOAuthService
{
    public const string YggdrasilServer = "https://littleskin.cn/api/yggdrasil";
    public const string DefaultRedirectUri =
        "http://127.0.0.1:17342/oauth/littleskin/callback";

    private const string AuthorizationEndpoint = "https://littleskin.cn/oauth/authorize";
    private const string TokenEndpoint = "https://littleskin.cn/oauth/token";
    private const string ApiRoot = "https://littleskin.cn/api/";
    private const string RequestedScopes =
        "offline_access User.Read Player.ReadWrite Closet.Read " +
        "Yggdrasil.PlayerProfiles.Read Yggdrasil.MinecraftToken.Create";
    private const int MaximumClosetPages = 50;

    private readonly HttpClient _client;

    public LittleSkinOAuthService()
        : this(PortableHttp.Client)
    {
    }

    public LittleSkinOAuthService(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public static LittleSkinOAuthConfiguration ResolveConfiguration()
    {
        string clientId = FirstNonEmpty(
            Environment.GetEnvironmentVariable("PCL_LITTLESKIN_CLIENT_ID"),
            Environment.GetEnvironmentVariable("LITTLESKIN_CLIENT_ID"));
        string clientSecret = FirstNonEmpty(
            Environment.GetEnvironmentVariable("PCL_LITTLESKIN_CLIENT_SECRET"),
            Environment.GetEnvironmentVariable("LITTLESKIN_CLIENT_SECRET"));
        string redirect = FirstNonEmpty(
            Environment.GetEnvironmentVariable("PCL_LITTLESKIN_REDIRECT_URI"),
            Environment.GetEnvironmentVariable("LITTLESKIN_REDIRECT_URI"),
            DefaultRedirectUri);

        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(clientId))
            missing.Add("PCL_LITTLESKIN_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(clientSecret))
            missing.Add("PCL_LITTLESKIN_CLIENT_SECRET");
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "缺少 LittleSkin OAuth 配置：" + string.Join("、", missing) + "。");
        }

        if (!Uri.TryCreate(redirect, UriKind.Absolute, out Uri? redirectUri))
            throw new InvalidOperationException("PCL_LITTLESKIN_REDIRECT_URI 不是有效的绝对 URL。");

        return new LittleSkinOAuthConfiguration(clientId, clientSecret, redirectUri);
    }

    public LittleSkinAuthorizationRequest CreateAuthorizationRequest(
        LittleSkinOAuthConfiguration configuration,
        string state)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        string url = AuthorizationEndpoint +
                     "?client_id=" + Uri.EscapeDataString(configuration.ClientId) +
                     "&redirect_uri=" + Uri.EscapeDataString(configuration.RedirectUri.AbsoluteUri) +
                     "&response_type=code" +
                     "&scope=" + Uri.EscapeDataString(RequestedScopes) +
                     "&state=" + Uri.EscapeDataString(state);
        return new LittleSkinAuthorizationRequest(new Uri(url), state);
    }

    public Task<LittleSkinOAuthTokens> ExchangeAuthorizationCodeAsync(
        LittleSkinOAuthConfiguration configuration,
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return RequestTokensAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = configuration.ClientId,
                ["client_secret"] = configuration.ClientSecret,
                ["redirect_uri"] = configuration.RedirectUri.AbsoluteUri,
                ["code"] = code
            },
            fallbackRefreshToken: string.Empty,
            "兑换 LittleSkin OAuth 授权码失败",
            cancellationToken);
    }

    public Task<LittleSkinOAuthTokens> RefreshOAuthTokenAsync(
        LittleSkinOAuthConfiguration configuration,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return RequestTokensAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = configuration.ClientId,
                ["client_secret"] = configuration.ClientSecret,
                ["refresh_token"] = refreshToken
            },
            refreshToken,
            "刷新 LittleSkin OAuth 令牌失败",
            cancellationToken);
    }

    public async Task<IReadOnlyList<LittleSkinProfile>> GetProfilesAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        string body = await SendBearerAsync(
                HttpMethod.Get,
                new Uri(ApiRoot + "yggdrasil/sessionserver/session/minecraft/profile"),
                accessToken,
                content: null,
                "获取 LittleSkin 角色档案失败",
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("LittleSkin 角色档案响应不是数组。");

        List<LittleSkinProfile> profiles = [];
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            string uuid = ReadString(entry, "id");
            string name = ReadString(entry, "name");
            if (!string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(name))
                profiles.Add(new LittleSkinProfile(name, NormalizeUuid(uuid)));
        }

        return profiles;
    }

    public async Task<LittleSkinMinecraftSession> CreateMinecraftSessionAsync(
        string accessToken,
        string uuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
        string normalizedUuid = NormalizeUuid(uuid);
        string payload =
            "{\"uuid\":\"" + JsonEncodedText.Encode(normalizedUuid) + "\"}";
        string body = await SendBearerAsync(
                HttpMethod.Post,
                new Uri(ApiRoot + "yggdrasil/authserver/oauth"),
                accessToken,
                new StringContent(payload, Encoding.UTF8, "application/json"),
                "获取 LittleSkin Minecraft 令牌失败",
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        JsonElement selected = root.TryGetProperty("selectedProfile", out JsonElement value)
            ? value
            : default;
        string username = ReadString(selected, "name");
        string profileUuid = NormalizeUuid(ReadString(selected, "id"));
        string minecraftToken = ReadString(root, "accessToken");
        string clientToken = ReadString(root, "clientToken");
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(profileUuid) ||
            string.IsNullOrWhiteSpace(minecraftToken))
        {
            throw new InvalidDataException("LittleSkin Minecraft 令牌响应缺少选定角色。");
        }

        PortableLog.Info("LittleSkinAuth", $"Minecraft 令牌创建完成；玩家={username}。");
        return new LittleSkinMinecraftSession(
            username,
            profileUuid,
            minecraftToken,
            clientToken);
    }

    public async Task<IReadOnlyList<LittleSkinPlayer>> GetPlayersAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        string body = await SendBearerAsync(
                HttpMethod.Get,
                new Uri(ApiRoot + "players"),
                accessToken,
                content: null,
                "获取 LittleSkin 角色列表失败",
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("LittleSkin 角色列表响应不是数组。");

        List<LittleSkinPlayer> players = [];
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (!TryReadInt64(entry, "pid", out long playerId))
                continue;
            players.Add(new LittleSkinPlayer(
                playerId,
                ReadString(entry, "name"),
                ReadInt64(entry, "tid_skin"),
                ReadInt64(entry, "tid_cape")));
        }

        return players;
    }

    public async Task<IReadOnlyList<LittleSkinClosetItem>> GetClosetItemsAsync(
        string accessToken,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default)
    {
        string category = kind == LittleSkinTextureKind.Cape ? "cape" : "skin";
        List<LittleSkinClosetItem> result = [];
        int lastPage = 1;
        for (int page = 1; page <= Math.Min(lastPage, MaximumClosetPages); page++)
        {
            string body = await SendBearerAsync(
                    HttpMethod.Get,
                    new Uri(ApiRoot + $"closet?category={category}&page={page}"),
                    accessToken,
                    content: null,
                    "获取 LittleSkin 衣柜失败",
                    cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            lastPage = Math.Max(1, (int)ReadInt64(root, "last_page", 1));
            if (!root.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (!TryReadInt64(entry, "tid", out long textureId))
                    continue;
                string hash = ReadString(entry, "hash");
                if (!IsTextureHash(hash))
                    continue;
                string name = ReadClosetItemName(entry);
                result.Add(new LittleSkinClosetItem(
                    textureId,
                    string.IsNullOrWhiteSpace(name) ? "Texture " + textureId : name,
                    ReadString(entry, "type"),
                    "https://littleskin.cn/textures/" + hash.ToLowerInvariant(),
                    kind));
            }
        }

        return result;
    }

    public async Task ApplyTextureAsync(
        string accessToken,
        long playerId,
        long textureId,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(playerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(textureId);
        string field = kind == LittleSkinTextureKind.Cape ? "cape" : "skin";
        using FormUrlEncodedContent content = new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [field] = textureId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        _ = await SendBearerAsync(
                HttpMethod.Put,
                new Uri(ApiRoot + $"players/{playerId}/textures"),
                accessToken,
                content,
                kind == LittleSkinTextureKind.Cape
                    ? "更换 LittleSkin 披风失败"
                    : "更换 LittleSkin 皮肤失败",
                cancellationToken)
            .ConfigureAwait(false);
        PortableLog.Info(
            "LittleSkinAppearance",
            $"角色材质已更新；PlayerId={playerId}；Kind={kind}；TextureId={textureId}。");
    }

    private async Task<LittleSkinOAuthTokens> RequestTokensAsync(
        Dictionary<string, string> form,
        string fallbackRefreshToken,
        string operation,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client
            .PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, operation);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string accessToken = ReadString(root, "access_token");
        string refreshToken = FirstNonEmpty(ReadString(root, "refresh_token"), fallbackRefreshToken);
        int expiresIn = (int)Math.Clamp(ReadInt64(root, "expires_in", 259200), 1, int.MaxValue);
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidDataException("LittleSkin OAuth 响应缺少访问令牌或刷新令牌。");
        return new LittleSkinOAuthTokens(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private async Task<string> SendBearerAsync(
        HttpMethod method,
        Uri uri,
        string accessToken,
        HttpContent? content,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using HttpRequestMessage request = new(method, uri)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, operation);
        return body;
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string body,
        string operation)
    {
        if (response.IsSuccessStatusCode)
            return;
        string detail = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            detail = FirstNonEmpty(
                ReadString(document.RootElement, "error_description"),
                ReadString(document.RootElement, "message"),
                ReadString(document.RootElement, "error"));
        }
        catch (JsonException)
        {
        }

        string message = string.IsNullOrWhiteSpace(detail)
            ? $"{operation}（HTTP {(int)response.StatusCode}）。"
            : $"{operation}：{detail}";
        PortableLog.Error("LittleSkinAuth", message);
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string ReadClosetItemName(JsonElement entry)
    {
        if (entry.TryGetProperty("pivot", out JsonElement pivot) &&
            pivot.ValueKind == JsonValueKind.Object)
        {
            string itemName = ReadString(pivot, "item_name");
            if (!string.IsNullOrWhiteSpace(itemName))
                return itemName;
        }

        return ReadString(entry, "name");
    }

    private static string NormalizeUuid(string uuid) =>
        new(uuid.Where(static character => character is not ('-' or ' ')).ToArray());

    private static bool IsTextureHash(string hash) =>
        hash.Length == 64 &&
        hash.All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    private static string ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement element, string propertyName, long fallback = 0) =>
        TryReadInt64(element, propertyName, out long value) ? value : fallback;

    private static bool TryReadInt64(
        JsonElement element,
        string propertyName,
        out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }
}
