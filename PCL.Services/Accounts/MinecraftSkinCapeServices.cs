using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Services.Accounts;

/// <summary>The outcome of a Microsoft-profile skin upload.</summary>
public sealed record MinecraftSkinUploadResult(string? SkinAddress);

/// <summary>
/// Uploads a skin owned by the authenticated Microsoft Minecraft profile; the JSON profile
/// response is parsed for the canonical active texture URL.
/// </summary>
public sealed class MinecraftSkinService
{
    private static readonly Uri SkinEndpoint =
        new("https://api.minecraftservices.com/minecraft/profile/skins");

    private readonly HttpClient _client;

    public MinecraftSkinService(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<MinecraftSkinUploadResult> UploadAsync(
        string accessToken,
        byte[] pngBytes,
        string fileName,
        bool isSlim,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
        {
            throw new ArgumentException("皮肤文件为空。", nameof(pngBytes));
        }

        using MultipartFormDataContent content = new();
        content.Add(new StringContent(isSlim ? "slim" : "classic"), "variant");
        ByteArrayContent fileContent = new(pngBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(
            fileContent,
            "file",
            string.IsNullOrWhiteSpace(fileName) ? "skin.png" : Path.GetFileName(fileName));

        using HttpRequestMessage request = new(HttpMethod.Post, SkinEndpoint)
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body);
        return new MinecraftSkinUploadResult(ParseActiveSkinAddress(body));
    }

    /// <summary>Parses the canonical active texture URL; malformed bodies never throw.</summary>
    public static string? ParseActiveSkinAddress(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("skins", out JsonElement skins) ||
                skins.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? fallback = null;
            foreach (JsonElement skin in skins.EnumerateArray())
            {
                if (skin.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string state = ReadString(skin, "state");
                string address = ReadString(skin, "url");
                if (!TryNormalizeHttpAddress(address, out string? normalized))
                {
                    continue;
                }

                fallback ??= normalized;
                if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    return normalized;
                }
            }

            return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool TryNormalizeHttpAddress(string address, out string? normalized)
    {
        normalized = null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string responseBody)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = string.IsNullOrWhiteSpace(responseBody)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
            : responseBody.Trim();
        throw new HttpRequestException("更换正版皮肤失败：" + detail, null, response.StatusCode);
    }
}

/// <summary>One cape owned by the authenticated Microsoft profile.</summary>
public sealed record MinecraftOwnedCape(
    string Id,
    string Alias,
    string TextureAddress,
    bool IsActive);

/// <summary>Lists and activates capes owned by the authenticated Microsoft profile.</summary>
public sealed class MinecraftCapeService
{
    private static readonly Uri ProfileEndpoint =
        new("https://api.minecraftservices.com/minecraft/profile");
    private static readonly Uri ActiveCapeEndpoint =
        new("https://api.minecraftservices.com/minecraft/profile/capes/active");

    private readonly HttpClient _client;

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
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, "读取正版账户披风失败");
        using JsonDocument document = JsonDocument.Parse(body);
        return ParseOwnedCapes(document.RootElement);
    }

    /// <summary>
    /// Activates one cape. Minecraft refuses capes the account does not own, and so do we —
    /// ownership is checked before the request.
    /// </summary>
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

        JsonObject payload = new() { ["capeId"] = normalizedId };
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, ActiveCapeEndpoint, accessToken);
        request.Content = content;
        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, responseBody, "更换正版披风失败");
    }

    public static IReadOnlyList<MinecraftOwnedCape> ParseOwnedCapes(JsonElement profile)
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
            {
                continue;
            }

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
                string.Equals(ReadString(cape, "state"), "ACTIVE", StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }

    /// <summary>
    /// Prefers the ACTIVE owned cape texture; otherwise keeps the sessionserver CAPE URL.
    /// </summary>
    public static string? PreferCapePreviewAddress(
        IReadOnlyList<MinecraftOwnedCape> ownedCapes,
        string? sessionCapeAddress)
    {
        ArgumentNullException.ThrowIfNull(ownedCapes);
        MinecraftOwnedCape? active = ownedCapes.FirstOrDefault(static cape => cape.IsActive);
        if (active is not null && !string.IsNullOrWhiteSpace(active.TextureAddress))
        {
            return active.TextureAddress;
        }

        return string.IsNullOrWhiteSpace(sessionCapeAddress) ? null : sessionCapeAddress;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint, string accessToken)
    {
        HttpRequestMessage request = new(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void EnsureAccessToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("当前正版档案缺少访问令牌，请重新登录。");
        }
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string responseBody,
        string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

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
}
