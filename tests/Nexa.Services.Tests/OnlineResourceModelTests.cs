using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Capabilities;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static readonly DateTimeOffset ModelNow = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
    private static JsonObject ModelDocument() => JsonNode.Parse("""
        {"schema":1,"metric":"process-working-set-peak-mib","generatedAt":"2026-09-25T00:00:00Z",
         "expiresAt":"2026-10-02T00:00:00Z","models":[{
          "os":"windows","loader":"Fabric","samples":40,"validationSamples":10,
          "coefficients":[1024,1024,512,256],"featureMin":[1,0.5,0.25,0.25],"featureMax":[1,2,2,4],
          "validationLossMiB":100,"baselineLossMiB":200,"coverage":0.9}]}
        """)!.AsObject();
    private static byte[] ModelBytes(JsonObject document) => Encoding.UTF8.GetBytes(document.ToJsonString());

    private static void OnlineModelAdmissionAndPredictionStayBounded()
    {
        var model = OnlineWorkingSetModel.Parse(ModelBytes(ModelDocument()), ModelNow);
        AssertTrue(model.TryPredict("windows", "Fabric", 4096, 256, 16, ModelNow, out long peak));
        AssertEqual(2816L, peak);
        AssertFalse(model.TryPredict("linux", "Fabric", 4096, 256, 16, ModelNow, out _));
        AssertFalse(model.TryPredict("windows", "Forge", 4096, 256, 16, ModelNow, out _));
        AssertFalse(model.TryPredict("windows", "Fabric", 16384, 256, 16, ModelNow, out _));
        AssertFalse(model.TryPredict("windows", "Fabric", 4096, 0, 16, ModelNow, out _));
        AssertFalse(model.TryPredict("windows", "Fabric", 4096, 256, 65, ModelNow, out _));
        AssertFalse(model.TryPredict("windows", "Fabric", 4096, 256, 16, ModelNow.AddDays(7), out _));
        AssertFalse(model.TryPredict("windows", "Fabric", 4096, 256, 16, ModelNow.AddMinutes(-6), out _));
        foreach (Action<JsonObject> change in new Action<JsonObject>[]
        {
            root => root["schema"] = 2,
            root => root["metric"] = "heap",
            root => root["generatedAt"] = "2026-09-26T00:00:00Z",
            root => root["expiresAt"] = "2027-01-01T00:00:00Z",
            root => root["expiresAt"] = "2026-09-24T00:00:00Z",
            root => root["models"]![0]!["coefficients"]![0] = -1,
            root => root["models"]![0]!["coefficients"]![0] = 32769,
            root => root["models"]![0]!["coefficients"] = new JsonArray(1, 2),
            root => root["models"]![0]!["featureMin"]![0] = 0,
            root => root["models"]![0]!["featureMin"]![1] = 3,
            root => root["models"]![0]!["coverage"] = .79,
            root => root["models"]![0]!["validationLossMiB"] = 201,
            root => root["models"]![0]!["samples"] = 39,
            root => root["models"]![0]!["loader"] = "Unknown",
            root => root["models"]!.AsArray().Add(root["models"]![0]!.DeepClone()),
        })
        {
            var document = ModelDocument(); change(document);
            bool rejected = false;
            try { OnlineWorkingSetModel.Parse(ModelBytes(document), ModelNow); }
            catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected);
        }
        var empty = ModelDocument(); empty["models"] = new JsonArray();
        AssertFalse(OnlineWorkingSetModel.Parse(ModelBytes(empty), ModelNow)
            .TryPredict("windows", "Fabric", 4096, 256, 16, ModelNow, out _));
    }

    private sealed class ModelClock : TimeProvider
    {
        public DateTimeOffset Now = ModelNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class ModelFeed : HttpMessageHandler
    {
        public byte[] Body = ModelBytes(ModelDocument());
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AssertEqual("https://api.pcln.top/v2/launcher/resource-model", request.RequestUri!.AbsoluteUri);
            // A lying Content-Length cannot bypass actual stream accounting.
            var response = new HttpResponseMessage(Status) { Content = new StreamContent(new MemoryStream(Body)) };
            response.Content.Headers.ContentLength = 1;
            return Task.FromResult(response);
        }
    }
    private static async ValueTask OnlineModelDownloadKeepsOnlyAdmittedUnexpiredData()
    {
        var feed = new ModelFeed(); using var http = new HttpClient(feed); var clock = new ModelClock();
        using var client = new OnlineWorkingSetModelClient(http, clock);
        AssertTrue(await client.RefreshAsync());
        var accepted = client.Current; AssertTrue(accepted is not null);
        feed.Body = new byte[OnlineWorkingSetModel.MaximumDocumentBytes + 1];
        AssertFalse(await client.RefreshAsync()); AssertTrue(ReferenceEquals(accepted, client.Current));
        feed.Body = Encoding.UTF8.GetBytes("{\"schema\":1,\"schema\":1}");
        AssertFalse(await client.RefreshAsync());
        feed.Body = ModelBytes(ModelDocument()).Concat(Encoding.UTF8.GetBytes("garbage")).ToArray();
        AssertFalse(await client.RefreshAsync());
        feed.Status = HttpStatusCode.ServiceUnavailable;
        AssertFalse(await client.RefreshAsync()); AssertTrue(ReferenceEquals(accepted, client.Current));
        feed.Status = HttpStatusCode.OK;
        var older = ModelDocument(); older["generatedAt"] = "2026-09-24T00:00:00Z"; older["expiresAt"] = "2026-10-01T00:00:00Z";
        feed.Body = ModelBytes(older); AssertFalse(await client.RefreshAsync());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool cancellationObserved = false;
        try { await client.RefreshAsync(canceled.Token); } catch (OperationCanceledException) { cancellationObserved = true; }
        AssertTrue(cancellationObserved);
        clock.Now = ModelNow.AddDays(7);
        AssertTrue(client.Current is null);
        feed.Body = ModelBytes(ModelDocument()); AssertFalse(await client.RefreshAsync());
    }
}
