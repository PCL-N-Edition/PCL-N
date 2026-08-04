// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text.Json;
using PCL.Application.Accounts;

namespace PCL.Application.Test;

[TestClass]
public sealed class LittleSkinOAuthServiceTests
{
    [TestMethod]
    public void CreateAuthorizationRequest_UsesDocumentedScopesAndState()
    {
        LittleSkinOAuthService service = new(new HttpClient());
        LittleSkinOAuthConfiguration configuration = new(
            "client-id",
            "client-secret",
            new Uri("http://127.0.0.1:17342/oauth/littleskin/callback"));

        LittleSkinAuthorizationRequest request = service.CreateAuthorizationRequest(
            configuration,
            "state-token");

        string url = request.AuthorizationUri.AbsoluteUri;
        StringAssert.Contains(url, "response_type=code");
        StringAssert.Contains(url, "client_id=client-id");
        StringAssert.Contains(url, "state=state-token");
        StringAssert.Contains(url, "openid");
        StringAssert.Contains(url, "offline_access");
        StringAssert.Contains(url, "Yggdrasil.PlayerProfiles.Read");
        StringAssert.Contains(url, "Yggdrasil.MinecraftToken.Create");
        Assert.DoesNotContain("Yggdrasil.PlayerProfiles.Select", url);
        Assert.DoesNotContain("Yggdrasil.Server.Join", url);
    }

    [TestMethod]
    public async Task DeviceCodeFlow_PollsAndCreatesMinecraftSessionViaOfficialApis()
    {
        int tokenPolls = 0;
        using HttpClient client = new(new DelegateHandler(async request =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;

            if (host == "open.littleskin.cn" && path == "/oauth/device_code")
            {
                string form = await request.Content!.ReadAsStringAsync();
                StringAssert.Contains(form, "client_id=client-id");
                StringAssert.Contains(form, "Yggdrasil.PlayerProfiles.Read");
                StringAssert.Contains(form, "Yggdrasil.MinecraftToken.Create");
                return Json(
                    """
                    {
                      "user_code": "ABCD-EFGH",
                      "device_code": "device-xyz",
                      "verification_uri": "https://open.littleskin.cn/oauth/link",
                      "verification_uri_complete": "https://open.littleskin.cn/oauth/link?user_code=ABCD-EFGH",
                      "expires_in": 300,
                      "interval": 1
                    }
                    """);
            }

            if (host == "open.littleskin.cn" && path == "/oauth/token")
            {
                tokenPolls++;
                string form = await request.Content!.ReadAsStringAsync();
                if (form.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", StringComparison.Ordinal) ||
                    form.Contains("device_code", StringComparison.Ordinal))
                {
                    if (tokenPolls < 2)
                    {
                        return new HttpResponseMessage(HttpStatusCode.BadRequest)
                        {
                            Content = new StringContent(
                                """{"error":"authorization_pending","error_description":"pending"}""")
                        };
                    }

                    return Json(
                        """
                        {
                          "token_type": "Bearer",
                          "expires_in": 259200,
                          "access_token": "oauth-access",
                          "refresh_token": "oauth-refresh",
                          "id_token": "header.payload.sig"
                        }
                        """);
                }
            }

            if (request.Headers.TryGetValues("Authorization", out IEnumerable<string>? authValues))
                StringAssert.StartsWith(authValues.First(), "Bearer ");
            if (path.EndsWith("/session/minecraft/profile", StringComparison.Ordinal))
            {
                return Json(
                    """
                    [
                      {"id":"0123456789abcdef0123456789abcdef","name":"Alice","properties":[]}
                    ]
                    """);
            }

            if (path.EndsWith("/authserver/oauth", StringComparison.Ordinal))
            {
                string body = await request.Content!.ReadAsStringAsync();
                using JsonDocument document = JsonDocument.Parse(body);
                Assert.AreEqual(
                    "0123456789abcdef0123456789abcdef",
                    document.RootElement.GetProperty("uuid").GetString());
                return Json(
                    """
                    {
                      "accessToken":"minecraft-token",
                      "clientToken":"minecraft-client-token",
                      "selectedProfile":{
                        "id":"0123456789abcdef0123456789abcdef",
                        "name":"Alice"
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        LittleSkinOAuthService service = new(client);
        LittleSkinOAuthConfiguration configuration = new(
            "client-id",
            string.Empty,
            new Uri(LittleSkinOAuthService.DeviceFlowRedirectUri));

        LittleSkinDeviceCodeInfo device = await service.RequestDeviceCodeAsync(configuration);
        Assert.AreEqual("ABCD-EFGH", device.UserCode);

        LittleSkinOAuthTokens tokens = await service.WaitForDeviceAuthorizationAsync(
            configuration,
            device);
        IReadOnlyList<LittleSkinProfile> profiles = await service.GetProfilesAsync(tokens.AccessToken);
        LittleSkinMinecraftSession session = await service.CreateMinecraftSessionAsync(
            tokens.AccessToken,
            profiles[0].Uuid);

        Assert.AreEqual("oauth-access", tokens.AccessToken);
        Assert.AreEqual("oauth-refresh", tokens.RefreshToken);
        Assert.HasCount(1, profiles);
        Assert.AreEqual("Alice", session.Username);
        Assert.AreEqual("minecraft-token", session.AccessToken);
        Assert.IsGreaterThanOrEqualTo(2, tokenPolls);
    }

    [TestMethod]
    public async Task AuthorizationCodeFlow_ExchangesCodeAndCreatesMinecraftSession()
    {
        using HttpClient client = new(new DelegateHandler(async request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/oauth/token")
            {
                string form = await request.Content!.ReadAsStringAsync();
                StringAssert.Contains(form, "grant_type=authorization_code");
                StringAssert.Contains(form, "client_secret=client-secret");
                return Json(
                    """
                    {
                      "access_token": "oauth-access",
                      "refresh_token": "oauth-refresh",
                      "expires_in": 259200
                    }
                    """);
            }

            if (path.EndsWith("/session/minecraft/profile", StringComparison.Ordinal))
            {
                return Json(
                    """
                    [
                      {"id":"0123456789abcdef0123456789abcdef","name":"Alice","properties":[]}
                    ]
                    """);
            }

            if (path.EndsWith("/authserver/oauth", StringComparison.Ordinal))
            {
                return Json(
                    """
                    {
                      "accessToken":"minecraft-token",
                      "clientToken":"minecraft-client-token",
                      "selectedProfile":{
                        "id":"0123456789abcdef0123456789abcdef",
                        "name":"Alice"
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        LittleSkinOAuthService service = new(client);
        LittleSkinOAuthConfiguration configuration = new(
            "client-id",
            "client-secret",
            new Uri("http://127.0.0.1:17342/oauth/littleskin/callback"));

        LittleSkinOAuthTokens tokens = await service.ExchangeAuthorizationCodeAsync(
            configuration,
            "authorization-code");
        IReadOnlyList<LittleSkinProfile> profiles = await service.GetProfilesAsync(tokens.AccessToken);
        LittleSkinMinecraftSession session = await service.CreateMinecraftSessionAsync(
            tokens.AccessToken,
            profiles[0].Uuid);

        Assert.AreEqual("Alice", session.Username);
        Assert.AreEqual("minecraft-token", session.AccessToken);
    }

    [TestMethod]
    public async Task RequestDeviceCode_InvalidClient_ThrowsFriendlyMessage()
    {
        using HttpClient client = new(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":"invalid_client","error_description":"Client authentication failed"}""")
            })));
        LittleSkinOAuthService service = new(client);
        LittleSkinOAuthConfiguration configuration = new(
            "client-id",
            string.Empty,
            new Uri(LittleSkinOAuthService.DeviceFlowRedirectUri));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RequestDeviceCodeAsync(configuration));

        StringAssert.Contains(ex.Message, "申请暂未通过");
        StringAssert.Contains(ex.Message, "第三方登录");
        StringAssert.Contains(ex.Message, "invalid_client");
    }

    [TestMethod]
    public async Task RefreshOAuthToken_PrefersOpenEndpointForDeviceTokens()
    {
        using HttpClient client = new(new DelegateHandler(async request =>
        {
            Assert.AreEqual("open.littleskin.cn", request.RequestUri!.Host);
            Assert.AreEqual("/oauth/token", request.RequestUri.AbsolutePath);
            string form = await request.Content!.ReadAsStringAsync();
            StringAssert.Contains(form, "grant_type=refresh_token");
            StringAssert.Contains(form, "client_id=client-id");
            StringAssert.Contains(form, "refresh_token=existing-refresh");
            Assert.DoesNotContain("client_secret", form);
            return Json(
                """
                {
                  "access_token": "refreshed-access",
                  "refresh_token": "new-refresh",
                  "expires_in": 3600
                }
                """);
        }));
        LittleSkinOAuthService service = new(client);
        LittleSkinOAuthConfiguration configuration = new(
            "client-id",
            string.Empty,
            new Uri(LittleSkinOAuthService.DeviceFlowRedirectUri));

        LittleSkinOAuthTokens tokens = await service.RefreshOAuthTokenAsync(
            configuration,
            "existing-refresh");

        Assert.AreEqual("refreshed-access", tokens.AccessToken);
        Assert.AreEqual("new-refresh", tokens.RefreshToken);
    }

    [TestMethod]
    public async Task ClosetAndTextureApi_HandlesSkinsAndCapes()
    {
        int applied = 0;
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        using HttpClient client = new(new DelegateHandler(async request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/closet")
            {
                string category = ParseQuery(request.RequestUri.Query)["category"];
                string type = category == "cape" ? "cape" : "alex";
                return Json(
                    $$"""
                    {
                      "last_page":1,
                      "data":[{
                        "tid":42,
                        "name":"Original",
                        "type":"{{type}}",
                        "hash":"{{hash}}",
                        "pivot":{"item_name":"Favorite"}
                      }]
                    }
                    """);
            }

            if (path == "/api/players")
            {
                return Json(
                    """
                    [{"pid":7,"name":"Alice","tid_skin":1,"tid_cape":2}]
                    """);
            }

            if (path == "/api/players/7/textures")
            {
                string form = await request.Content!.ReadAsStringAsync();
                StringAssert.Contains(form, "cape=42");
                applied++;
                return Json("""{"code":0,"message":"ok"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        LittleSkinOAuthService service = new(client);

        IReadOnlyList<LittleSkinClosetItem> skins = await service.GetClosetItemsAsync(
            "access",
            LittleSkinTextureKind.Skin);
        IReadOnlyList<LittleSkinClosetItem> capes = await service.GetClosetItemsAsync(
            "access",
            LittleSkinTextureKind.Cape);
        IReadOnlyList<LittleSkinPlayer> players = await service.GetPlayersAsync("access");
        await service.ApplyTextureAsync(
            "access",
            players[0].PlayerId,
            capes[0].TextureId,
            LittleSkinTextureKind.Cape);

        Assert.HasCount(1, skins);
        Assert.HasCount(1, capes);
        Assert.AreEqual(LittleSkinTextureKind.Cape, capes[0].Kind);
        Assert.AreEqual("Favorite", capes[0].Name);
        Assert.AreEqual("https://littleskin.cn/textures/" + hash, capes[0].TextureAddress);
        Assert.AreEqual(1, applied);
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part.ElementAtOrDefault(1) ?? string.Empty),
                StringComparer.Ordinal);

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handle(request);
    }
}
