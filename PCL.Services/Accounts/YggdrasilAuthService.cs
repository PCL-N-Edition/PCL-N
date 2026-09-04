using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Services.Logging;

namespace PCL.Services.Accounts;

/// <summary>One Yggdrasil authenticate/refresh request.</summary>
public sealed record YggdrasilAuthLoginRequest(
    string Server,
    string Username,
    string Password,
    string? ClientToken = null);

/// <summary>One Yggdrasil authenticate/refresh outcome, credentials included.</summary>
public sealed record YggdrasilAuthLoginResult(
    string Username,
    string Uuid,
    string AccessToken,
    string AuthServer,
    string AuthServerDisplayName,
    string ClientToken = "",
    string RefreshToken = "");

/// <summary>
/// Yggdrasil third-party authentication: authenticate, validate, and refresh against any
/// authlib-injector style server. The server URL normalizes to the `/api/yggdrasil` root.
/// The HttpClient is caller-owned, so tests fixture the transport.
/// </summary>
public sealed class YggdrasilAuthService
{
    private readonly HttpClient _httpClient;
    private readonly LogService? _log;

    public YggdrasilAuthService(HttpClient? httpClient = null, LogService? log = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _log = log;
    }

    /// <summary>
    /// Authenticates with username and password, requesting a stable client token when the
    /// caller has none.
    /// </summary>
    public async Task<YggdrasilAuthLoginResult> AuthenticateAsync(
        YggdrasilAuthLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Server) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ArgumentException("认证服务器、邮箱和密码不能为空。", nameof(request));
        }

        string authServer = NormalizeYggdrasilServer(request.Server);
        string clientToken = string.IsNullOrWhiteSpace(request.ClientToken)
            ? Guid.NewGuid().ToString("N")
            : request.ClientToken.Trim();
        var payload = new JsonObject
        {
            ["agent"] = new JsonObject
            {
                ["name"] = "Minecraft",
                ["version"] = 1,
            },
            ["username"] = request.Username,
            ["password"] = request.Password,
            ["clientToken"] = clientToken,
            ["requestUser"] = true,
        };

        (HttpStatusCode statusCode, JsonObject? json, string body) = await PostAsync(
            $"{authServer}/authserver/authenticate", payload, cancellationToken).ConfigureAwait(false);
        if (!IsSuccessful(statusCode, json))
        {
            _log?.Warn("YggdrasilAuth", $"Authentication rejected http_status={(int)statusCode}");
            throw CreateAuthException(statusCode, json, body);
        }

        return ParseLoginResult(json, authServer);
    }

    /// <summary>
    /// Validates a stored access token against the auth server (Yggdrasil validate). Per the
    /// spec, success status means valid, anything else means invalid.
    /// </summary>
    public async Task<bool> ValidateAsync(
        string authServer,
        string accessToken,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        string server = NormalizeYggdrasilServer(authServer);
        var payload = new JsonObject { ["accessToken"] = accessToken };
        if (!string.IsNullOrWhiteSpace(clientToken))
        {
            payload["clientToken"] = clientToken;
        }

        try
        {
            (HttpStatusCode statusCode, _, _) = await PostAsync(
                $"{server}/authserver/validate", payload, cancellationToken).ConfigureAwait(false);
            return (int)statusCode is >= 200 and < 300;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            _log?.Write(cancellationToken.IsCancellationRequested ? LogLevel.Debug : LogLevel.Warn,
                "YggdrasilAuth", "Stored session validation did not complete.", ExceptionDiagnostics.Describe(failure));
            return false;
        }
    }

    /// <summary>
    /// Refreshes an access token (Yggdrasil refresh). Prefer this when validate fails or the
    /// JWT is near expiry.
    /// </summary>
    public async Task<YggdrasilAuthLoginResult> RefreshAsync(
        string authServer,
        string accessToken,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("访问令牌为空，无法刷新。", nameof(accessToken));
        }

        string server = NormalizeYggdrasilServer(authServer);
        var payload = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["requestUser"] = true,
        };
        if (!string.IsNullOrWhiteSpace(clientToken))
        {
            payload["clientToken"] = clientToken;
        }

        (HttpStatusCode statusCode, JsonObject? json, string body) = await PostAsync(
            $"{server}/authserver/refresh", payload, cancellationToken).ConfigureAwait(false);
        if (!IsSuccessful(statusCode, json))
        {
            _log?.Warn("YggdrasilAuth", $"Session refresh rejected http_status={(int)statusCode}");
            throw CreateAuthException(statusCode, json, body);
        }

        return ParseLoginResult(json, server);
    }

    /// <summary>
    /// Returns true when a JWT access token has not yet reached its `exp` claim (with a
    /// two-minute skew by default). Opaque tokens and unreadable payloads count as unexpired —
    /// the server decides.
    /// </summary>
    public static bool IsJwtAccessTokenUnexpired(string? accessToken, TimeSpan? skew = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        string[] parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return true;
        }

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
        catch (Exception failure) when (
            failure is FormatException or System.Text.Json.JsonException or ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    /// <summary>
    /// Normalizes a Yggdrasil server address to its `/api/yggdrasil` root: adds the scheme,
    /// strips trailing slashes and an `/authserver` suffix, and appends the API root.
    /// </summary>
    public static string NormalizeYggdrasilServer(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        string normalized = server.Trim().TrimEnd('/');
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "https://" + normalized;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("认证服务器地址无效。", nameof(server));
        }

        normalized = uri.ToString().TrimEnd('/');
        if (normalized.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/authserver".Length].TrimEnd('/');
        }

        if (!normalized.EndsWith("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/api/yggdrasil";
        }

        return normalized;
    }

    private async Task<(HttpStatusCode StatusCode, JsonObject? Json, string Body)> PostAsync(
        string url,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient
            .PostAsync(url, content, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, TryParseObject(body), body);
    }

    private static bool IsSuccessful(HttpStatusCode statusCode, JsonObject? json) =>
        (int)statusCode is >= 200 and < 300 && json?["error"] is null;

    private static YggdrasilAuthLoginResult ParseLoginResult(JsonObject? json, string authServer)
    {
        string accessToken = json?["accessToken"]?.ToString() ?? string.Empty;
        string clientToken = json?["clientToken"]?.ToString() ?? string.Empty;
        string refreshToken = json?["refreshToken"]?.ToString() ?? string.Empty;
        JsonObject? profile = json?["selectedProfile"] as JsonObject ??
                              (json?["availableProfiles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        string uuid = profile?["id"]?.ToString() ?? string.Empty;
        string username = profile?["name"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(uuid) ||
            string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("第三方认证成功，但服务器没有返回可用的 Minecraft 档案。");
        }

        return new YggdrasilAuthLoginResult(
            username,
            uuid,
            accessToken,
            authServer,
            GetServerDisplayName(authServer),
            clientToken,
            refreshToken);
    }

    private static JsonObject? TryParseObject(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(responseBody) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
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
        {
            return new InvalidOperationException(errorMessage);
        }

        string? error = json?["error"]?.ToString();
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new InvalidOperationException(error);
        }

        if ((int)statusCode is 401 or 403)
        {
            return new InvalidOperationException("认证服务器拒绝了登录请求。请检查邮箱和密码。");
        }

        string detail = string.IsNullOrWhiteSpace(responseBody) ? statusCode.ToString() : responseBody;
        return new InvalidOperationException("认证服务器返回了无法识别的响应：" + detail);
    }

    private static string GetServerDisplayName(string authServer)
    {
        return Uri.TryCreate(authServer, UriKind.Absolute, out Uri? uri) ? uri.Host : "第三方认证";
    }
}
