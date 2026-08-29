using System.Diagnostics;

namespace PCL.Services.Network;

/// <summary>One named reachability probe target.</summary>
public sealed record NetworkEndpointProbe(string Name, string Url);

/// <summary>One probe outcome: reachability, HTTP status, and round-trip latency.</summary>
public sealed record NetworkProbeResult(
    string Name,
    string Url,
    bool Reachable,
    int? StatusCode,
    double LatencyMilliseconds,
    string? Error)
{
    public static NetworkProbeResult Unreachable(NetworkEndpointProbe endpoint, string error) =>
        new(endpoint.Name, endpoint.Url, false, null, 0, error);
}

/// <summary>
/// Network reachability probing over a caller-owned HttpClient: every configured endpoint is
/// measured with a headers-only request and a wall-clock latency, failures become unreachable
/// results rather than exceptions. Surfaces use the outcomes to rank mirrors and explain
/// offline states; nothing here throws for an unreachable network.
/// </summary>
public sealed class NetworkProbeService
{
    private readonly HttpClient _client;
    private readonly IReadOnlyList<NetworkEndpointProbe> _endpoints;

    public NetworkProbeService(HttpClient client, IReadOnlyList<NetworkEndpointProbe> endpoints)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
    }

    public IReadOnlyList<NetworkEndpointProbe> Endpoints => _endpoints;

    /// <summary>Probes every configured endpoint and returns one result per endpoint.</summary>
    public async Task<IReadOnlyList<NetworkProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default)
    {
        List<NetworkProbeResult> results = [];
        foreach (NetworkEndpointProbe endpoint in _endpoints)
        {
            results.Add(await ProbeAsync(endpoint, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<NetworkProbeResult> ProbeAsync(NetworkEndpointProbe endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, endpoint.Url);
            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            double latency = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            bool reachable = (int)response.StatusCode is >= 200 and < 500;
            return new NetworkProbeResult(
                endpoint.Name,
                endpoint.Url,
                reachable,
                (int)response.StatusCode,
                latency,
                null);
        }
        catch (Exception failure) when (
            failure is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
        {
            return NetworkProbeResult.Unreachable(
                endpoint,
                failure.Message.Length > 0 ? failure.Message : failure.GetType().Name);
        }
    }
}
