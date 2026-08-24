// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PCL.Core.IO.Net;
using PCL.Core.Logging;

namespace PCL.Application.Accounts;

public sealed record LittleSkinOAuthConfiguration(
    string ClientId,
    /// <summary>Required for authorization-code exchange only; device flow uses public client id.</summary>
    string ClientSecret,
    Uri RedirectUri);

public sealed record LittleSkinAuthorizationRequest(
    Uri AuthorizationUri,
    string State);

/// <summary>Device authorization grant (RFC 8628) pair from open.littleskin.cn.</summary>
public sealed record LittleSkinDeviceCodeInfo(
    string UserCode,
    string DeviceCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

public sealed record LittleSkinOAuthTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string? IdToken = null);

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

public sealed record LittleSkinTextureUploadResult(
    string ProfileUuid,
    LittleSkinTextureKind Kind,
    bool IsSlim);

public interface ILittleSkinOAuthService
{
    LittleSkinAuthorizationRequest CreateAuthorizationRequest(
        LittleSkinOAuthConfiguration configuration,
        string state);

    Task<LittleSkinDeviceCodeInfo> RequestDeviceCodeAsync(
        LittleSkinOAuthConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<LittleSkinOAuthTokens> WaitForDeviceAuthorizationAsync(
        LittleSkinOAuthConfiguration configuration,
        LittleSkinDeviceCodeInfo deviceCode,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

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

    Task EnsureClosetTextureAsync(
        string accessToken,
        long textureId,
        string name,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default);

    Task<LittleSkinTextureUploadResult> UploadMinecraftTextureAsync(
        string minecraftAccessToken,
        string profileUuid,
        byte[] pngBytes,
        string fileName,
        bool isSlim,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// LittleSkin OAuth 2 client per
/// <see href="https://manual.littlesk.in/advanced/oauth2/">OAuth 2</see> and
/// <see href="https://manual.littlesk.in/advanced/api">LittleSkin API</see>.
/// <para>
/// Desktop launchers should use the <b>device authorization grant</b>
/// (<c>open.littleskin.cn</c>). After obtaining a Bearer access token:
/// </para>
/// <list type="number">
/// <item><c>GET …/sessionserver/session/minecraft/profile</c> (scope <c>Yggdrasil.PlayerProfiles.Read</c>)</item>
/// <item><c>POST …/authserver/oauth</c> (scope <c>Yggdrasil.MinecraftToken.Create</c>)</item>
/// </list>
/// OAuth access tokens are only for LittleSkin APIs; Minecraft receives the Yggdrasil
/// access token from step 2.
/// </summary>
public sealed class LittleSkinOAuthService : ILittleSkinOAuthService
{
    public const string YggdrasilServer = "https://littleskin.cn/api/yggdrasil";
    public const string DefaultRedirectUri =
        "http://127.0.0.1:17342/oauth/littleskin/callback";

    /// <summary>Device-flow callback URL required when applying for device-code whitelist.</summary>
    public const string DeviceFlowRedirectUri = "https://open.littleskin.cn/oauth/callback";

    /// <summary>
    /// Shown when LittleSkin returns <c>invalid_client</c> (app not on device-code whitelist).
    /// </summary>
    public const string InvalidClientUserMessage =
        "LittleSkin OAuth 设备代码流申请暂未通过（invalid_client）。请改用「第三方登录」输入 Yggdrasil 地址与账号密码。";

    private const string AuthorizationEndpoint = "https://littleskin.cn/oauth/authorize";
    private const string PassportTokenEndpoint = "https://littleskin.cn/oauth/token";
    private const string DeviceCodeEndpoint = "https://open.littleskin.cn/oauth/device_code";
    private const string OpenTokenEndpoint = "https://open.littleskin.cn/oauth/token";
    private const string ApiRoot = "https://littleskin.cn/api/";
    private const string YggdrasilApiRoot = "https://littleskin.cn/api/yggdrasil/";

    /// <summary>
    /// Scopes for the current launcher device flow. Public skin-library textures
    /// are applied directly to a player, so the closet only needs read access.
    /// Must not combine <c>PlayerProfiles.Read</c> with <c>PlayerProfiles.Select</c>.
    /// </summary>
    public const string RequestedScopes =
        "openid offline_access " +
        "User.Read Player.ReadWrite Closet.Read " +
        "Yggdrasil.PlayerProfiles.Read Yggdrasil.MinecraftToken.Create";

    /// <summary>
    /// Authorization-code flow scopes. LittleSkin only supports OpenID Connect
    /// and <c>offline_access</c> on its device flow; code exchange returns a
    /// refresh token without requesting either scope.
    /// </summary>
    public const string AuthorizationCodeScopes =
        "User.Read Player.ReadWrite Closet.Read " +
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

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "缺少 LittleSkin OAuth 配置：PCL_LITTLESKIN_CLIENT_ID。" +
                "桌面启动器使用设备代码流，仅需 Client ID（须在 LittleSkin 申请设备代码流白名单，" +
                "回调 URL 设为 https://open.littleskin.cn/oauth/callback）。");
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
                     "&scope=" + Uri.EscapeDataString(AuthorizationCodeScopes) +
                     "&state=" + Uri.EscapeDataString(state);
        return new LittleSkinAuthorizationRequest(new Uri(url), state);
    }

    public async Task<LittleSkinDeviceCodeInfo> RequestDeviceCodeAsync(
        LittleSkinOAuthConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using FormUrlEncodedContent form = new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = configuration.ClientId,
                ["scope"] = RequestedScopes
            });
        using HttpRequestMessage request = new(HttpMethod.Post, DeviceCodeEndpoint)
        {
            Content = form
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? requestId = TryGetRequestId(response);
        ThrowIfInvalidClient(body, requestId);
        EnsureSuccess(response, body, "申请 LittleSkin 设备代码失败", requestId);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string userCode = ReadString(root, "user_code");
        string deviceCode = ReadString(root, "device_code");
        string verificationUri = ReadString(root, "verification_uri");
        string verificationUriComplete = FirstNonEmpty(
            ReadString(root, "verification_uri_complete"),
            verificationUri);
        int expiresIn = (int)Math.Clamp(ReadInt64(root, "expires_in", 300), 30, int.MaxValue);
        int interval = (int)Math.Clamp(ReadInt64(root, "interval", 5), 1, 120);
        if (string.IsNullOrWhiteSpace(userCode) ||
            string.IsNullOrWhiteSpace(deviceCode) ||
            string.IsNullOrWhiteSpace(verificationUri))
        {
            throw new InvalidDataException("LittleSkin 设备代码响应缺少必要字段。");
        }

        PortableLog.Info(
            "LittleSkinAuth",
            $"设备代码已申请；user_code={userCode}；expires_in={expiresIn}s。");
        return new LittleSkinDeviceCodeInfo(
            userCode,
            deviceCode,
            verificationUri,
            verificationUriComplete,
            expiresIn,
            interval);
    }

    public async Task<LittleSkinOAuthTokens> WaitForDeviceAuthorizationAsync(
        LittleSkinOAuthConfiguration configuration,
        LittleSkinDeviceCodeInfo deviceCode,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(deviceCode);

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresInSeconds);
        int intervalMs = Math.Max(1000, deviceCode.IntervalSeconds * 1000);
        double started = Environment.TickCount64;
        double totalMs = Math.Max(1, deviceCode.ExpiresInSeconds * 1000d);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(Math.Clamp((Environment.TickCount64 - started) / totalMs * 0.55d, 0d, 0.55d));

            using FormUrlEncodedContent form = new(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = configuration.ClientId,
                    ["device_code"] = deviceCode.DeviceCode
                });
            using HttpRequestMessage request = new(HttpMethod.Post, OpenTokenEndpoint)
            {
                Content = form
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            string? requestId = TryGetRequestId(response);

            if (response.IsSuccessStatusCode)
            {
                progress?.Report(0.6d);
                return ParseTokenResponse(body, fallbackRefreshToken: string.Empty, requireRefreshToken: true);
            }

            string error = TryReadOAuthError(body);
            if (string.Equals(error, "authorization_pending", StringComparison.Ordinal))
            {
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(error, "slow_down", StringComparison.Ordinal))
            {
                intervalMs = Math.Min(intervalMs + 5000, 60_000);
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            ThrowIfInvalidClient(body, requestId);
            EnsureSuccess(response, body, "LittleSkin 设备授权失败", requestId);
        }

        throw new TimeoutException("LittleSkin 设备授权超时，请重新发起登录。");
    }

    public Task<LittleSkinOAuthTokens> ExchangeAuthorizationCodeAsync(
        LittleSkinOAuthConfiguration configuration,
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (string.IsNullOrWhiteSpace(configuration.ClientSecret))
        {
            throw new InvalidOperationException(
                "授权代码流需要 PCL_LITTLESKIN_CLIENT_SECRET。桌面启动器请使用设备代码流。");
        }

        return RequestTokensAsync(
            PassportTokenEndpoint,
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
            requireRefreshToken: true,
            cancellationToken);
    }

    public async Task<LittleSkinOAuthTokens> RefreshOAuthTokenAsync(
        LittleSkinOAuthConfiguration configuration,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        // Device-flow tokens (primary for launchers) refresh on open.littleskin.cn.
        try
        {
            return await RequestTokensAsync(
                    OpenTokenEndpoint,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = configuration.ClientId,
                        ["refresh_token"] = refreshToken
                    },
                    refreshToken,
                    "刷新 LittleSkin OAuth 令牌失败",
                    requireRefreshToken: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!string.IsNullOrWhiteSpace(configuration.ClientSecret))
        {
            // Legacy authorization-code tokens refresh on littleskin.cn with client_secret.
            return await RequestTokensAsync(
                    PassportTokenEndpoint,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = configuration.ClientId,
                        ["client_secret"] = configuration.ClientSecret,
                        ["refresh_token"] = refreshToken
                    },
                    refreshToken,
                    "刷新 LittleSkin OAuth 令牌失败",
                    requireRefreshToken: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// GET https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile
    /// Requires <c>Yggdrasil.PlayerProfiles.Read</c>.
    /// </summary>
    public async Task<IReadOnlyList<LittleSkinProfile>> GetProfilesAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        string body = await SendBearerAsync(
                HttpMethod.Get,
                new Uri(YggdrasilApiRoot + "sessionserver/session/minecraft/profile"),
                accessToken,
                content: null,
                "获取 LittleSkin 角色档案失败",
                cancellationToken)
            .ConfigureAwait(false);

        // Unauthorized resource may return HTTP 200 with code=403.
        if (TryReadApiErrorCode(body, out int code) && code == 403)
        {
            throw new HttpRequestException(
                "获取 LittleSkin 角色档案失败：访问令牌缺少 Yggdrasil.PlayerProfiles.Read 权限，请重新授权。",
                null,
                HttpStatusCode.Forbidden);
        }

        List<LittleSkinProfile> profiles = ParseProfileArray(body);
        PortableLog.Info("LittleSkinAuth", $"角色档案列表已加载；count={profiles.Count}。");
        return profiles;
    }

    /// <summary>
    /// POST https://littleskin.cn/api/yggdrasil/authserver/oauth
    /// Body: <c>{"uuid":"&lt;undashed&gt;"}</c>. Requires <c>Yggdrasil.MinecraftToken.Create</c>.
    /// </summary>
    public async Task<LittleSkinMinecraftSession> CreateMinecraftSessionAsync(
        string accessToken,
        string uuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
        string normalizedUuid = NormalizeUuid(uuid);
        string payload =
            "{\"uuid\":\"" + EscapeJsonString(normalizedUuid) + "\"}";
        string body = await SendBearerAsync(
                HttpMethod.Post,
                new Uri(YggdrasilApiRoot + "authserver/oauth"),
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
                [field] = textureId.ToString(CultureInfo.InvariantCulture)
            });
        string body = await SendBearerAsync(
                HttpMethod.Put,
                new Uri(ApiRoot + $"players/{playerId}/textures"),
                accessToken,
                content,
                kind == LittleSkinTextureKind.Cape
                    ? "更换 LittleSkin 披风失败"
                    : "更换 LittleSkin 皮肤失败",
            cancellationToken)
            .ConfigureAwait(false);
        EnsureApiOperationSucceeded(body, kind == LittleSkinTextureKind.Cape
            ? "更换 LittleSkin 披风失败"
            : "更换 LittleSkin 皮肤失败");
        PortableLog.Info(
            "LittleSkinAppearance",
            $"角色材质已更新；PlayerId={playerId}；Kind={kind}；TextureId={textureId}。");
    }

    public async Task EnsureClosetTextureAsync(
        string accessToken,
        long textureId,
        string name,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(textureId);
        IReadOnlyList<LittleSkinClosetItem> items =
            await GetClosetItemsAsync(accessToken, kind, cancellationToken).ConfigureAwait(false);
        if (items.Any(item => item.TextureId == textureId))
            return;

        using FormUrlEncodedContent content = new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tid"] = textureId.ToString(CultureInfo.InvariantCulture),
                ["name"] = string.IsNullOrWhiteSpace(name) ? "PCL N Texture" : name.Trim()
            });
        string body = await SendBearerAsync(
                HttpMethod.Post,
                new Uri(ApiRoot + "closet"),
                accessToken,
                content,
                "加入 LittleSkin 衣柜失败",
                cancellationToken)
            .ConfigureAwait(false);
        EnsureApiOperationSucceeded(body, "加入 LittleSkin 衣柜失败");
        PortableLog.Info(
            "LittleSkinAppearance",
            $"材质已加入衣柜；Kind={kind}；TextureId={textureId}。");
    }

    /// <summary>
    /// Uploads and applies a private skin through the authlib-injector compatible
    /// Yggdrasil profile API. This endpoint authenticates with the Minecraft session
    /// token, not the provider OAuth token.
    /// </summary>
    public async Task<LittleSkinTextureUploadResult> UploadMinecraftTextureAsync(
        string minecraftAccessToken,
        string profileUuid,
        byte[] pngBytes,
        string fileName,
        bool isSlim,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftAccessToken);
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
            throw new ArgumentException("皮肤文件为空。", nameof(pngBytes));
        string uuid = NormalizeUuid(profileUuid);
        if (uuid.Length != 32)
            throw new ArgumentException("LittleSkin 角色 UUID 无效。", nameof(profileUuid));

        using MultipartFormDataContent content = new();
        content.Add(new StringContent(isSlim ? "slim" : string.Empty), "model");
        ByteArrayContent fileContent = new(pngBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(
            fileContent,
            "file",
            string.IsNullOrWhiteSpace(fileName) ? "skin.png" : Path.GetFileName(fileName));
        using HttpRequestMessage request = new(
            HttpMethod.Put,
            new Uri(YggdrasilApiRoot + $"api/user/profile/{uuid}/skin"))
        {
            Content = content
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", minecraftAccessToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, "上传并应用 LittleSkin 皮肤失败", TryGetRequestId(response));
        PortableLog.Info(
            "LittleSkinAppearance",
            $"自定义皮肤已通过 Yggdrasil 接口上传并应用；Profile={uuid}；Slim={isSlim}。");
        return new LittleSkinTextureUploadResult(uuid, LittleSkinTextureKind.Skin, isSlim);
    }

    private async Task<LittleSkinOAuthTokens> RequestTokensAsync(
        string endpoint,
        Dictionary<string, string> form,
        string fallbackRefreshToken,
        string operation,
        bool requireRefreshToken,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, operation, TryGetRequestId(response));
        return ParseTokenResponse(body, fallbackRefreshToken, requireRefreshToken);
    }

    private static LittleSkinOAuthTokens ParseTokenResponse(
        string body,
        string fallbackRefreshToken,
        bool requireRefreshToken)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string accessToken = ReadString(root, "access_token");
        string refreshToken = FirstNonEmpty(ReadString(root, "refresh_token"), fallbackRefreshToken);
        string idToken = ReadString(root, "id_token");
        int expiresIn = (int)Math.Clamp(ReadInt64(root, "expires_in", 259200), 1, int.MaxValue);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidDataException("LittleSkin OAuth 响应缺少访问令牌。");
        if (requireRefreshToken && string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidDataException("LittleSkin OAuth 响应缺少刷新令牌，无法维持长期登录。");
        return new LittleSkinOAuthTokens(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            string.IsNullOrWhiteSpace(idToken) ? null : idToken);
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
        // Docs: Authorization: Bearer {{access_token}} + Accept: application/json
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken.Trim());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, operation, TryGetRequestId(response));
        return body;
    }

    private static void ThrowIfInvalidClient(string body, string? requestId = null)
    {
        if (!string.Equals(TryReadOAuthError(body), "invalid_client", StringComparison.Ordinal))
            return;

        string message = InvalidClientUserMessage;
        if (!string.IsNullOrWhiteSpace(requestId))
            message += " 请求 ID：" + requestId;
        PortableLog.Warn("LittleSkinAuth", message);
        throw new InvalidOperationException(message);
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string body,
        string operation,
        string? requestId = null)
    {
        if (response.IsSuccessStatusCode)
            return;

        ThrowIfInvalidClient(body, requestId);

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
        if (!string.IsNullOrWhiteSpace(requestId))
            message += " 请求 ID：" + requestId;
        PortableLog.Error("LittleSkinAuth", message);
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string? TryGetRequestId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Yggdralt-Req-ID", out IEnumerable<string>? values))
            return values.FirstOrDefault();
        if (response.Headers.TryGetValues("X-Yggdrasil-Req-ID", out values))
            return values.FirstOrDefault();
        return null;
    }

    private static string TryReadOAuthError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static bool TryReadApiErrorCode(string body, out int code)
    {
        code = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!TryReadInt64(document.RootElement, "code", out long value) ||
                value is < int.MinValue or > int.MaxValue)
            {
                return false;
            }

            code = (int)value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void EnsureApiOperationSucceeded(string body, string operation)
    {
        if (!TryReadApiErrorCode(body, out int code) || code == 0)
            return;

        string detail = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            detail = ReadString(document.RootElement, "message");
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail) ? operation : operation + "：" + detail);
    }

    private static List<LittleSkinProfile> ParseProfileArray(string body)
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

    private static string EscapeJsonString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

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
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
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
