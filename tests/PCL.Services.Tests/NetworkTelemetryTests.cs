using System.Net;
using PCL.Services.Network;
using PCL.Services.Telemetry;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

// XSR-517: Network probing and opt-in telemetry — reachability outcomes over a stub handler,
// consent-gated buffering with bounded eviction, and flush semantics through a transport port.
internal static partial class Program
{
    private sealed class DictionaryHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpStatusCode> _codes = new(StringComparer.Ordinal);

        public void Serve(string url, HttpStatusCode code) => _codes[url] = code;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_codes.TryGetValue(request.RequestUri!.ToString(), out HttpStatusCode code))
            {
                // An unserved host models a connection failure, not an HTTP error.
                throw new HttpRequestException("connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(code));
        }
    }

    private sealed class RecordingTransport : ITelemetryTransport
    {
        public bool Accept { get; set; } = true;

        public List<IReadOnlyList<TelemetryEvent>> Batches { get; } = [];

        public Task<bool> SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken = default)
        {
            Batches.Add(batch);
            return Task.FromResult(Accept);
        }
    }

    private static TelemetryService CreateTelemetryService(int capacity = 500, IXsrStateObserver? observer = null)
    {
        XsrStateStoreBuilder builder = new();
        TelemetryService.DeclareState(builder);
        return new TelemetryService(builder.Build(observer), capacity);
    }

    internal static async ValueTask NetworkProbesReportReachabilityAndLatency()
    {
        DictionaryHandler handler = new();
        handler.Serve("https://mirror-a.example/ping", HttpStatusCode.OK);
        handler.Serve("https://mirror-b.example/ping", HttpStatusCode.TooManyRequests);
        NetworkProbeService service = new(new HttpClient(handler),
        [
            new NetworkEndpointProbe("mirror-a", "https://mirror-a.example/ping"),
            new NetworkEndpointProbe("mirror-b", "https://mirror-b.example/ping"),
            new NetworkEndpointProbe("offline", "https://offline.example/ping"),
        ]);

        IReadOnlyList<NetworkProbeResult> results = await service.ProbeAllAsync();

        AssertEqual(3, results.Count);
        NetworkProbeResult reachable = results[0];
        AssertTrue(reachable.Reachable);
        AssertEqual(200, reachable.StatusCode);
        AssertNull(reachable.Error);
        AssertTrue(reachable.LatencyMilliseconds >= 0);

        NetworkProbeResult throttled = results[1];
        AssertTrue(throttled.Reachable);
        AssertEqual(429, throttled.StatusCode);

        NetworkProbeResult offline = results[2];
        AssertFalse(offline.Reachable);
        AssertNull(offline.StatusCode);

        NetworkProbeResult single = await service.ProbeAsync(new NetworkEndpointProbe("mirror-a", "https://mirror-a.example/ping"));
        AssertTrue(single.Reachable);
        await Task.CompletedTask;
    }

    internal static void TelemetryWithoutConsentRecordsNothing()
    {
        TelemetryService service = CreateTelemetryService();
        AssertFalse(service.Consent);

        service.Record("app.started", new Dictionary<string, string> { ["v"] = "2.0.0.alpha.1" });
        AssertEqual(0, service.PendingCount);
        AssertEqual(0, service.StateStore.Read<int>(service.StateStore.Resolve(TelemetryService.PendingKey)).Value);
    }

    internal static void TelemetryBuffersWithBoundedEviction()
    {
        TelemetryService service = CreateTelemetryService(capacity: 3);
        service.Consent = true;
        for (int index = 1; index <= 5; index++)
        {
            service.Record($"event.{index}", new Dictionary<string, string> { ["i"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        AssertEqual(3, service.PendingCount);
        // The pending cell tracks the current queue depth, not the lifetime total.
        AssertEqual(3, service.StateStore.Read<int>(service.StateStore.Resolve(TelemetryService.PendingKey)).Value);
    }

    internal static async ValueTask TelemetryFlushUploadsAndClearsOrRetains()
    {
        TelemetryService service = CreateTelemetryService();
        service.Consent = true;
        service.Record("app.started", new Dictionary<string, string> { ["v"] = "1" });
        service.Record("app.ready");

        RecordingTransport transport = new();
        int uploaded = await service.FlushAsync(transport);
        AssertEqual(2, uploaded);
        AssertEqual(1, transport.Batches.Count);
        AssertEqual(0, service.PendingCount);
        AssertEqual("app.started", transport.Batches[0][0].Name);
        AssertTrue(transport.Batches[0][0].Properties.ContainsKey("v"));

        // Empty flush is a zero no-op.
        AssertEqual(0, await service.FlushAsync(transport));

        // A rejected batch stays buffered.
        transport.Accept = false;
        service.Record("app.later");
        AssertEqual(0, await service.FlushAsync(transport));
        AssertEqual(1, service.PendingCount);

        // The pending count is visible as state.
        AssertEqual(1, service.StateStore.Read<int>(service.StateStore.Resolve(TelemetryService.PendingKey)).Value);
    }

    internal static void TelemetryBatchSerializationIsStable()
    {
        TelemetryEvent @event = new(
            "app.started",
            DateTimeOffset.FromUnixTimeMilliseconds(1_000),
            new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
        string json = TelemetryService.SerializeBatch([@event]);

        AssertTrue(json.Contains("\"name\":\"app.started\"", StringComparison.Ordinal));
        AssertTrue(json.Contains("\"timestamp\":1000", StringComparison.Ordinal));
        AssertTrue(json.IndexOf("\"a\"", StringComparison.Ordinal) < json.IndexOf("\"b\"", StringComparison.Ordinal));
    }
}
