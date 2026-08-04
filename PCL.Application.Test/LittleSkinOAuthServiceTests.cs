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
    public void CreateAuthorizationRequest_UsesAuthorizationCodeScopesAndState()
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
        StringAssert.Contains(url, "Player.ReadWrite");
        StringAssert.Contains(url, "Closet.Read");
        StringAssert.Contains(url, "Yggdrasil.PlayerProfiles.Read");
        StringAssert.Contains(url, "Yggdrasil.MinecraftToken.Create");
        Assert.DoesNotContain("Yggdrasil.Server.Join", url);
        Assert.DoesNotContain("Closet.ReadWrite", url);
        Assert.DoesNotContain("Yggdrasil.PlayerProfiles.Select", url);
    }

    [TestMethod]
    public async Task AuthorizationCodeFlow_ExchangesCodeAndCreatesMinecraftSession()
    {
        Queue<HttpRequestMessage> requests = new();
        using HttpClient client = new(new DelegateHandler(async request =>
        {
            requests.Enqueue(request);
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

            Assert.AreEqual(
                "Bearer",
                request.Headers.Authorization?.Scheme);
            Assert.AreEqual(
                "oauth-access",
                request.Headers.Authorization?.Parameter);
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
            "client-secret",
            new Uri("http://127.0.0.1:17342/oauth/littleskin/callback"));

        LittleSkinOAuthTokens tokens = await service.ExchangeAuthorizationCodeAsync(
            configuration,
            "authorization-code");
        IReadOnlyList<LittleSkinProfile> profiles = await service.GetProfilesAsync(
            tokens.AccessToken);
        LittleSkinMinecraftSession session = await service.CreateMinecraftSessionAsync(
            tokens.AccessToken,
            profiles[0].Uuid);

        Assert.AreEqual("oauth-access", tokens.AccessToken);
        Assert.AreEqual("oauth-refresh", tokens.RefreshToken);
        Assert.HasCount(1, profiles);
        Assert.AreEqual("Alice", session.Username);
        Assert.AreEqual("minecraft-token", session.AccessToken);
        Assert.AreEqual("minecraft-client-token", session.ClientToken);
        Assert.HasCount(3, requests);
    }

    [TestMethod]
    public async Task GetProfilesAsync_FallsBackToPlayersAndPublicUuidLookup()
    {
        using HttpClient client = new(new DelegateHandler(async request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/session/minecraft/profile", StringComparison.Ordinal) &&
                !path.Contains("/profile/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """
                        {"error":"ForbiddenOperationException","errorMessage":"Invalid access token, please re-login."}
                        """)
                };
            }

            if (path == "/api/players")
            {
                return Json(
                    """
                    [{"pid":7,"name":"Alice","tid_skin":1,"tid_cape":0}]
                    """);
            }

            if (path.Contains("/profiles/minecraft/Alice", StringComparison.Ordinal) ||
                path.Contains("/lookup/name/Alice", StringComparison.Ordinal))
            {
                return Json(
                    """
                    {"id":"0123456789abcdef0123456789abcdef","name":"Alice"}
                    """);
            }

            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        LittleSkinOAuthService service = new(client);

        IReadOnlyList<LittleSkinProfile> profiles = await service.GetProfilesAsync("oauth-access");

        Assert.HasCount(1, profiles);
        Assert.AreEqual("Alice", profiles[0].Username);
        Assert.AreEqual("0123456789abcdef0123456789abcdef", profiles[0].Uuid);
    }

    [TestMethod]
    public async Task RefreshOAuthToken_UsesRefreshGrantAndKeepsExistingRefreshToken()
    {
        using HttpClient client = new(new DelegateHandler(async request =>
        {
            Assert.AreEqual("/oauth/token", request.RequestUri!.AbsolutePath);
            string form = await request.Content!.ReadAsStringAsync();
            StringAssert.Contains(form, "grant_type=refresh_token");
            StringAssert.Contains(form, "client_id=client-id");
            StringAssert.Contains(form, "client_secret=client-secret");
            StringAssert.Contains(form, "refresh_token=existing-refresh");
            return Json(
                """
                {
                  "access_token": "refreshed-access",
                  "expires_in": 3600
                }
                """);
        }));
        LittleSkinOAuthService service = new(client);
        LittleSkinOAuthConfiguration configuration = new(
            "client-id",
            "client-secret",
            new Uri("http://127.0.0.1:17342/oauth/littleskin/callback"));

        LittleSkinOAuthTokens tokens = await service.RefreshOAuthTokenAsync(
            configuration,
            "existing-refresh");

        Assert.AreEqual("refreshed-access", tokens.AccessToken);
        Assert.AreEqual("existing-refresh", tokens.RefreshToken);
        Assert.IsTrue(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(55));
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
