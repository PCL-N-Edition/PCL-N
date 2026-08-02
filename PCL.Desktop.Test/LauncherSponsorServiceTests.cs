// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using PCL.Desktop.Hosting;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class LauncherSponsorServiceTests
{
    [TestMethod]
    public async Task FetchAsync_ParsesPublicSponsorProjection()
    {
        HttpRequestMessage? captured = null;
        using HttpClient client = new(new RoutingHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "sponsors": [
                        { "name": "  👩‍💻 Developer  ", "isActive": true },
                        { "name": "", "isActive": false },
                        { "name": "Past Sponsor", "isActive": false }
                      ],
                      "totalCount": 7,
                      "generatedAt": "2026-08-03T00:00:00Z",
                      "stale": true
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        using LauncherSponsorService service = new(client, new Uri("https://api.test/v1/sponsors"));

        LauncherSponsorSnapshot result = await service.FetchAsync();

        Assert.AreEqual("https://api.test/v1/sponsors", captured?.RequestUri?.AbsoluteUri);
        StringAssert.Contains(captured?.Headers.UserAgent.ToString() ?? string.Empty, "PCL-N-Desktop");
        Assert.AreEqual(2, result.Sponsors.Count);
        Assert.AreEqual("👩‍💻 Developer", result.Sponsors[0].Name);
        Assert.AreEqual("👩‍💻", result.Sponsors[0].Initial);
        Assert.IsTrue(result.Sponsors[0].IsActive);
        Assert.AreEqual(7, result.TotalCount);
        Assert.IsTrue(result.IsStale);
    }

    [TestMethod]
    public async Task FetchAsync_RejectsFailedProxyResponse()
    {
        using HttpClient client = new(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using LauncherSponsorService service = new(client, new Uri("https://api.test/v1/sponsors"));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => service.FetchAsync());
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
