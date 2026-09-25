using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexa.Services.Telemetry;

/// <summary>Bounded, anonymous facts only. The caller owns the authenticated HTTP client.</summary>
public sealed partial class CloudflareTelemetryTransport(HttpClient client, Func<bool>? compactBatches = null,
    Action<string>? reportFailure = null) : ITelemetryTransport
{
    private string? _lastFailure;
    private static readonly string[] CommonKeys = ["version", "os", "arch", "result"];
    public int MaximumBatchSize => compactBatches?.Invoke() == true ? 2 : 4;

    public async Task<bool> SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken = default)
    {
        if (batch.Count > 50 || batch.Any(item => !IsAllowed(item))) return Failure("遥测批次未发送：事件格式未通过本地校验。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.pcln.top/v2/launcher/telemetry");
        request.Content = new StringContent(TelemetryService.SerializeBatch(batch), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return Failure($"遥测服务器拒绝批次：HTTP {(int)response.StatusCode}。请检查 API 客户端身份和服务端接入规则。");
        Interlocked.Exchange(ref _lastFailure, null);
        return true;
    }

    private bool Failure(string reason)
    {
        if (Interlocked.Exchange(ref _lastFailure, reason) != reason) reportFailure?.Invoke(reason);
        return false;
    }

    internal static bool IsAllowed(TelemetryEvent item) =>
        TelemetryEventCatalog.Level(item.Name) == item.Level
        && CommonKeys.All(key => item.Properties.TryGetValue(key, out var value) && value.Length <= 64)
        && DiagnosticTelemetry.ValidExtras(item)
        && VersionPattern().IsMatch(item.Properties["version"])
        && item.Properties["os"] is "windows" or "linux" or "macos"
        && item.Properties["arch"] is "x64" or "arm64" or "x86"
        && item.Properties["result"] is "ok" or "failed" or "cancelled" or "unknown";
    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}(?:\.(?:(?:alpha|beta)\.\d{1,6}|ci\.[a-f0-9]{6}))?$")]
    private static partial Regex VersionPattern();
}
