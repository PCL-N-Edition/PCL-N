// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.Logging;

namespace PCL.Application.Accounts;

public sealed record ThirdPartyAuthLoginRequest(
    string Server,
    string Username,
    string Password,
    string? ClientToken = null);

public sealed record ThirdPartyAuthLoginResult(
    string Username,
    string Uuid,
    string AccessToken,
    string AuthServer,
    string AuthServerDisplayName,
    string ClientToken = "",
    string RefreshToken = "");

public sealed class ThirdPartyAuthService(HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly HttpClient _httpClient = httpClient ?? SharedClient;

    public async Task<ThirdPartyAuthLoginResult> AuthenticateAsync(
        ThirdPartyAuthLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string authServer = NormalizeYggdrasilServer(request.Server);
        PortableLog.Info("ThirdPartyAuth", $"开始第三方认证；服务器={GetServerDisplayName(authServer)}。");
        PortableLog.Debug("ThirdPartyAuth", $"认证参数：Endpoint={authServer}/authserver/authenticate；Username={request.Username}。");
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ArgumentException("认证服务器、邮箱和密码不能为空。", nameof(request));
        }

        // Stable clientToken improves validate/refresh on many Yggdrasil servers (LittleSkin).
        string clientToken = string.IsNullOrWhiteSpace(request.ClientToken)
            ? Guid.NewGuid().ToString("N")
            : request.ClientToken.Trim();

        JsonObject payload = new()
        {
            ["agent"] = new JsonObject
            {
                ["name"] = "Minecraft",
                ["version"] = 1
            },
            ["username"] = request.Username,
            ["password"] = request.Password,
            ["clientToken"] = clientToken,
            ["requestUser"] = true
        };

        using StringContent content = new(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await _httpClient
            .PostAsync($"{authServer}/authserver/authenticate", content, cancellationToken)
            .ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        JsonObject? json = TryParseObject(responseBody);
        if (!response.IsSuccessStatusCode || json?["error"] is not null)
        {
            InvalidOperationException exception = CreateAuthException(response.StatusCode, json, responseBody);
            PortableLog.Error(exception, "ThirdPartyAuth", $"第三方认证失败；服务器={GetServerDisplayName(authServer)}；HTTP={(int)response.StatusCode}。");
            throw exception;
        }

        return ParseLoginResult(json, authServer, "认证");
    }

    /// <summary>
    /// Validates a stored access token against the auth server (Yggdrasil validate).
    /// </summary>
    public async Task<bool> ValidateAsync(
        string authServer,
        string accessToken,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return false;

        string server = NormalizeYggdrasilServer(authServer);
        JsonObject payload = new() { ["accessToken"] = accessToken };
        if (!string.IsNullOrWhiteSpace(clientToken))
            payload["clientToken"] = clientToken;

        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using HttpResponseMessage response = await _httpClient
                .PostAsync($"{server}/authserver/validate", content, cancellationToken)
                .ConfigureAwait(false);
            // Spec: 204 No Content = valid; 403 = invalid.
            if (response.IsSuccessStatusCode)
            {
                PortableLog.Debug("ThirdPartyAuth", $"访问令牌校验通过；服务器={GetServerDisplayName(server)}。");
                return true;
            }

            PortableLog.Warn(
                "ThirdPartyAuth",
                $"访问令牌校验失败；服务器={GetServerDisplayName(server)}；HTTP={(int)response.StatusCode}。");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            PortableLog.Warn(ex, "ThirdPartyAuth", "访问令牌校验请求失败，将尝试 refresh。");
            return false;
        }
    }

    /// <summary>
    /// Refreshes an access token (Yggdrasil refresh). Prefer when JWT is near expiry or validate fails.
    /// </summary>
    public async Task<ThirdPartyAuthLoginResult> RefreshAsync(
        string authServer,
        string accessToken,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("访问令牌为空，无法刷新。", nameof(accessToken));

        string server = NormalizeYggdrasilServer(authServer);
        PortableLog.Info("ThirdPartyAuth", $"刷新第三方访问令牌；服务器={GetServerDisplayName(server)}。");
        JsonObject payload = new()
        {
            ["accessToken"] = accessToken,
            ["requestUser"] = true
        };
        if (!string.IsNullOrWhiteSpace(clientToken))
            payload["clientToken"] = clientToken;

        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient
            .PostAsync($"{server}/authserver/refresh", content, cancellationToken)
            .ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        JsonObject? json = TryParseObject(responseBody);
        if (!response.IsSuccessStatusCode || json?["error"] is not null)
        {
            InvalidOperationException exception = CreateAuthException(response.StatusCode, json, responseBody);
            PortableLog.Error(
                exception,
                "ThirdPartyAuth",
                $"刷新第三方令牌失败；服务器={GetServerDisplayName(server)}；HTTP={(int)response.StatusCode}。");
            throw exception;
        }

        return ParseLoginResult(json, server, "刷新");
    }

    private static ThirdPartyAuthLoginResult ParseLoginResult(JsonObject? json, string authServer, string actionLabel)
    {
        string accessToken = json?["accessToken"]?.ToString() ?? "";
        string clientToken = json?["clientToken"]?.ToString() ?? "";
        // Some Yggdrasil Connect servers also return refreshToken.
        string refreshToken = json?["refreshToken"]?.ToString() ?? "";
        JsonObject? profile = json?["selectedProfile"] as JsonObject ??
                              (json?["availableProfiles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        string uuid = profile?["id"]?.ToString() ?? "";
        string username = profile?["name"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(uuid) ||
            string.IsNullOrWhiteSpace(username))
        {
            PortableLog.Error("ThirdPartyAuth", $"第三方{actionLabel}响应缺少可用档案；服务器={GetServerDisplayName(authServer)}。");
            throw new InvalidOperationException($"第三方{actionLabel}成功，但服务器没有返回可用的 Minecraft 档案。");
        }

        PortableLog.Info("ThirdPartyAuth", $"第三方{actionLabel}完成；服务器={GetServerDisplayName(authServer)}；玩家={username}。");
        return new ThirdPartyAuthLoginResult(
            username,
            uuid,
            accessToken,
            authServer,
            GetServerDisplayName(authServer),
            clientToken,
            refreshToken);
    }

    /// <summary>Returns true when a JWT access token has not yet reached its exp claim (with skew).</summary>
    public static bool IsJwtAccessTokenUnexpired(string? accessToken, TimeSpan? skew = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return false;

        string[] parts = accessToken.Split('.');
        if (parts.Length < 2)
            return true; // Opaque token — cannot judge from payload.

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty("exp", out System.Text.Json.JsonElement expiration) ||
                !expiration.TryGetInt64(out long seconds))
            {
                return true;
            }

            TimeSpan margin = skew ?? TimeSpan.FromMinutes(2);
            return DateTimeOffset.FromUnixTimeSeconds(seconds) > DateTimeOffset.UtcNow.Add(margin);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    public static string NormalizeYggdrasilServer(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        string normalized = server.Trim().TrimEnd('/');
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "https://" + normalized;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("认证服务器地址无效。", nameof(server));
        }

        normalized = uri.ToString().TrimEnd('/');
        if (normalized.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^"/authserver".Length].TrimEnd('/');
        if (!normalized.EndsWith("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
            normalized += "/api/yggdrasil";
        return normalized;
    }

    private static JsonObject? TryParseObject(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            return JsonNode.Parse(responseBody) as JsonObject;
        }
        catch (Exception ex)
        {
            PortableLog.Debug(ex, "ThirdPartyAuth", "认证服务器返回的正文不是有效 JSON。");
            return null;
        }
    }

    private static InvalidOperationException CreateAuthException(
        HttpStatusCode statusCode,
        JsonObject? json,
        string responseBody)
    {
        string? errorMessage = json?["errorMessage"]?.ToString();
        if (!string.IsNullOrWhiteSpace(errorMessage))
            return new InvalidOperationException(errorMessage);

        string? error = json?["error"]?.ToString();
        if (!string.IsNullOrWhiteSpace(error))
            return new InvalidOperationException(error);

        if ((int)statusCode is 401 or 403)
            return new InvalidOperationException("认证服务器拒绝了登录请求。请检查邮箱和密码。");

        string detail = string.IsNullOrWhiteSpace(responseBody)
            ? statusCode.ToString()
            : responseBody;
        return new InvalidOperationException("认证服务器返回了无法识别的响应：" + detail);
    }

    private static string GetServerDisplayName(string authServer)
    {
        if (Uri.TryCreate(authServer, UriKind.Absolute, out Uri? uri))
            return uri.Host;

        return "第三方认证";
    }
}
