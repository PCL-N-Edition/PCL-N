using System.Net;
using System.Text.Json.Nodes;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class NexaFeedHandler : HttpMessageHandler
    {
        internal string Body = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AssertEqual("/v2/updates/latest", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
        }
    }
    private static async ValueTask NexaUpdatesRejectLegacyAndWrongPlatform()
    {
        var handler = new NexaFeedHandler();
        using var http = new HttpClient(handler);
        var service = new NexaUpdateService(http);
        const string version = "2.0.0.alpha.5", rid = "win-x64";
        JsonObject Asset(string suffix) => new()
        {
            ["name"] = $"Nexa-{version}-{rid}{suffix}",
            ["size"] = 123,
            ["url"] = $"https://api.pcln.top/v2/updates/releases/{version}/Nexa-{version}-{rid}{suffix}"
        };
        var root = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["product"] = "nexacl",
            ["version"] = version,
            ["channel"] = "alpha",
            ["rid"] = rid,
            ["installer"] = Asset(".setup.exe"),
            ["portable"] = Asset(".portable.zip"),
            ["githubUrl"] = $"https://github.com/PCL-N-Edition/PCL-N/releases/tag/{version}"
        };
        handler.Body = root.ToJsonString();
        var result = await service.CheckAsync(new("2.0.0.alpha.4", rid, "alpha"));
        AssertTrue(result.IsSuccess); AssertEqual(version, result.Value!.Offer!.Version);
        AssertTrue((await service.CheckAsync(new(version, rid, "alpha"))).Value!.Offer is null);
        foreach (var (key, value) in new[] { ("version", "1.99.0"), ("rid", "win-arm64"), ("product", "pcl"), ("githubUrl", "https://example.com") })
        {
            var malformed = root.DeepClone(); malformed[key] = value; handler.Body = malformed.ToJsonString();
            AssertFalse((await service.CheckAsync(new("2.0.0.alpha.4", rid, "alpha"))).IsSuccess);
        }
        handler.Body = new string(' ', 128 * 1024 + 1);
        AssertFalse((await service.CheckAsync(new("2.0.0.alpha.4", rid, "alpha"))).IsSuccess);
    }
}
