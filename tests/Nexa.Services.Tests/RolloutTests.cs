using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Rollouts;
using Nexa.Services.Updates;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static JsonObject GrayRule(int points = 10000) => new()
    {
        ["id"] = "alpha-five",
        ["kind"] = "update",
        ["target"] = "2.0.0.alpha.5",
        ["basisPoints"] = points,
        ["channels"] = new JsonArray("alpha"),
        ["rids"] = new JsonArray("win-x64"),
        ["enabled"] = true,
        ["expiresAt"] = "2099-01-01T00:00:00Z"
    };
    private sealed class RolloutFeed : HttpMessageHandler
    {
        public string Body = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
    }
    private static async ValueTask RolloutsKeepCohortsAndFailClosed()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nexa-rollouts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var builder = new XsrStateStoreBuilder(); RolloutStateContract.DeclareState(builder); var store = builder.Build();
            var feed = new RolloutFeed(); using var http = new HttpClient(feed);
            string path = Path.Combine(folder, "seed");
            using var service = new RolloutService(http, store, path, "alpha", "win-x64");
            using var second = new RolloutService(http, store, path, "alpha", "win-x64");
            bool Included(RolloutService current, JsonObject rule) { using var doc = JsonDocument.Parse(rule.ToJsonString()); return current.Includes(doc.RootElement, "alpha", "win-x64"); }
            AssertFalse(Included(service, GrayRule(0))); AssertTrue(Included(service, GrayRule()));
            AssertEqual(Included(service, GrayRule(5000)), Included(second, GrayRule(5000)));
            for (int i = 0; i < 100; i++)
            {
                int bucket = RolloutService.Bucket(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "fixed");
                AssertTrue(bucket >= 0 && bucket < 10000);
                if (bucket < 2500) AssertTrue(bucket < 5000);
            }
            var disabled = GrayRule(); disabled["enabled"] = false; AssertFalse(Included(service, disabled));
            var expired = GrayRule(); expired["expiresAt"] = "2000-01-01T00:00:00Z"; AssertFalse(Included(service, expired));
            var wrong = GrayRule(); wrong["rids"] = new JsonArray("osx-arm64"); AssertFalse(Included(service, wrong));
            var feature = GrayRule(); feature["kind"] = "feature"; feature["target"] = "telemetry.compact-batches";
            feed.Body = new JsonObject { ["revision"] = 1, ["rules"] = new JsonArray(feature) }.ToJsonString();
            await service.RefreshAsync(); AssertTrue(service.CompactTelemetryBatches);
            feed.Body = new string(' ', 32769); await service.RefreshAsync(); AssertFalse(service.CompactTelemetryBatches);
            AssertEqual(64L, new FileInfo(path).Length);
        }
        finally { Directory.Delete(folder, true); }
    }
}
