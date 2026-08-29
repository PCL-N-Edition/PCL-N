namespace PCL.Services.Minecraft.Java;

/// <summary>HTTP transport for Mojang's Java runtime catalog and per-runtime manifests.</summary>
public sealed class HttpJavaRuntimeMetadataProvider : IJavaRuntimeMetadataProvider, IDisposable
{
    public const string RuntimeIndexUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpJavaRuntimeMetadataProvider()
        : this(CreateDefaultClient(), ownsClient: true)
    {
    }

    public HttpJavaRuntimeMetadataProvider(HttpClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    public async ValueTask<string> GetRuntimeIndexAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _client.GetAsync(RuntimeIndexUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestUrl);
        using HttpResponseMessage response = await _client.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static HttpClient CreateDefaultClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/2.0");
        return client;
    }
}
