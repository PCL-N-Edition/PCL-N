// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PCL.Application.Accounts;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftSkinServiceTests
{
    [TestMethod]
    public async Task UploadAsync_SendsAuthenticatedSlimMultipartRequest()
    {
        using HttpClient client = new(new AsyncDelegateHandler(async request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(
                "https://api.minecraftservices.com/minecraft/profile/skins",
                request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(
                new AuthenticationHeaderValue("Bearer", "minecraft-token"),
                request.Headers.Authorization);
            string content = await request.Content!.ReadAsStringAsync();
            StringAssert.Contains(content, "name=variant");
            StringAssert.Contains(content, "slim");
            StringAssert.Contains(content, "filename=skin.png");
            return Json("""{"skins":[]}""");
        }));
        MinecraftSkinService service = new(client);

        MinecraftSkinUploadResult result = await service.UploadAsync(
            "minecraft-token",
            [1, 2, 3],
            "skin.png",
            isSlim: true);

        Assert.IsNull(result.SkinAddress);
    }

    [TestMethod]
    public async Task UploadAsync_ReturnsActiveSkinFromProfileResponse()
    {
        using HttpClient client = new(new AsyncDelegateHandler(_ => Task.FromResult(
            Json(
                """
                {
                  "skins": [
                    {"state":"INACTIVE","url":"https://textures.minecraft.net/texture/old"},
                    {"state":"ACTIVE","url":"https://textures.minecraft.net/texture/current"}
                  ]
                }
                """))));
        MinecraftSkinService service = new(client);

        MinecraftSkinUploadResult result = await service.UploadAsync(
            "minecraft-token",
            [1, 2, 3],
            "skin.png",
            isSlim: false);

        Assert.AreEqual(
            "https://textures.minecraft.net/texture/current",
            result.SkinAddress);
    }

    [TestMethod]
    public void ParseActiveSkinAddress_IgnoresMalformedSuccessfulBody()
    {
        Assert.IsNull(MinecraftSkinService.ParseActiveSkinAddress("not-json"));
        Assert.IsNull(MinecraftSkinService.ParseActiveSkinAddress(string.Empty));
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class AsyncDelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request);
    }
}
