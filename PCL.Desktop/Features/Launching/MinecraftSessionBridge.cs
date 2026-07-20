// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Application.Launching;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Session-scoped loopback implementation used by the experimental JVM host.
/// It deliberately exposes no non-loopback listener and accepts only the small
/// Yggdrasil/Minecraft-services surface needed by authlib.
/// </summary>
internal sealed class MinecraftSessionBridge : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxTextureBytes = 4 * 1024 * 1024;

    private readonly MinecraftJvmHostRequest _request;
    private readonly JvmHostLifecycleWriter _lifecycle;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, string> _textureSources = new(StringComparer.Ordinal);
    private readonly AuthServerMetadata _metadata;
    private readonly Task _acceptLoop;
    private readonly string? _offlineTextureToken;
    private readonly JsonObject _playerCertificates;
    private readonly SemaphoreSlim _thirdPartyProfileGate = new(1, 1);
    private JsonObject? _thirdPartyServicesProfile;

    private MinecraftSessionBridge(MinecraftJvmHostRequest request, JvmHostLifecycleWriter lifecycle)
    {
        _request = request;
        _lifecycle = lifecycle;
        _metadata = AuthServerMetadata.Parse(request.AuthServerMetadata);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 16);
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // SocketsHttpHandler + ConnectTimeout prevents hung DNS when best-effort skin hydration fails.
        // Wrap the system proxy so loopback auth mocks/unit tests never go through a corporate proxy.
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
            Proxy = new LoopbackBypassProxy(HttpClient.DefaultProxy),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        // Session-scoped chat signing keys. Empty PEMs make the client report "profile public key missing".
        _playerCertificates = CreatePlayerCertificates();

        if (request.IdentityMode == MinecraftJvmHostIdentityMode.Offline &&
            IsUsableSkinSource(request.OfflineSkinSource))
        {
            _offlineTextureToken = RegisterTextureSource(request.OfflineSkinSource!);
        }

        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public string BaseUrl { get; }

    public static MinecraftSessionBridge Start(
        MinecraftJvmHostRequest request,
        JvmHostLifecycleWriter lifecycle)
    {
        if (request.IdentityMode == MinecraftJvmHostIdentityMode.ThirdParty &&
            (string.IsNullOrWhiteSpace(request.AuthServer) ||
             string.IsNullOrWhiteSpace(request.AccessToken) ||
             request.AccessToken == "0"))
        {
            throw new InvalidDataException("第三方档案缺少认证服务器地址或访问令牌。");
        }

        return new MinecraftSessionBridge(request, lifecycle);
    }

    public void AppendJvmProperties(List<string> arguments)
    {
        arguments.Add("-Dminecraft.api.session.host=" + BaseUrl + "/sessionserver");
        arguments.Add("-Dminecraft.api.services.host=" + BaseUrl + "/minecraftservices");
        arguments.Add("-Dminecraft.api.profiles.host=" + BaseUrl + "/api");
        arguments.Add("-Dminecraft.api.discovery.host=" + BaseUrl + "/minecraft/client");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientSafeAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientSafeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();
                HttpRequestData request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                HttpResponseData response = await RouteAsync(request, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or
                                           OperationCanceledException or SocketException or JsonException or
                                           FormatException or CryptographicException)
            {
                try
                {
                    using NetworkStream stream = client.GetStream();
                    HttpResponseData failure = ex is OperationCanceledException
                        ? new HttpResponseData(503, "Service Unavailable", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bridge shutting down"))
                        : new HttpResponseData(502, "Bad Gateway", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("PCL N session bridge: " + ex.Message));
                    await WriteResponseAsync(stream, failure, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The client may already have closed the one-shot HTTP/1.1 connection.
                }
            }
        }
    }

    private async Task<HttpResponseData> RouteAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        if (!request.Target.StartsWith('/'))
            return TextResponse(400, "Bad Request", "Only origin-form HTTP targets are accepted.");

        string path = request.Target.Split('?', 2)[0];
        if (string.Equals(path, "/minecraft/client", StringComparison.OrdinalIgnoreCase))
            return JsonResponse(CreateDiscoveryDocument());
        if (string.Equals(path, "/pcl/publickeys", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(new JsonObject
            {
                ["profilePropertyKeys"] = new JsonArray(),
                ["playerCertificateKeys"] = new JsonArray()
            });
        }

        const string texturePrefix = "/pcl/texture/";
        if (path.StartsWith(texturePrefix, StringComparison.Ordinal))
        {
            string token = path[texturePrefix.Length..];
            return await ServeTextureAsync(token, cancellationToken).ConfigureAwait(false);
        }

        if (_request.IdentityMode == MinecraftJvmHostIdentityMode.ThirdParty &&
            path.StartsWith("/minecraftservices/", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleThirdPartyMinecraftServicesAsync(request, path, cancellationToken)
                .ConfigureAwait(false);
        }

        return _request.IdentityMode switch
        {
            MinecraftJvmHostIdentityMode.Offline => HandleOfflineRequest(request, path),
            MinecraftJvmHostIdentityMode.ThirdParty => await ProxyThirdPartyAsync(request, path, cancellationToken)
                .ConfigureAwait(false),
            _ => TextResponse(404, "Not Found", "The session bridge is disabled for this profile.")
        };
    }

    private HttpResponseData HandleOfflineRequest(HttpRequestData request, string path)
    {
        _lifecycle.SendOnce("OfflineSession", "离线档案会话已由 Jvm.NET Host 接管");
        if (request.Method == "POST" &&
            string.Equals(path, "/sessionserver/session/minecraft/join", StringComparison.OrdinalIgnoreCase))
        {
            return EmptyResponse(204, "No Content");
        }

        if (request.Method == "GET" &&
            path.StartsWith("/sessionserver/session/minecraft/profile/", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(CreateOfflineProfile());
        }

        if (request.Method == "GET" &&
            string.Equals(path, "/sessionserver/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(CreateOfflineProfile());
        }

        if (request.Method == "GET" && path.StartsWith("/api/users/profiles/minecraft/", StringComparison.OrdinalIgnoreCase))
            return JsonResponse(CreateNameAndId());

        if (request.Method == "POST" && string.Equals(path, "/api/profiles/minecraft", StringComparison.OrdinalIgnoreCase))
            return JsonResponse(new JsonArray(CreateNameAndId()));

        if (request.Method == "GET" && string.Equals(path, "/minecraftservices/minecraft/profile", StringComparison.OrdinalIgnoreCase))
            return JsonResponse(CreateOfflineServicesProfile());

        if (request.Method == "POST" &&
            string.Equals(path, "/minecraftservices/player/certificates", StringComparison.OrdinalIgnoreCase))
        {
            _lifecycle.SendOnce("PlayerCertificates", "会话聊天签名密钥已由 Jvm.NET Host 生成本地密钥对");
            return JsonResponse(CloneJsonObject(_playerCertificates));
        }

        if (request.Method == "GET" &&
            string.Equals(path, "/minecraftservices/player/attributes", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(CreatePlayerAttributes());
        }

        return TextResponse(404, "Not Found", "Offline endpoint is not required by this authlib version.");
    }

    private async Task<HttpResponseData> HandleThirdPartyMinecraftServicesAsync(
        HttpRequestData request,
        string path,
        CancellationToken cancellationToken)
    {
        if (request.Method == "GET" &&
            string.Equals(path, "/minecraftservices/minecraft/profile", StringComparison.OrdinalIgnoreCase))
        {
            JsonObject profile = await GetOrCreateThirdPartyServicesProfileAsync(cancellationToken)
                .ConfigureAwait(false);
            _lifecycle.SendOnce("ThirdPartyProfile", "第三方档案已由 PCL N 本机会话提供");
            return JsonResponse(CloneJsonObject(profile));
        }

        if (request.Method == "POST" &&
            string.Equals(path, "/minecraftservices/player/certificates", StringComparison.OrdinalIgnoreCase))
        {
            _lifecycle.SendOnce("PlayerCertificates", "第三方会话聊天签名密钥已由 Jvm.NET Host 生成本地密钥对");
            return JsonResponse(CloneJsonObject(_playerCertificates));
        }

        if (request.Method == "GET" &&
            string.Equals(path, "/minecraftservices/player/attributes", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(CreatePlayerAttributes());
        }

        return TextResponse(404, "Not Found", "Unsupported third-party Minecraft Services endpoint.");
    }

    private async Task<HttpResponseData> ProxyThirdPartyAsync(
        HttpRequestData request,
        string path,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedAuthPath(path))
            return TextResponse(404, "Not Found", "Unsupported authentication endpoint.");

        string authRoot = _request.AuthServer!.Trim().TrimEnd('/');
        string target = authRoot + request.Target;
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? targetUri) ||
            targetUri.Scheme is not ("http" or "https"))
        {
            return TextResponse(502, "Bad Gateway", "Invalid authentication server URL.");
        }

        byte[] requestBody = IsJoinRequest(request.Method, path)
            ? CreateJoinRequestBody(request.Body)
            : request.Body;
        using HttpRequestMessage upstream = new(new HttpMethod(request.Method), targetUri);
        if (requestBody.Length > 0)
        {
            upstream.Content = new ByteArrayContent(requestBody);
            if (request.Headers.TryGetValue("Content-Type", out string? contentType) &&
                MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed))
            {
                upstream.Content.Headers.ContentType = parsed;
            }
        }
        upstream.Headers.TryAddWithoutValidation("Accept", request.Headers.TryGetValue("Accept", out string? accept)
            ? accept
            : "application/json");
        CopyRequestHeader(request, upstream, "Authorization");
        CopyRequestHeader(request, upstream, "Accept-Language");
        CopyRequestHeader(request, upstream, "User-Agent");

        using HttpResponseMessage response = await _httpClient.SendAsync(
                upstream,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        byte[] body = await ReadLimitedAsync(response.Content, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        string contentTypeValue = response.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";

        if (response.IsSuccessStatusCode && IsProfileTexturePath(path) && body.Length > 0)
        {
            try
            {
                body = SanitizeThirdPartyProfile(body);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or
                                           InvalidOperationException or CryptographicException)
            {
                // Keep the upstream body if sanitization fails; authlib already trusts signatures.
            }
        }

        PublishThirdPartyLifecycle(request.Method, path, response.StatusCode, body);
        return new HttpResponseData((int)response.StatusCode, response.ReasonPhrase ?? "Upstream", contentTypeValue, body);
    }

    private static bool IsProfileTexturePath(string path) =>
        path.StartsWith("/sessionserver/session/minecraft/profile/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, "/sessionserver/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase);

    private byte[] CreateJoinRequestBody(byte[] originalBody)
    {
        JsonObject join = originalBody.Length == 0
            ? new JsonObject()
            : JsonNode.Parse(originalBody)?.AsObject()
              ?? throw new InvalidDataException("第三方 join 请求不是 JSON 对象。");
        join["accessToken"] = _request.AccessToken;
        join["selectedProfile"] = NormalizeUuid(_request.PlayerUuid);
        return Encoding.UTF8.GetBytes(join.ToJsonString());
    }

    private static bool IsJoinRequest(string method, string path) =>
        method == "POST" &&
        string.Equals(path, "/sessionserver/session/minecraft/join", StringComparison.OrdinalIgnoreCase);

    private void PublishThirdPartyLifecycle(
        string method,
        string path,
        HttpStatusCode statusCode,
        byte[]? responseBody = null)
    {
        int status = (int)statusCode;
        if (method == "POST" &&
            string.Equals(path, "/sessionserver/session/minecraft/join", StringComparison.OrdinalIgnoreCase))
        {
            string stage = status is >= 200 and < 300 ? "ThirdPartyJoin" : "ThirdPartyJoinFailed";
            string detail = $"第三方服务器会话注册 HTTP {status}";
            if (status is < 200 or >= 300 && responseBody is { Length: > 0 })
            {
                string snippet = Encoding.UTF8.GetString(responseBody);
                if (snippet.Length > 240)
                    snippet = snippet[..240] + "…";
                detail += "；" + snippet;
            }

            _lifecycle.SendOnce(stage, detail);
            return;
        }

        if (path.StartsWith("/sessionserver/session/minecraft/profile/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/sessionserver/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            _lifecycle.SendOnce("ThirdPartyProfile", $"第三方档案服务已通过 PCL N 本机桥接；HTTP {status}");
        }
    }

    private static void CopyRequestHeader(HttpRequestData request, HttpRequestMessage upstream, string name)
    {
        if (request.Headers.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
            upstream.Headers.TryAddWithoutValidation(name, value);
    }

    private byte[] SanitizeThirdPartyProfile(byte[] body)
    {
        JsonObject profile = JsonNode.Parse(body)?.AsObject()
                             ?? throw new InvalidDataException("第三方档案响应不是 JSON 对象。");
        if (profile["properties"] is not JsonArray properties)
            return Encoding.UTF8.GetBytes(profile.ToJsonString());

        foreach (JsonNode? node in properties.ToArray())
        {
            if (node is not JsonObject property ||
                !string.Equals(property["name"]?.GetValue<string>(), "textures", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? value = property["value"]?.GetValue<string>();
            string? signature = property["signature"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value) || !ValidateTextureProperty(value, signature))
            {
                properties.Remove(node);
                continue;
            }

            JsonObject payload = DecodeTexturePayload(value);
            if (!RewriteTextureUrls(payload))
            {
                properties.Remove(node);
                continue;
            }

            property["value"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            // The rewrite necessarily invalidates the upstream signature. authlib is patched to
            // accept this marker only because validation happened immediately above.
            property["signature"] = "AA==";
        }

        return Encoding.UTF8.GetBytes(profile.ToJsonString());
    }

    private bool ValidateTextureProperty(string value, string? signature)
    {
        if (_metadata.TryVerify(value, signature))
            return true;

        try
        {
            JsonObject payload = DecodeTexturePayload(value);
            return AllTextureUrlsAllowed(payload, _metadata.SkinDomains);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool RewriteTextureUrls(JsonObject payload)
    {
        if (payload["textures"] is not JsonObject textures)
            return false;

        bool rewritten = false;
        foreach ((string _, JsonNode? value) in textures.ToArray())
        {
            if (value is not JsonObject texture || texture["url"]?.GetValue<string>() is not { } url ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            string token = RegisterTextureSource(uri.AbsoluteUri);
            texture["url"] = BaseUrl + "/pcl/texture/" + token;
            rewritten = true;
        }
        return rewritten;
    }

    private async Task<HttpResponseData> ServeTextureAsync(string token, CancellationToken cancellationToken)
    {
        if (!_textureSources.TryGetValue(token, out string? source))
            return TextResponse(404, "Not Found", "Unknown texture token.");

        try
        {
            byte[] bytes = await LoadTextureBytesAsync(source, cancellationToken).ConfigureAwait(false);
            ValidatePng(bytes);
            _lifecycle.SendOnce("SkinReady", "皮肤纹理已通过 Jvm.NET Host 提供给游戏");
            return new HttpResponseData(200, "OK", "image/png", bytes, CacheControl: "private, max-age=300");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or
                                       FormatException or JsonException or UnauthorizedAccessException)
        {
            return TextResponse(502, "Bad Gateway", "Unable to load skin: " + ex.Message);
        }
    }

    private async Task<byte[]> LoadTextureBytesAsync(string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            FileInfo info = new(source);
            if (info.Length > MaxTextureBytes)
                throw new InvalidDataException("皮肤文件超过 4 MiB 限制。");
            return await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        }

        string target = source;
        if (source.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
        {
            string uuid = NormalizeUuid(source[5..]);
            target = "https://sessionserver.mojang.com/session/minecraft/profile/" + uuid;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("离线皮肤来源不是文件或 HTTP(S) 地址。");

        using HttpResponseMessage response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await ReadLimitedAsync(response.Content, MaxTextureBytes, cancellationToken).ConfigureAwait(false);
        if (LooksLikePng(bytes))
            return bytes;

        string textureUrl = ExtractSkinUrlFromProfile(bytes)
                            ?? throw new InvalidDataException("皮肤来源既不是 PNG，也不包含有效的 textures 档案。");
        if (!Uri.TryCreate(textureUrl, UriKind.Absolute, out Uri? textureUri) || textureUri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("档案中的皮肤地址无效。");
        using HttpResponseMessage textureResponse = await _httpClient.GetAsync(
                textureUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        textureResponse.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(textureResponse.Content, MaxTextureBytes, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject CreateOfflineProfile()
    {
        JsonArray properties = new();
        if (_offlineTextureToken is not null)
        {
            JsonObject skin = new()
            {
                ["url"] = BaseUrl + "/pcl/texture/" + _offlineTextureToken
            };
            if (_request.OfflineSkinSlim)
                skin["metadata"] = new JsonObject { ["model"] = "slim" };

            JsonObject payload = new()
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["profileId"] = NormalizeUuid(_request.PlayerUuid),
                ["profileName"] = _request.PlayerName,
                ["textures"] = new JsonObject { ["SKIN"] = skin }
            };
            properties.Add((JsonNode)new JsonObject
            {
                ["name"] = "textures",
                ["value"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString())),
                ["signature"] = "AA=="
            });
        }

        return new JsonObject
        {
            ["id"] = NormalizeUuid(_request.PlayerUuid),
            ["name"] = _request.PlayerName,
            ["properties"] = properties
        };
    }

    private JsonObject CreateOfflineServicesProfile()
    {
        JsonObject response = CreateNameAndId();
        JsonArray skins = new();
        if (_offlineTextureToken is not null)
        {
            skins.Add((JsonNode)CreateServicesSkinEntry(
                BaseUrl + "/pcl/texture/" + _offlineTextureToken,
                _request.OfflineSkinSlim ? "SLIM" : "CLASSIC"));
        }

        response["skins"] = skins;
        response["capes"] = new JsonArray();
        return response;
    }

    private async Task<JsonObject> GetOrCreateThirdPartyServicesProfileAsync(CancellationToken cancellationToken)
    {
        if (_thirdPartyServicesProfile is not null)
            return _thirdPartyServicesProfile;

        await _thirdPartyProfileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_thirdPartyServicesProfile is not null)
                return _thirdPartyServicesProfile;

            JsonObject profile = CreateNameAndId();
            JsonArray skins = new();
            JsonArray capes = new();

            try
            {
                using CancellationTokenSource skinTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                skinTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                JsonObject? textures = await FetchThirdPartyTexturesPayloadAsync(skinTimeout.Token)
                    .ConfigureAwait(false);
                if (textures is not null)
                    PopulateServicesTextures(textures, skins, capes);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                                           JsonException or FormatException or OperationCanceledException or
                                           TaskCanceledException or UnauthorizedAccessException or
                                           SocketException)
            {
                // Skin hydration is best-effort; chat certificates still work without it.
            }

            profile["skins"] = skins;
            profile["capes"] = capes;
            _thirdPartyServicesProfile = profile;
            if (skins.Count > 0)
                _lifecycle.SendOnce("SkinReady", "第三方皮肤已映射到本机会话桥");
            return profile;
        }
        finally
        {
            _thirdPartyProfileGate.Release();
        }
    }

    private async Task<JsonObject?> FetchThirdPartyTexturesPayloadAsync(CancellationToken cancellationToken)
    {
        string authRoot = _request.AuthServer!.Trim().TrimEnd('/');
        string uuid = NormalizeUuid(_request.PlayerUuid);
        string target = authRoot + "/sessionserver/session/minecraft/profile/" + uuid;
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? targetUri) ||
            targetUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        using HttpRequestMessage upstream = new(HttpMethod.Get, targetUri);
        upstream.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(_request.AccessToken) && _request.AccessToken != "0")
            upstream.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _request.AccessToken);

        using HttpResponseMessage response = await _httpClient.SendAsync(
                upstream,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        byte[] body = await ReadLimitedAsync(response.Content, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
            return null;

        JsonObject profile = JsonNode.Parse(body)?.AsObject()
                             ?? throw new InvalidDataException("第三方档案响应不是 JSON 对象。");
        if (profile["properties"] is not JsonArray properties)
            return null;

        foreach (JsonNode? node in properties)
        {
            if (node is not JsonObject property ||
                !string.Equals(property["name"]?.GetValue<string>(), "textures", StringComparison.OrdinalIgnoreCase) ||
                property["value"]?.GetValue<string>() is not { } value ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                // Local services profile only needs a decodable texture payload; URLs are proxied
                // through the bridge. Signature/domain checks still apply when rewriting for authlib.
                JsonObject payload = DecodeTexturePayload(value);
                if (payload["textures"] is JsonObject textures)
                    return textures;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException or
                                           InvalidOperationException)
            {
            }
        }

        return null;
    }

    private void PopulateServicesTextures(JsonObject textures, JsonArray skins, JsonArray capes)
    {
        if (textures["SKIN"] is JsonObject skin &&
            skin["url"]?.GetValue<string>() is { } skinUrl &&
            Uri.TryCreate(skinUrl, UriKind.Absolute, out Uri? skinUri) &&
            skinUri.Scheme is "http" or "https")
        {
            string token = RegisterTextureSource(skinUri.AbsoluteUri);
            string? model = skin["metadata"]?["model"]?.GetValue<string>();
            bool slim = string.Equals(model, "slim", StringComparison.OrdinalIgnoreCase);
            skins.Add((JsonNode)CreateServicesSkinEntry(
                BaseUrl + "/pcl/texture/" + token,
                slim ? "SLIM" : "CLASSIC"));
        }

        if (textures["CAPE"] is JsonObject cape &&
            cape["url"]?.GetValue<string>() is { } capeUrl &&
            Uri.TryCreate(capeUrl, UriKind.Absolute, out Uri? capeUri) &&
            capeUri.Scheme is "http" or "https")
        {
            string token = RegisterTextureSource(capeUri.AbsoluteUri);
            capes.Add((JsonNode)new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["state"] = "ACTIVE",
                ["url"] = BaseUrl + "/pcl/texture/" + token,
                ["alias"] = "Cape"
            });
        }
    }

    private static JsonObject CreateServicesSkinEntry(string url, string variant) => new()
    {
        ["id"] = Guid.NewGuid().ToString("N"),
        ["state"] = "ACTIVE",
        ["url"] = url,
        ["variant"] = variant,
        ["textureKey"] = Guid.NewGuid().ToString("N")
    };

    private static JsonObject CreatePlayerCertificates()
    {
        using RSA rsa = RSA.Create(2048);
        string privateKey = EnsurePemTrailingNewline(rsa.ExportRSAPrivateKeyPem());
        string publicKey = EnsurePemTrailingNewline(rsa.ExportRSAPublicKeyPem());

        // Mojang signs these with the services key. For Host third-party/offline we only need a
        // parseable key pair so the client can load chat signing; signature trust is relaxed in authlib.
        byte[] publicKeyDer = rsa.ExportSubjectPublicKeyInfo();
        string signature = Convert.ToBase64String(
            rsa.SignData(publicKeyDer, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        string signatureV2 = Convert.ToBase64String(
            rsa.SignData(publicKeyDer, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["keyPair"] = new JsonObject
            {
                ["privateKey"] = privateKey,
                ["publicKey"] = publicKey
            },
            ["publicKeySignature"] = signature,
            ["publicKeySignatureV2"] = signatureV2,
            ["expiresAt"] = now.AddDays(2).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture),
            ["refreshedAfter"] = now.AddHours(12).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static JsonObject CreatePlayerAttributes() => new()
    {
        ["privileges"] = new JsonObject
        {
            ["onlineChat"] = new JsonObject { ["enabled"] = true },
            ["multiplayerServer"] = new JsonObject { ["enabled"] = true },
            ["multiplayerRealms"] = new JsonObject { ["enabled"] = false },
            ["telemetry"] = new JsonObject { ["enabled"] = false }
        },
        ["profanityFilterPreferences"] = new JsonObject { ["profanityFilterOn"] = false },
        ["banStatus"] = new JsonObject { ["bannedScopes"] = new JsonObject() }
    };

    private static string EnsurePemTrailingNewline(string pem) =>
        pem.EndsWith('\n') ? pem : pem + "\n";

    private static JsonObject CloneJsonObject(JsonObject source) =>
        JsonNode.Parse(source.ToJsonString())?.AsObject()
        ?? throw new InvalidOperationException("Failed to clone JSON object.");

    private JsonObject CreateNameAndId() => new()
    {
        ["id"] = NormalizeUuid(_request.PlayerUuid),
        ["name"] = _request.PlayerName
    };

    private JsonObject CreateDiscoveryDocument()
    {
        JsonObject session = CreateEndpoints(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["join"] = BaseUrl + "/sessionserver/session/minecraft/join",
            ["verify"] = BaseUrl + "/sessionserver/session/minecraft/hasJoined",
            ["getProfileById"] = BaseUrl + "/sessionserver/session/minecraft/profile/{profileId}"
        });
        JsonObject profiles = CreateEndpoints(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["getByName"] = BaseUrl + "/api/users/profiles/minecraft/{name}",
            ["getManyByName"] = BaseUrl + "/api/profiles/minecraft",
            ["getTexture"] = BaseUrl + "/pcl/texture/{textureId}"
        });
        JsonObject player = CreateEndpoints(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["getAttributes"] = BaseUrl + "/minecraftservices/player/attributes",
            ["getCertificates"] = BaseUrl + "/minecraftservices/player/certificates",
            ["getBlocklist"] = BaseUrl + "/minecraftservices/privacy/blocklist",
            ["sendReport"] = BaseUrl + "/minecraftservices/player/report",
            ["getFriends"] = BaseUrl + "/minecraftservices/player/friends",
            ["updateAttributes"] = BaseUrl + "/minecraftservices/player/attributes",
            ["updateFriends"] = BaseUrl + "/minecraftservices/player/friends",
            ["updatePresence"] = BaseUrl + "/minecraftservices/player/presence"
        });

        return new JsonObject
        {
            ["environment"] = "prod",
            ["product"] = "minecraft",
            ["discovery"] = new JsonObject
            {
                ["product"] = "minecraft",
                ["authentication"] = CreateEndpoints(new Dictionary<string, string>
                {
                    ["getPublicKeys"] = BaseUrl + "/pcl/publickeys"
                }),
                ["session"] = session,
                ["player"] = player,
                ["profiles"] = profiles,
                ["telemetry"] = CreateEndpoints(new Dictionary<string, string>())
            }
        };
    }

    private JsonObject CreateEndpoints(IReadOnlyDictionary<string, string> values)
    {
        JsonObject endpoints = new();
        foreach ((string name, string uri) in values)
        {
            endpoints[name] = new JsonObject
            {
                ["uri"] = uri,
                ["validUris"] = new JsonArray(BaseUrl)
            };
        }
        return new JsonObject { ["endpoints"] = endpoints };
    }

    private static bool IsAllowedAuthPath(string path) =>
        path.StartsWith("/sessionserver/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/authserver/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/minecraftservices/", StringComparison.OrdinalIgnoreCase);

    private string RegisterTextureSource(string source)
    {
        string token = Guid.NewGuid().ToString("N");
        _textureSources[token] = source;
        return token;
    }

    private static bool IsUsableSkinSource(string? source) =>
        !string.IsNullOrWhiteSpace(source) &&
        !source.StartsWith("avares://", StringComparison.OrdinalIgnoreCase);

    private static bool AllTextureUrlsAllowed(JsonObject payload, IReadOnlyList<string> allowedDomains)
    {
        if (allowedDomains.Count == 0 || payload["textures"] is not JsonObject textures)
            return false;
        bool found = false;
        foreach ((string _, JsonNode? value) in textures)
        {
            string? url = value?["url"]?.GetValue<string>();
            if (url is null)
                continue;
            found = true;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") ||
                !allowedDomains.Any(domain => HostMatchesDomain(uri.Host, domain)))
            {
                return false;
            }
        }
        return found;
    }

    private static bool HostMatchesDomain(string host, string domain)
    {
        string normalized = domain.Trim().TrimStart('*', '.').TrimEnd('.');
        return normalized.Length > 0 &&
               (string.Equals(host, normalized, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject DecodeTexturePayload(string value)
    {
        byte[] decoded = Convert.FromBase64String(value);
        return JsonNode.Parse(decoded)?.AsObject()
               ?? throw new InvalidDataException("textures 属性不是 JSON 对象。");
    }

    private static string? ExtractSkinUrlFromProfile(byte[] bytes)
    {
        JsonObject profile = JsonNode.Parse(bytes)?.AsObject()
                             ?? throw new InvalidDataException("档案响应不是 JSON 对象。");
        if (profile["properties"] is not JsonArray properties)
            return null;
        foreach (JsonNode? node in properties)
        {
            if (node is not JsonObject property ||
                !string.Equals(property["name"]?.GetValue<string>(), "textures", StringComparison.OrdinalIgnoreCase) ||
                property["value"]?.GetValue<string>() is not { } value)
            {
                continue;
            }
            JsonObject payload = DecodeTexturePayload(value);
            return payload["textures"]?["SKIN"]?["url"]?.GetValue<string>();
        }
        return null;
    }

    private static void ValidatePng(byte[] bytes)
    {
        if (!LooksLikePng(bytes) || bytes.Length < 24)
            throw new InvalidDataException("皮肤不是有效的 PNG 文件。");
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width < 64 || width > 1024 || (height != width && height * 2 != width))
            throw new InvalidDataException($"不支持的皮肤尺寸：{width}x{height}。");
    }

    private static bool LooksLikePng(byte[] bytes) => bytes.Length >= 8 &&
        bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
        bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    private static string NormalizeUuid(string uuid)
    {
        string normalized = new(uuid.Where(static c => char.IsAsciiHexDigit(c)).ToArray());
        if (normalized.Length != 32)
            return Guid.Empty.ToString("N");
        return normalized.ToLowerInvariant();
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maxBytes)
            throw new InvalidDataException("HTTP 响应超过允许大小。");
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream destination = new();
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException("HTTP 响应超过允许大小。");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static async Task<HttpRequestData> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using MemoryStream received = new();
        byte[] buffer = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("HTTP client disconnected before sending headers.");
            received.Write(buffer, 0, read);
            if (received.Length > MaxHeaderBytes)
                throw new InvalidDataException("HTTP request headers are too large.");
            headerEnd = FindHeaderEnd(received.GetBuffer(), checked((int)received.Length));
        }

        byte[] all = received.ToArray();
        string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
            throw new InvalidDataException("Invalid HTTP request line.");

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int separator = lines[i].IndexOf(':');
            if (separator <= 0)
                continue;
            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? lengthText) &&
            (!int.TryParse(lengthText, out contentLength) || contentLength < 0 || contentLength > MaxRequestBytes))
        {
            throw new InvalidDataException("Invalid or oversized HTTP request body.");
        }

        int bodyOffset = headerEnd + 4;
        byte[] body = new byte[contentLength];
        int bufferedBody = Math.Min(contentLength, all.Length - bodyOffset);
        if (bufferedBody > 0)
            Buffer.BlockCopy(all, bodyOffset, body, 0, bufferedBody);
        int position = bufferedBody;
        while (position < contentLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(position, contentLength - position), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new IOException("HTTP client disconnected while sending its body.");
            position += read;
        }

        return new HttpRequestData(requestLine[0].ToUpperInvariant(), requestLine[1], headers, body);
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpResponseData response,
        CancellationToken cancellationToken)
    {
        string safeReason = response.Reason.Replace('\r', ' ').Replace('\n', ' ');
        string headers =
            $"HTTP/1.1 {response.StatusCode} {safeReason}\r\n" +
            $"Content-Type: {response.ContentType}\r\n" +
            $"Content-Length: {response.Body.Length}\r\n" +
            $"Cache-Control: {response.CacheControl}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HttpResponseData JsonResponse(JsonNode json) =>
        new(200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.ToJsonString()));

    private static HttpResponseData TextResponse(int statusCode, string reason, string text) =>
        new(statusCode, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static HttpResponseData EmptyResponse(int statusCode, string reason) =>
        new(statusCode, reason, "text/plain; charset=utf-8", []);

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        _httpClient.Dispose();
        _metadata.Dispose();
        _thirdPartyProfileGate.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Delegates to the system/default proxy for remote hosts, but always reaches loopback directly.
    /// </summary>
    private sealed class LoopbackBypassProxy(IWebProxy inner) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => inner.Credentials;
            set => inner.Credentials = value;
        }

        public Uri? GetProxy(Uri destination) =>
            IsLoopbackHost(destination) ? destination : inner.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            IsLoopbackHost(host) || inner.IsBypassed(host);

        private static bool IsLoopbackHost(Uri uri) =>
            uri.IsLoopback ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "[::1]", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record HttpRequestData(
        string Method,
        string Target,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    private sealed record HttpResponseData(
        int StatusCode,
        string Reason,
        string ContentType,
        byte[] Body,
        string CacheControl = "no-store");

    private sealed class AuthServerMetadata : IDisposable
    {
        private AuthServerMetadata(IReadOnlyList<string> skinDomains, RSA? signatureKey)
        {
            SkinDomains = skinDomains;
            SignatureKey = signatureKey;
        }

        public IReadOnlyList<string> SkinDomains { get; }

        private RSA? SignatureKey { get; }

        public static AuthServerMetadata Parse(string? metadata)
        {
            if (string.IsNullOrWhiteSpace(metadata))
                return new AuthServerMetadata([], null);
            try
            {
                JsonObject root = JsonNode.Parse(metadata)?.AsObject()
                                  ?? throw new JsonException("Metadata is not an object.");
                string[] domains = root["skinDomains"] is JsonArray array
                    ? array.Select(static node => node?.GetValue<string>() ?? string.Empty)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .ToArray()
                    : [];
                string? publicKey = root["signaturePublickey"]?.GetValue<string>()
                                    ?? root["signaturePublicKey"]?.GetValue<string>();
                return new AuthServerMetadata(domains, TryImportKey(publicKey));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
            {
                return new AuthServerMetadata([], null);
            }
        }

        public bool TryVerify(string value, string? signature)
        {
            if (SignatureKey is null || string.IsNullOrWhiteSpace(signature))
                return false;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(value);
                byte[] signatureBytes = Convert.FromBase64String(signature);
                return SignatureKey.VerifyData(data, signatureBytes, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1) ||
                       SignatureKey.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                return false;
            }
        }

        private static RSA? TryImportKey(string? publicKey)
        {
            if (string.IsNullOrWhiteSpace(publicKey))
                return null;
            RSA rsa = RSA.Create();
            try
            {
                if (publicKey.Contains("BEGIN", StringComparison.Ordinal))
                {
                    rsa.ImportFromPem(publicKey);
                }
                else
                {
                    rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                }
                return rsa;
            }
            catch
            {
                rsa.Dispose();
                return null;
            }
        }

        public void Dispose() => SignatureKey?.Dispose();
    }
}
