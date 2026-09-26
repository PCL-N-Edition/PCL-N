using Nexa.Core.Media;

namespace Nexa.Services.Resources;

public sealed class ResourceIconService(HttpClient http) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(4);
    private readonly Dictionary<string, PngImage> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public static bool IsAllowed(string url) => url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "cdn.modrinth.com" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal);

    public async Task<ResourceIconResult> ReadAsync(ResourceIconQuery query, CancellationToken token)
    {
        if (!IsAllowed(query.Url)) return new(null);
        lock (_gate) if (_cache.TryGetValue(query.Url, out var cached)) return new(cached);
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate) if (_cache.TryGetValue(query.Url, out var cached)) return new(cached);
            using var request = new HttpRequestMessage(HttpMethod.Get, query.Url);
            request.Headers.UserAgent.ParseAdd("NexaCL/2.0 (https://github.com/PCL-N-Edition/PCL-N)");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1_048_576) return new(null);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 1_048_577 - output.Length)), token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > 1_048_576) return new(null);
                output.Write(buffer, 0, read);
            }
            var image = PngImage.TryCreateResourceIcon(output.GetBuffer().AsSpan(0, (int)output.Length));
            if (image is not null) lock (_gate)
            {
                if (_cache.Count >= 32) _cache.Remove(_cache.Keys.First());
                _cache[query.Url] = image;
            }
            return new(image);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException) { return new(null); }
        finally { _slots.Release(); }
    }
    // The runtime cancels outstanding queries before disposal. Do not dispose a semaphore
    // while canceled HTTP operations are still unwinding and releasing their slots.
    public void Dispose() { lock (_gate) _cache.Clear(); }
}
