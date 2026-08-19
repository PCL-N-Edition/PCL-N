// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using PCL.Application.Updates;

namespace PCL.Application.Test;

[TestClass]
public sealed class PluginSidecarUpdateServiceTests
{
    [TestMethod]
    public async Task CheckAsync_ReadsPluginChannelAndReportsNewerVersion()
    {
        using HttpClient client = new(new RoutingHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/updates/channels/plugin", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "tag": "v0.20.1",
                      "version": "0.20.1",
                      "channel": "plugin",
                      "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                      "publishedAt": "2026-08-19T04:00:00Z",
                      "manifestKey": "channels/plugin.json",
                      "releaseNotes": "- Sidecar mismatch dialog\n- Market DTO mapping"
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        using PluginSidecarUpdateService service = new(
            client,
            distributionBaseUrl: "https://api.pcln.top/v1/updates/releases");

        PluginSidecarUpdateCheckResult result = await service.CheckAsync(
            new PluginSidecarInstallIdentity("win-x64", "SelfContained", "0.20.0"));

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("0.20.1", result.LatestVersion);
        Assert.AreEqual("v0.20.1", result.ReleaseName);
        StringAssert.Contains(result.ReleaseNotes, "Sidecar mismatch dialog");
        StringAssert.Contains(result.PackageUrl, "PCL_Plugin_Sidecar_win-x64_SelfContained.zip");
        Assert.IsFalse(
            result.PackageUrl!.Contains("blockmap", StringComparison.OrdinalIgnoreCase),
            "Sidecar updates must be full-package URLs without block maps.");
    }

    [TestMethod]
    public async Task CheckAsync_SameVersionIsNotNewer()
    {
        using HttpClient client = new(new RoutingHandler(_ => Json("""
            {
              "tag": "v0.20.1",
              "version": "0.20.1",
              "channel": "plugin",
              "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
              "publishedAt": "2026-08-19T04:00:00Z",
              "manifestKey": "channels/plugin.json"
            }
            """)));
        using PluginSidecarUpdateService service = new(
            client,
            distributionBaseUrl: "https://api.pcln.top/v1/updates/releases");

        PluginSidecarUpdateCheckResult result = await service.CheckAsync(
            new PluginSidecarInstallIdentity(
                "win-x64",
                "SelfContained",
                "0.20.1",
                "abcdef0123456789abcdef0123456789abcdef01"));

        Assert.IsTrue(result.Success);
        Assert.IsFalse(result.IsUpdateAvailable);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }
}
