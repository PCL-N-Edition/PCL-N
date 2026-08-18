// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PCL.Application.Accounts;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftCapeServiceTests
{
    [TestMethod]
    public async Task GetOwnedCapesAsync_ParsesOnlyAccountCapesAndActiveState()
    {
        using HttpClient client = new(new DelegateHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual(
                "https://api.minecraftservices.com/minecraft/profile",
                request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(
                new AuthenticationHeaderValue("Bearer", "minecraft-token"),
                request.Headers.Authorization);
            return Json(
                """
                {
                  "capes": [
                    {
                      "id": "cape-active",
                      "state": "ACTIVE",
                      "url": "https://textures.minecraft.net/texture/active",
                      "alias": "Migrator"
                    },
                    {
                      "id": "cape-owned",
                      "state": "INACTIVE",
                      "url": "https://textures.minecraft.net/texture/owned",
                      "alias": "Vanilla"
                    },
                    {
                      "id": "cape-invalid",
                      "state": "INACTIVE",
                      "url": "file:///not-a-network-texture",
                      "alias": "Invalid"
                    }
                  ]
                }
                """);
        }));
        MinecraftCapeService service = new(client);

        IReadOnlyList<MinecraftOwnedCape> result =
            await service.GetOwnedCapesAsync("minecraft-token");

        Assert.HasCount(2, result);
        Assert.IsTrue(result.Single(cape => cape.Id == "cape-active").IsActive);
        MinecraftOwnedCape owned = result.Single(cape => cape.Id == "cape-owned");
        Assert.AreEqual("Vanilla", owned.Name);
        Assert.AreEqual(
            "https://textures.minecraft.net/texture/owned",
            owned.TextureAddress);
        Assert.IsFalse(owned.IsActive);
    }

    [TestMethod]
    public void PreferCapePreviewAddress_PrefersActiveOwnedCapeOverSessionUrl()
    {
        string? preferred = MinecraftCapeService.PreferCapePreviewAddress(
            [
                new MinecraftOwnedCape(
                    "inactive",
                    "Inactive",
                    "https://textures.minecraft.net/texture/inactive",
                    IsActive: false),
                new MinecraftOwnedCape(
                    "active",
                    "Active",
                    "https://textures.minecraft.net/texture/active",
                    IsActive: true)
            ],
            "https://textures.minecraft.net/texture/session");

        Assert.AreEqual("https://textures.minecraft.net/texture/active", preferred);
    }

    [TestMethod]
    public void PreferCapePreviewAddress_FallsBackToSessionCapeWhenNoneActive()
    {
        string? preferred = MinecraftCapeService.PreferCapePreviewAddress(
            [
                new MinecraftOwnedCape(
                    "owned",
                    "Owned",
                    "https://textures.minecraft.net/texture/owned",
                    IsActive: false)
            ],
            "https://textures.minecraft.net/texture/session");

        Assert.AreEqual("https://textures.minecraft.net/texture/session", preferred);
    }

    [TestMethod]
    public async Task SetActiveCapeAsync_ActivatesCapeOwnedByAuthenticatedAccount()
    {
        int putCount = 0;
        using HttpClient client = new(new AsyncDelegateHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(
                    """
                    {
                      "capes": [{
                        "id": "owned-cape",
                        "state": "INACTIVE",
                        "url": "https://textures.minecraft.net/texture/owned",
                        "alias": "Owned"
                      }]
                    }
                    """);
            }

            Assert.AreEqual(HttpMethod.Put, request.Method);
            Assert.AreEqual(
                "https://api.minecraftservices.com/minecraft/profile/capes/active",
                request.RequestUri!.AbsoluteUri);
            string body = await request.Content!.ReadAsStringAsync();
            using JsonDocument document = JsonDocument.Parse(body);
            Assert.AreEqual(
                "owned-cape",
                document.RootElement.GetProperty("capeId").GetString());
            putCount++;
            return Json("""{"capes":[]}""");
        }));
        MinecraftCapeService service = new(client);

        await service.SetActiveCapeAsync("minecraft-token", "owned-cape");

        Assert.AreEqual(1, putCount);
    }

    [TestMethod]
    public async Task SetActiveCapeAsync_RejectsCapeNotOwnedByAuthenticatedAccount()
    {
        int putCount = 0;
        using HttpClient client = new(new DelegateHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
                putCount++;
            return Json(
                """
                {
                  "capes": [{
                    "id": "owned-cape",
                    "state": "INACTIVE",
                    "url": "https://textures.minecraft.net/texture/owned",
                    "alias": "Owned"
                  }]
                }
                """);
        }));
        MinecraftCapeService service = new(client);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SetActiveCapeAsync("minecraft-token", "someone-elses-cape"));

        StringAssert.Contains(exception.Message, "不属于当前正版账户");
        Assert.AreEqual(0, putCount);
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class AsyncDelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request);
    }
}
