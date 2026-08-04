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
    private const string YggdrasilApiRoot = "https://littleskin.cn/api/yggdrasil/";
    /// <summary>
    /// OAuth scopes for login + closet. <c>PlayerProfiles.Read</c> lists every role;
    /// must not be combined with <c>PlayerProfiles.Select</c> (server rejects both).
    /// </summary>
    private const string RequestedScopes =
        "openid offline_access User.Read Player.ReadWrite Closet.Read " +
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
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        // Preferred: OAuth-gated Yggdrasil list (requires Yggdrasil.PlayerProfiles.Read).
        Exception? yggdrasilFailure = null;
        try
        {
            string body = await SendBearerAsync(
                    HttpMethod.Get,
                    new Uri(YggdrasilApiRoot + "sessionserver/session/minecraft/profile"),
                    accessToken,
                    content: null,
                    "获取 LittleSkin 角色档案失败",
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<LittleSkinProfile> fromYggdrasil = ParseProfileArray(body);
            if (fromYggdrasil.Count > 0)
                return fromYggdrasil;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException)
        {
            yggdrasilFailure = ex;
            PortableLog.Warn(
                "LittleSkinAuth",
                "Yggdrasil 角色档案接口失败，回退到 /api/players + 公开 UUID 解析：" + ex.Message);
        }

        // Fallback: Passport-scoped player list + public name→UUID lookup.
        // Some OAuth tokens work for Laravel /api/* but not the Express Yggdrasil gateway.
        try
        {
            IReadOnlyList<LittleSkinPlayer> players = await GetPlayersAsync(accessToken, cancellationToken)
                .ConfigureAwait(false);
            List<LittleSkinProfile> resolved = [];
            foreach (LittleSkinPlayer player in players)
            {
                if (string.IsNullOrWhiteSpace(player.Username))
                    continue;
                string? uuid = await TryResolveUuidByNameAsync(player.Username, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    PortableLog.Warn(
                        "LittleSkinAuth",
                        $"无法解析角色 UUID：name={player.Username}；pid={player.PlayerId}。");
                    continue;
                }

                resolved.Add(new LittleSkinProfile(player.Username, uuid));
            }

            if (resolved.Count > 0)
                return resolved;

            if (players.Count == 0)
            {
                throw new InvalidOperationException(
                    "LittleSkin 账户下没有角色。请先在 littleskin.cn 创建至少一个角色后再登录。");
            }

            throw new InvalidDataException(
                "已读取到 LittleSkin 角色列表，但无法解析任何角色的 UUID。");
        }
        catch (Exception fallbackEx) when (yggdrasilFailure is not null &&
                                          fallbackEx is not InvalidOperationException)
        {
            throw new HttpRequestException(
                "获取 LittleSkin 角色档案失败：" + yggdrasilFailure.Message +
                "；回退路径也失败：" + fallbackEx.Message,
                fallbackEx);
        }

        // Unreachable: try always returns or throws.
        throw new InvalidOperationException("获取 LittleSkin 角色档案失败。");
    }

    public async Task<LittleSkinMinecraftSession> CreateMinecraftSessionAsync(
        string accessToken,
        string uuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
        string normalizedUuid = NormalizeUuid(uuid);
        string dashedUuid = FormatUuidDashed(normalizedUuid);
        static string BuildUuidPayload(string value) =>
            "{\"uuid\":\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"}";

        // Docs use undashed UUID; retry dashed if the gateway rejects the first form.
        string body;
        try
        {
            body = await SendBearerAsync(
                    HttpMethod.Post,
                    new Uri(YggdrasilApiRoot + "authserver/oauth"),
                    accessToken,
                    new StringContent(BuildUuidPayload(normalizedUuid), Encoding.UTF8, "application/json"),
                    "获取 LittleSkin Minecraft 令牌失败",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!string.Equals(normalizedUuid, dashedUuid, StringComparison.Ordinal))
        {
            body = await SendBearerAsync(
                    HttpMethod.Post,
                    new Uri(YggdrasilApiRoot + "authserver/oauth"),
                    accessToken,
                    new StringContent(BuildUuidPayload(dashedUuid), Encoding.UTF8, "application/json"),
                    "获取 LittleSkin Minecraft 令牌失败",
                    cancellationToken)
                .ConfigureAwait(false);
        }
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
            JsonElement root = document.RootElement;
            detail = FirstNonEmpty(
                ReadString(root, "errorMessage"),
                ReadString(root, "error_description"),
                ReadString(root, "message"),
                ReadString(root, "error"));
        }
        catch (JsonException)
        {
            if (!string.IsNullOrWhiteSpace(body) && body.Length <= 240 && body[0] is not '<')
                detail = body.Trim();
        }

        string message = string.IsNullOrWhiteSpace(detail)
            ? $"{operation}（HTTP {(int)response.StatusCode}）。"
            : $"{operation}：{detail}";
        PortableLog.Error("LittleSkinAuth", message);
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static IReadOnlyList<LittleSkinProfile> ParseProfileArray(string body)
    {
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

    private async Task<string?> TryResolveUuidByNameAsync(
        string username,
        CancellationToken cancellationToken)
    {
        // Public Yggdrasil name lookup (no OAuth).
        string[] paths =
        [
            YggdrasilApiRoot + "api/users/profiles/minecraft/" + Uri.EscapeDataString(username),
            YggdrasilApiRoot + "minecraftservices/minecraft/profile/lookup/name/" +
            Uri.EscapeDataString(username)
        ];

        foreach (string path in paths)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, path);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using HttpResponseMessage response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                string body = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(body);
                string uuid = ReadString(document.RootElement, "id");
                if (!string.IsNullOrWhiteSpace(uuid))
                    return NormalizeUuid(uuid);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
            {
                // try next path
            }
        }

        return null;
    }

    private static string FormatUuidDashed(string undashed)
    {
        string id = NormalizeUuid(undashed);
        if (id.Length != 32)
            return id;
        return id.Substring(0, 8) + "-" +
               id.Substring(8, 4) + "-" +
               id.Substring(12, 4) + "-" +
               id.Substring(16, 4) + "-" +
               id.Substring(20, 12);
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
