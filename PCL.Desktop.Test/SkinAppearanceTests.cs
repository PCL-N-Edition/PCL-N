// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using PCL.Desktop.Features.Launching.Appearance;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class SkinAppearanceTests
{
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

        SkinSitePage result = await catalog.GetPageAsync(2);

        Assert.AreEqual("LittleSkin Test", result.SiteName);
        Assert.AreEqual("6.0.2", result.ServerVersion);
        Assert.AreEqual(2, result.Page);
        Assert.IsTrue(result.HasPreviousPage);
        Assert.IsFalse(result.HasNextPage);
        Assert.HasCount(1, result.Items);
        Assert.AreEqual("Test Skin", result.Items[0].Name);
        Assert.AreEqual("alex", result.Items[0].Model);
        Assert.AreEqual(
            "https://littleskin.cn/textures/" + hash,
            result.Items[0].SkinAddress);
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
