// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Hosting;
using System.Net;
using System.Text;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class LauncherAnnouncementServiceTests
{
    [TestMethod]
    public void SelectEligible_FiltersModeVersionChannelPlatformAndSeenRevision()
    {
        DateTimeOffset revision = DateTimeOffset.Parse("2026-07-19T10:00:00Z");
        LauncherAnnouncementService.LauncherAnnouncementDto info = Create(
            "survey",
            "info",
            revision,
            minimum: "1.2.0",
            maximum: "2.0.0");
        LauncherAnnouncementService.LauncherAnnouncementDto security = Create(
            "security-fix",
            "security",
            revision,
            channels: ["beta"],
            platforms: ["windows"]);

        IReadOnlyList<LauncherAnnouncement> importantOnly = LauncherAnnouncementService.SelectEligible(
            [info, security],
            "1.2.7-beta",
            "beta",
            "windows",
            "zh-TW",
            activityMode: 1,
            seen: new HashSet<string>());

        Assert.AreEqual(1, importantOnly.Count);
        Assert.AreEqual("security-fix", importantOnly[0].Id);
        Assert.AreEqual("安全公告", importantOnly[0].Title);

        IReadOnlyList<LauncherAnnouncement> seen = LauncherAnnouncementService.SelectEligible(
            [security],
            "1.2.7",
            "beta",
            "windows",
            "en-US",
            activityMode: 0,
            seen: new HashSet<string> { importantOnly[0].SeenKey });
        Assert.AreEqual(0, seen.Count);
    }

    [TestMethod]
    public async Task FetchEligibleAsync_AcceptsCloudflareItemsEnvelope()
    {
        using HttpClient client = new(new StubHttpMessageHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "items": [
                    {
                      "id": "cloudflare-notice",
                      "title": "Service notice",
                      "body": "**Cloudflare announcement body.**",
                      "publishedAt": "2026-08-28T06:00:00Z",
                      "updatedAt": "2026-08-28T07:00:00Z"
                    }
                  ],
                  "backend": "cloudflare"
                }
                """,
                Encoding.UTF8,
                "application/json")
        }));
        using LauncherAnnouncementService service = new(
            client,
            new Uri("https://api.example.test/v1/announcements"));

        IReadOnlyList<LauncherAnnouncement> result = await service.FetchEligibleAsync(
            "1.4.11-beta",
            "beta",
            "windows",
            "en-US",
            activityMode: 0,
            seen: new HashSet<string>());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("cloudflare-notice", result[0].Id);
        Assert.AreEqual("Service notice", result[0].Title);
        Assert.AreEqual("Got it", result[0].PrimaryLabel);
        Assert.AreEqual("cloudflare-notice@2026-08-28T07:00:00.0000000+00:00", result[0].SeenKey);
    }

    private static LauncherAnnouncementService.LauncherAnnouncementDto Create(
        string id,
        string severity,
        DateTimeOffset updatedAt,
        string? minimum = null,
        string? maximum = null,
        IReadOnlyList<string>? channels = null,
        IReadOnlyList<string>? platforms = null) => new(
        id,
        severity,
        100,
        minimum,
        maximum,
        channels ?? [],
        platforms ?? [],
        new Dictionary<string, LauncherAnnouncementService.LauncherAnnouncementContent>
        {
            ["zh-CN"] = new("安全公告", "**请立即更新。**"),
            ["en-US"] = new("Security notice", "**Please update now.**")
        },
        Dismissible: true,
        UpdatedAt: updatedAt);

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
