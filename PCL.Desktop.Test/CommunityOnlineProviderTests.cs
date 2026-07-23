// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Desktop.Features.Community;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class CommunityOnlineProviderTests
{
    [TestMethod]
    public void McimMirrorPolicyOrdersApiAndDownloadCandidates()
    {
        IReadOnlyList<string> api = McimMirrorPolicy.ApiCandidates(
            "https://api.modrinth.com/v2/search",
            CommunityResourceSource.Modrinth,
            DownloadSourcePreference.MirrorOnly);
        Assert.AreEqual("https://mod.mcimirror.top/modrinth/v2/search", api[0]);
        Assert.AreEqual("https://api.modrinth.com/v2/search", api[1]);

        IReadOnlyList<string> downloads = McimMirrorPolicy.DownloadCandidates(
            "https://cdn.modrinth.com/data/a/file.jar",
            CommunityResourceSource.Modrinth,
            DownloadSourcePreference.PreferOfficialWithMirrorFallback);
        Assert.AreEqual("https://cdn.modrinth.com/data/a/file.jar", downloads[0]);
        Assert.AreEqual("https://mod.mcimirror.top/data/a/file.jar", downloads[1]);
    }

    [TestMethod]
    public async Task McimTranslationCachesBySourceProjectAndDescriptionHash()
    {
        int requests = 0;
        using HttpClient client = new(new DelegateHttpHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"description\":\"中文说明\"}}")
            };
        }));
        string cache = Path.Combine(Path.GetTempPath(), "pcl-mcim-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            McimTranslationService service = new(client, cache);
            CommunityResourceEntry entry = new("abc", "slug", "Title", "English", "mod", null, 0, null);
            McimTranslationResult first = await service.GetAsync(entry);
            McimTranslationResult second = await service.GetAsync(entry);
            Assert.AreEqual("中文说明", first.Text);
            Assert.IsTrue(second.FromCache);
            Assert.AreEqual(1, requests);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);
        }
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
