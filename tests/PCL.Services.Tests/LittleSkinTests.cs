using System.Net;
using System.Text;
using PCL.Services.Accounts;

namespace PCL.Services.Tests;

// XSR-514: LittleSkin OAuth (device flow, token refresh, profiles, Minecraft session,
// closet, texture upload) plus the Microsoft skin/cape services — all fixture-driven.
internal static partial class Program
{
    private static LittleSkinOAuthConfiguration LittleSkinConfig() => new(
        ClientId: "client-id",
        ClientSecret: string.Empty,
        RedirectUri: new Uri("http://127.0.0.1:17342/oauth/littleskin/callback"));

    internal static async ValueTask LittleSkinDeviceFlowRunsToEnd()
    {
        ScriptedHandler handler = new();
        LittleSkinOAuthService service = new(new HttpClient(handler));
        handler.Serve(
            "https://open.littleskin.cn/oauth/device_code",
            """{"user_code":"LS-1234","device_code":"LS-DEV","verification_uri":"https://open.littleskin.cn/device","verification_uri_complete":"https://open.littleskin.cn/device?c=LS-1234","expires_in":300,"interval":1}""");
        // One pending poll (OAuth errors arrive as HTTP 400), then success.
        handler.Serve(
            "https://open.littleskin.cn/oauth/token",
            """{"error":"authorization_pending"}""",
            HttpStatusCode.BadRequest);
        handler.Serve(
            "https://open.littleskin.cn/oauth/token",
            """{"access_token":"LS-AT","refresh_token":"LS-RT","expires_in":259200,"id_token":"id-1"}""");

        LittleSkinDeviceCodeInfo device = await service.RequestDeviceCodeAsync(LittleSkinConfig());
        AssertEqual("LS-1234", device.UserCode);
        AssertEqual("LS-DEV", device.DeviceCode);
        AssertEqual(300, device.ExpiresInSeconds);

        LittleSkinOAuthTokens tokens = await service.WaitForDeviceAuthorizationAsync(LittleSkinConfig(), device);
        AssertEqual("LS-AT", tokens.AccessToken);
        AssertEqual("LS-RT", tokens.RefreshToken);
        AssertEqual("id-1", tokens.IdToken);
        AssertTrue(tokens.ExpiresAt > DateTimeOffset.UtcNow);
    }

    internal static async ValueTask LittleSkinTokenPathsAndInvalidClient()
    {
        ScriptedHandler handler = new();
        LittleSkinOAuthService service = new(new HttpClient(handler));

        // Refresh goes to open.littleskin.cn for device-flow tokens.
        handler.Serve("https://open.littleskin.cn/oauth/token",
            """{"access_token":"LS-AT2","refresh_token":"LS-RT2","expires_in":259200}""");
        LittleSkinOAuthTokens refreshed = await service.RefreshOAuthTokenAsync(LittleSkinConfig(), "LS-RT");
        AssertEqual("LS-AT2", refreshed.AccessToken);
        AssertTrue(handler.Requests.Any(request => request.Contains("open.littleskin.cn", StringComparison.Ordinal)));

        // An app that is not whitelisted for the device flow gets the invalid_client message.
        ScriptedHandler plain = new();
        plain.Serve(
            "https://open.littleskin.cn/oauth/device_code",
            """{"error":"invalid_client"}""",
            HttpStatusCode.BadRequest);
        LittleSkinOAuthService rejecting = new(new HttpClient(plain));
        bool invalidClient = false;
        try
        {
            await rejecting.RequestDeviceCodeAsync(LittleSkinConfig());
        }
        catch (InvalidOperationException failure)
        {
            invalidClient = failure.Message.Contains("invalid_client", StringComparison.Ordinal);
        }

        AssertTrue(invalidClient);

        // The authorization-code exchange requires a client secret.
        bool secretRequired = false;
        try
        {
            await service.ExchangeAuthorizationCodeAsync(LittleSkinConfig(), "code-1");
        }
        catch (InvalidOperationException failure)
        {
            secretRequired = failure.Message.Contains("CLIENT_SECRET", StringComparison.Ordinal);
        }

        AssertTrue(secretRequired);
    }

    internal static async ValueTask LittleSkinProfilesSessionClosetAndApply()
    {
        ScriptedHandler handler = new();
        LittleSkinOAuthService service = new(new HttpClient(handler));
        handler.Serve(
            "https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile",
            """[{"id":"uuid-ls","name":"Lemon"}]""");
        handler.Serve(
            "https://littleskin.cn/api/yggdrasil/authserver/oauth",
            """{"accessToken":"MC-T","clientToken":"CT","selectedProfile":{"id":"u-u-i-d-l-s","name":"Lemon"}}""");
        handler.Serve(
            "https://littleskin.cn/api/players",
            """[{"pid":7,"name":"Lemon","tid_skin":111,"tid_cape":222}]""");
        handler.Serve(
            "https://littleskin.cn/api/closet?category=skin&page=1",
            """{"last_page":1,"data":[{"tid":111,"hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","type":"steve","pivot":{"item_name":"My Skin"}}]}""");
        handler.Serve(
            "https://littleskin.cn/api/players/7/textures",
            """{"code":0,"message":"ok"}""");
        handler.Serve(
            "https://littleskin.cn/api/closet",
            """{"code":0,"message":"ok"}""");

        IReadOnlyList<LittleSkinProfile> profiles = await service.GetProfilesAsync("LS-AT");
        AssertEqual(1, profiles.Count);
        AssertEqual("uuidls", profiles[0].Uuid);

        LittleSkinMinecraftSession session = await service.CreateMinecraftSessionAsync("LS-AT", "u-u-i-d-l-s");
        AssertEqual("Lemon", session.Username);
        AssertEqual("uuidls", session.Uuid);
        AssertEqual("MC-T", session.AccessToken);

        IReadOnlyList<LittleSkinPlayer> players = await service.GetPlayersAsync("LS-AT");
        AssertEqual(1, players.Count);
        AssertEqual(7, players[0].PlayerId);
        AssertEqual(111, players[0].SkinTextureId);
        AssertEqual(222, players[0].CapeTextureId);

        IReadOnlyList<LittleSkinClosetItem> closet = await service.GetClosetItemsAsync("LS-AT", LittleSkinTextureKind.Skin);
        AssertEqual(1, closet.Count);
        AssertEqual(111, closet[0].TextureId);
        AssertEqual("My Skin", closet[0].Name);
        AssertEqual("https://littleskin.cn/textures/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", closet[0].TextureAddress);

        await service.ApplyTextureAsync("LS-AT", 7, 222, LittleSkinTextureKind.Cape);

        // The closet is queried a second time by EnsureClosetTexture.
        handler.Serve(
            "https://littleskin.cn/api/closet?category=skin&page=1",
            """{"last_page":1,"data":[{"tid":111,"hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","type":"steve","pivot":{"item_name":"My Skin"}}]}""");
        await service.EnsureClosetTextureAsync("LS-AT", 111, "My Skin", LittleSkinTextureKind.Skin);

        // EnsureClosetTexture on an unknown texture must POST it into the closet.
        ScriptedHandler fresh = new();
        LittleSkinOAuthService freshService = new(new HttpClient(fresh));
        fresh.Serve(
            "https://littleskin.cn/api/closet?category=cape&page=1",
            """{"last_page":1,"data":[]}""");
        fresh.Serve("https://littleskin.cn/api/closet", """{"code":0,"message":"ok"}""");
        await freshService.EnsureClosetTextureAsync("LS-AT", 333, " ", LittleSkinTextureKind.Cape);
        AssertTrue(fresh.Requests.Any(request => request.Contains("api/closet", StringComparison.Ordinal)));
    }

    internal static async ValueTask LittleSkinTextureUploadUsesMinecraftToken()
    {
        ScriptedHandler handler = new();
        LittleSkinOAuthService service = new(new HttpClient(handler));
        handler.Serve(
            "https://littleskin.cn/api/yggdrasil/api/user/profile/12345678901234567890123456789012/skin",
            """{}""");

        const string dashedUuid = "12345678-9012-3456-7890-123456789012";
        LittleSkinTextureUploadResult result = await service.UploadMinecraftTextureAsync(
            "MC-T",
            dashedUuid,
            [0x89, 0x50, 0x4E, 0x47],
            "skin.png",
            isSlim: true);

        AssertEqual("12345678901234567890123456789012", result.ProfileUuid);
        AssertTrue(result.IsSlim);
        AssertTrue(handler.Requests.Single(request => request.Contains("/skin", StringComparison.Ordinal))
            .Contains("api/user/profile/12345678901234567890123456789012/skin", StringComparison.Ordinal));

        // A short UUID is refused without touching the network.
        ScriptedHandler strict = new();
        LittleSkinOAuthService strictService = new(new HttpClient(strict));
        bool shortUuid = false;
        try
        {
            await strictService.UploadMinecraftTextureAsync("MC-T", "u-u-i-d-l-s", [0x89], "s.png", false);
        }
        catch (ArgumentException)
        {
            shortUuid = true;
        }

        AssertTrue(shortUuid);
        AssertEqual(0, strict.Requests.Count);
    }

    internal static async ValueTask MicrosoftSkinUploadParsesActiveTexture()
    {
        ScriptedHandler handler = new();
        MinecraftSkinService service = new(new HttpClient(handler));
        handler.Serve(
            "https://api.minecraftservices.com/minecraft/profile/skins",
            """{"skins":[{"state":"ACTIVE","url":"https://textures.example/active"},{"state":"X","url":"not-a-url"}]}""");

        MinecraftSkinUploadResult result = await service.UploadAsync(
            "MC-AT",
            [0x89, 0x50, 0x4E, 0x47],
            "me.png",
            isSlim: false);

        AssertEqual("https://textures.example/active", result.SkinAddress);
        AssertNull(MinecraftSkinService.ParseActiveSkinAddress("not json"));
        AssertNull(MinecraftSkinService.ParseActiveSkinAddress("{}"));

        ScriptedHandler failing = new();
        MinecraftSkinService failingService = new(new HttpClient(failing));
        bool rejected = false;
        try
        {
            await failingService.UploadAsync("MC-AT", [0x01], "me.png", false);
        }
        catch (HttpRequestException failure)
        {
            rejected = failure.Message.Contains("更换正版皮肤失败", StringComparison.Ordinal);
        }

        AssertTrue(rejected);
    }

    internal static async ValueTask MicrosoftCapeServiceListsAndActivates()
    {
        ScriptedHandler handler = new();
        MinecraftCapeService service = new(new HttpClient(handler));
        handler.Serve(
            "https://api.minecraftservices.com/minecraft/profile",
            """{"capes":[{"id":"cape-1","alias":"First","url":"https://r.example/1","state":"ACTIVE"},{"id":"cape-1","alias":"dup","url":"https://r.example/1","state":"ACTIVE"},{"id":"cape-2","alias":"Second","url":"https://r.example/2","state":"X"}]}""");
        handler.Serve(
            "https://api.minecraftservices.com/minecraft/profile",
            """{"capes":[{"id":"cape-2","alias":"Second","url":"https://r.example/2","state":"X"}]}""");
        handler.Serve(
            "https://api.minecraftservices.com/minecraft/profile/capes/active",
            """{}""");

        IReadOnlyList<MinecraftOwnedCape> owned = await service.GetOwnedCapesAsync("MC-AT");
        AssertEqual(2, owned.Count); // duplicates collapse
        AssertTrue(owned[0].IsActive);
        AssertEqual("First", owned[0].Alias);

        await service.SetActiveCapeAsync("MC-AT", "cape-2");

        // An unowned cape is refused before any request.
        ScriptedHandler strictHandler = new();
        MinecraftCapeService strict = new(new HttpClient(strictHandler));
        strictHandler.Serve(
            "https://api.minecraftservices.com/minecraft/profile",
            """{"capes":[]}""");
        bool unowned = false;
        try
        {
            await strict.SetActiveCapeAsync("MC-AT", "cape-9");
        }
        catch (InvalidOperationException failure)
        {
            unowned = failure.Message.Contains("不属于", StringComparison.Ordinal);
        }

        AssertTrue(unowned);

        // Preview preference: the ACTIVE cape wins over the sessionserver address.
        AssertEqual(
            "https://r.example/1",
            MinecraftCapeService.PreferCapePreviewAddress(owned, "https://session.example/cape"));
        // With no ACTIVE cape and no session address there is no preview at all.
        AssertNull(MinecraftCapeService.PreferCapePreviewAddress([], null));
    }
}
