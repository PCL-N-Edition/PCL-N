using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexa.Services.Telemetry;

/// <summary>Bounded, anonymous facts only. The caller owns the authenticated HTTP client.</summary>
public sealed partial class CloudflareTelemetryTransport(HttpClient client) : ITelemetryTransport
{
    public int MaximumBatchSize => 50;

    public async Task<bool> SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken = default)
    {
        if (batch.Count > MaximumBatchSize || batch.Any(item => !IsAllowed(item))) return false;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.pcln.top/v2/launcher/telemetry");
        request.Content = new StringContent(TelemetryService.SerializeBatch(batch), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        return true;
    }

    internal static bool IsAllowed(TelemetryEvent item) =>
        TelemetryEventCatalog.Level(item.Name) == item.Level
        && item.Properties.Count == 4
        && item.Properties.Keys.All(key => key is "version" or "os" or "arch" or "result")
        && item.Properties.Values.All(value => value.Length <= 64)
        && VersionPattern().IsMatch(item.Properties["version"])
        && item.Properties["os"] is "windows" or "linux" or "macos"
        && item.Properties["arch"] is "x64" or "arm64" or "x86"
        && item.Properties["result"] is "ok" or "failed" or "cancelled" or "unknown";
    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}(?:\.(?:(?:alpha|beta)\.\d{1,6}|ci\.[a-f0-9]{6}))?$")]
    private static partial Regex VersionPattern();
}
