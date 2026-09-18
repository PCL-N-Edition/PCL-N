using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Nexa.Services.Downloads;

namespace Nexa.Services.Minecraft.Launch;

public interface IAuthlibInjectorProvider
{
    Task<string> EnsureAsync(string minecraftRoot, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the upstream artifact contract and verifies SHA-256 before exposing a JVM agent.</summary>
public sealed class AuthlibInjectorProvider(HttpClient http, DownloadService downloads) : IAuthlibInjectorProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<string> EnsureAsync(string minecraftRoot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.Combine(minecraftRoot, "libraries", "authlib-injector");
            Directory.CreateDirectory(directory);
            string metadataPath = Path.Combine(directory, "artifact.json");
            string metadata;
            try { metadata = await http.GetStringAsync("https://authlib-injector.yushi.moe/artifact/latest.json", cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException) when (File.Exists(metadataPath))
            { metadata = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false); }
            using JsonDocument document = JsonDocument.Parse(metadata);
            string hash = document.RootElement.GetProperty("checksums").GetProperty("sha256").GetString() ?? "";
            string url = document.RootElement.GetProperty("download_url").GetString() ?? "";
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https")
                throw new InvalidDataException("Invalid Authlib Injector artifact metadata.");
            string final = Path.Combine(directory, hash.ToLowerInvariant() + ".jar");
            if (File.Exists(final) && await Matches(final, hash, cancellationToken).ConfigureAwait(false)) return final;
            string pending = final + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                DownloadTransferResult result = await downloads.DownloadAsync(new DownloadRequest
                {
                    Sources = [url],
                    DestinationPath = pending,
                    ConnectionFactory = source => new HttpConnection(http, source),
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!result.Success || !await Matches(pending, hash, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("Authlib Injector download failed integrity validation.");
                File.Move(pending, final, overwrite: true);
                await File.WriteAllTextAsync(metadataPath + ".new", metadata, cancellationToken).ConfigureAwait(false);
                File.Move(metadataPath + ".new", metadataPath, overwrite: true);
                return final;
            }
            finally { File.Delete(pending); File.Delete(pending + ".PCLDownloading"); }
        }
        finally { _gate.Release(); }
    }
    private static async Task<bool> Matches(string path, string hash, CancellationToken token)
    {
        await using FileStream stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), hash, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() => _gate.Dispose();

    private sealed class HttpConnection(HttpClient client, string source) : IDownloadConnection
    {
        private HttpResponseMessage? _response;
        private Stream? _stream;
        public async ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, source);
            if (beginOffset > 0) request.Headers.Range = new RangeHeaderValue(beginOffset, null);
            _response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            long length = _response.Content.Headers.ContentRange?.Length ?? _response.Content.Headers.ContentLength ?? -1;
            long start = _response.Content.Headers.ContentRange?.From ?? 0;
            return new(length, start, length - 1, false);
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _stream!.ReadAsync(buffer, cancellationToken);
        public ValueTask StopAsync(CancellationToken cancellationToken = default) { _response?.Dispose(); _response = null; _stream = null; return ValueTask.CompletedTask; }
    }
}
