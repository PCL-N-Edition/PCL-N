// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching.Appearance;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class SkinAppearanceTests
{
    [TestMethod]
    public void ThirdPartyProfileBadge_UsesProviderHostWithoutImplementationBrand()
    {
        LoginProfileInfo profile = new(
            "Player",
            "legacy provider badge · LittleSkin",
            LaunchLoginProfileKind.ThirdParty,
            AuthServer: "https://littleskin.cn/api/yggdrasil");

        Assert.IsTrue(
            profile.DisplayInfo.EndsWith(" · littleskin.cn", StringComparison.Ordinal));
        Assert.IsFalse(
            profile.DisplayInfo.Contains("legacy provider badge", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void NCloudStorageTexture_IsRecognizedAsMinecraftSkin()
    {
        Assert.IsTrue(AsyncLogoLoader.LooksLikeMinecraftSkinAddress(
            "https://example.supabase.co/storage/v1/object/public/ncloud-skins/ab/skin.png"));
        Assert.IsFalse(AsyncLogoLoader.LooksLikeMinecraftSkinAddress(
            "https://example.supabase.co/storage/v1/object/public/plugin-icons/icon.png"));
    }

    [TestMethod]
    public void AppearanceCardMetrics_AdaptLayoutAndPreserveModelRatio()
    {
        AppearanceCardMetrics compact = PageSkinAppearanceRight.CalculateCardMetrics(
            146d,
            canApply: true);
        AppearanceCardMetrics roomy = PageSkinAppearanceRight.CalculateCardMetrics(
            286d,
            canApply: true);

        Assert.IsTrue(compact.IsHorizontal);
        Assert.IsFalse(roomy.IsHorizontal);
        Assert.IsTrue(compact.CardHeight < roomy.CardHeight);
        Assert.IsTrue(compact.PreviewHeight < roomy.PreviewHeight);
        Assert.AreEqual(
            compact.PreviewHeight * MinecraftPlayerPreview.PreferredAspectRatio,
            compact.PreviewWidth,
            0.001d);
        Assert.AreEqual(
            roomy.PreviewHeight * MinecraftPlayerPreview.PreferredAspectRatio,
            roomy.PreviewWidth,
            0.001d);
        Assert.IsTrue(compact.PreviewHeight < compact.CardHeight);
        Assert.IsTrue(roomy.PreviewHeight < roomy.CardHeight);
    }

    [TestMethod]
    public void MinecraftPlayerPreview_CyclesThroughAllSevenViews()
    {
        MinecraftPlayerView view = MinecraftPlayerView.Isometric;
        HashSet<MinecraftPlayerView> visited = [];
        for (int index = 0; index < 7; index++)
        {
            visited.Add(view);
            view = MinecraftPlayerPreview.GetNextView(view);
        }

        Assert.HasCount(7, visited);
        Assert.AreEqual(MinecraftPlayerView.Isometric, view);
    }

    [TestMethod]
    public void MinecraftPlayerPreview_RendersExpandedSecondLayerInEveryView()
    {
        foreach (MinecraftPlayerView view in Enum.GetValues<MinecraftPlayerView>())
        {
            int baseFaces = MinecraftPlayerPreview.CountVisibleSkinFaces(
                view,
                isSlim: false,
                includeSecondLayer: false);
            int dualLayerFaces = MinecraftPlayerPreview.CountVisibleSkinFaces(
                view,
                isSlim: false,
                includeSecondLayer: true);
            ProjectedBounds bounds = MinecraftPlayerPreview.CalculateProjectedSkinBounds(
                view,
                isSlim: false,
                includeSecondLayer: true);

            Assert.AreEqual(baseFaces * 2, dualLayerFaces, $"Unexpected layer count in {view}.");
            Assert.IsGreaterThan(0d, bounds.Width, $"Invalid width in {view}.");
            Assert.IsGreaterThan(0d, bounds.Height, $"Invalid height in {view}.");
        }
    }

    [TestMethod]
    public void MinecraftProfileTextureResolver_ParsesSkinCapeAndSlimModel()
    {
        string payload = JsonSerializer.Serialize(new
        {
            textures = new
            {
                SKIN = new
                {
                    url = "https://textures.example/skin.png",
                    metadata = new { model = "slim" }
                },
                CAPE = new { url = "https://textures.example/cape.png" }
            }
        });
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        using JsonDocument document = JsonDocument.Parse(
            $$"""{"properties":[{"name":"textures","value":"{{encoded}}"}]}""");

        MinecraftProfileTextures result = MinecraftProfileTextureResolver.ParseSessionProfile(
            document.RootElement,
            "fallback");

        Assert.AreEqual("https://textures.example/skin.png", result.SkinAddress);
        Assert.AreEqual("https://textures.example/cape.png", result.CapeAddress);
        Assert.IsTrue(result.IsSlim);
    }

    [TestMethod]
    public async Task MinecraftPlayerPreview_KeepsSkinWhenCapeDownloadFails()
    {
        byte[] expectedSkin = [1, 2, 3];
        (byte[]? skin, byte[]? cape) = await MinecraftPlayerPreview.LoadTextureBytesAsync(
            "https://textures.example/skin.png",
            "https://textures.example/cape.png",
            address => address.Contains("cape", StringComparison.Ordinal)
                ? Task.FromException<byte[]?>(new HttpRequestException("cape failed"))
                : Task.FromResult<byte[]?>(expectedSkin));

        CollectionAssert.AreEqual(expectedSkin, skin);
        Assert.IsNull(cape);
    }

    [TestMethod]
    public async Task SkinAppearanceHistoryStore_DeduplicatesAndKeepsNewestEntry()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PCL-N-Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "history.json");
        try
        {
            SkinAppearanceHistoryStore store = new(path);
            DateTimeOffset older = DateTimeOffset.UtcNow.AddMinutes(-5);
            DateTimeOffset newer = DateTimeOffset.UtcNow;
            await store.RecordAsync(
            [
                new(
                    "profile-a",
                    "Old",
                    AppearanceTextureKind.Skin,
                    "https://textures.example/SKIN.png",
                    false,
                    older),
                new(
                    "profile-b",
                    "New",
                    AppearanceTextureKind.Skin,
                    "https://textures.example/skin.png",
                    true,
                    newer),
                new(
                    "profile-b",
                    "Cape",
                    AppearanceTextureKind.Cape,
                    "https://textures.example/skin.png",
                    false,
                    newer)
            ]);

            IReadOnlyList<SkinAppearanceHistoryEntry> loaded = await store.LoadAsync();

            Assert.HasCount(2, loaded);
            SkinAppearanceHistoryEntry skin = loaded.Single(entry =>
                entry.Kind == AppearanceTextureKind.Skin);
            Assert.AreEqual("New", skin.DisplayName);
            Assert.IsTrue(skin.IsSlim);
            Assert.IsTrue(loaded.Any(entry => entry.Kind == AppearanceTextureKind.Cape));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LittleSkinCatalog_MapsPublicCatalogAndTextureHash()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        using HttpClient client = new(new StubHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api" => Json("""{"site_name":"LittleSkin Test","blessing_skin":"6.0.2"}"""),
                "/skinlib/list" => Json(
                    """
                    {
                      "current_page": 2,
                      "prev_page_url": "https://littleskin.cn/skinlib/list?page=1",
                      "next_page_url": null,
                      "data": [
                        {
                          "tid": 42,
                          "name": "Test Skin",
                          "nickname": "Uploader",
                          "type": "alex",
                          "likes": 7,
                          "hd": true
                        }
                      ]
                    }
                    """),
                "/skinlib/info/42" => Json($$"""{"tid":42,"hash":"{{hash}}"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        LittleSkinCatalog catalog = new(client);

        SkinSitePage result = await catalog.GetPageAsync(new SkinSiteQuery(Page: 2));

        Assert.AreEqual("LittleSkin Test", result.SiteName);
        Assert.AreEqual("6.0.2", result.ServerVersion);
        Assert.AreEqual(2, result.Page);
        Assert.IsTrue(result.HasPreviousPage);
        Assert.IsFalse(result.HasNextPage);
        Assert.HasCount(1, result.Items);
        Assert.AreEqual("Test Skin", result.Items[0].Name);
        Assert.AreEqual("alex", result.Items[0].Model);
        Assert.AreEqual(SkinSiteTextureKind.Skin, result.Items[0].TextureKind);
        Assert.AreEqual(
            "https://littleskin.cn/textures/" + hash,
            result.Items[0].SkinAddress);
    }

    [TestMethod]
    public async Task LittleSkinCatalog_ForwardsCapeSearchAndSortAndMapsCape()
    {
        const string hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        Uri? catalogRequest = null;
        using HttpClient client = new(new StubHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api" => Json("""{"site_name":"LittleSkin","blessing_skin":"6.0.2"}"""),
                "/skinlib/list" => CaptureCatalogRequest(request),
                "/skinlib/info/84" => Json($$"""{"tid":84,"hash":"{{hash}}"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        LittleSkinCatalog catalog = new(client);

        SkinSitePage result = await catalog.GetPageAsync(new SkinSiteQuery(
            Page: 3,
            TextureKind: SkinSiteTextureKind.Cape,
            SortOrder: SkinSiteSortOrder.Likes,
            Keyword: "  迁移者  "));

        Assert.IsNotNull(catalogRequest);
        string query = Uri.UnescapeDataString(catalogRequest.Query);
        StringAssert.Contains(query, "page=3");
        StringAssert.Contains(query, "filter=cape");
        StringAssert.Contains(query, "sort=likes");
        StringAssert.Contains(query, "keyword=迁移者");
        Assert.HasCount(1, result.Items);
        Assert.AreEqual(SkinSiteTextureKind.Cape, result.Items[0].TextureKind);

        HttpResponseMessage CaptureCatalogRequest(HttpRequestMessage request)
        {
            catalogRequest = request.RequestUri;
            return Json(
                """
                {
                  "current_page": 3,
                  "prev_page_url": "https://littleskin.cn/skinlib/list?page=2",
                  "next_page_url": null,
                  "data": [
                    {
                      "tid": 84,
                      "name": "Migrator Cape",
                      "nickname": "Uploader",
                      "type": "cape",
                      "likes": 99,
                      "hd": false
                    }
                  ]
                }
                """);
        }
    }

    [TestMethod]
    public void SkinSiteInteractionPolicy_BlocksMicrosoftAndNCloudOnly()
    {
        Assert.IsFalse(SkinSiteInteractionPolicy.CanApplyPublicTexture(
            LaunchLoginProfileKind.Microsoft));
        Assert.IsFalse(SkinSiteInteractionPolicy.CanApplyPublicTexture(
            LaunchLoginProfileKind.NCloud));
        Assert.IsTrue(SkinSiteInteractionPolicy.CanApplyPublicTexture(
            LaunchLoginProfileKind.LittleSkin));
        Assert.IsTrue(SkinSiteInteractionPolicy.CanApplyPublicTexture(
            LaunchLoginProfileKind.ThirdParty));
        Assert.IsTrue(SkinSiteInteractionPolicy.CanApplyPublicTexture(
            LaunchLoginProfileKind.Offline));
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
