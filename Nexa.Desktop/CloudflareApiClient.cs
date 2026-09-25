using System.Security.Cryptography.X509Certificates;

namespace Nexa.Desktop;

/// <summary>Owns the API Shield identity and its no-redirect HTTP connection pool.</summary>
internal sealed class CloudflareApiClient : IDisposable
{
    private readonly X509Certificate2 _certificate;
    internal HttpClient Client { get; }

    private CloudflareApiClient(X509Certificate2 certificate)
    {
        _certificate = certificate;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, ClientCertificateOptions = ClientCertificateOption.Manual };
        handler.ClientCertificates.Add(certificate);
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    internal static CloudflareApiClient? TryCreate()
    {
        string? path = Environment.GetEnvironmentVariable("NEXA_API_CLIENT_CERT_PATH");
        string? password = Environment.GetEnvironmentVariable("NEXA_API_CLIENT_CERT_PASSWORD");
        // A launcher started by an existing terminal may inherit an older environment.
        // Read the explicitly configured user value without copying credentials into the checkout.
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable("NEXA_API_CLIENT_CERT_PATH", EnvironmentVariableTarget.User);
            password ??= Environment.GetEnvironmentVariable("NEXA_API_CLIENT_CERT_PASSWORD", EnvironmentVariableTarget.User);
        }
        X509Certificate2 certificate;
        // Windows Schannel cannot use ephemeral PFX private keys (SEC_E_NO_CREDENTIALS).
        // UserKeySet without PersistKeySet lets certificate disposal clean up imported keys.
        X509KeyStorageFlags keyStorage = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        if (!string.IsNullOrWhiteSpace(path))
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, keyStorage);
        else
        {
            using var stream = typeof(CloudflareApiClient).Assembly.GetManifestResourceStream("Nexa.Desktop.Assets.api-client.pfx");
            if (stream is null) return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            certificate = X509CertificateLoader.LoadPkcs12(buffer.ToArray(), password, keyStorage);
        }
        if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
        {
            certificate.Dispose();
            throw new InvalidOperationException("Cloudflare API 客户端证书不可用。");
        }
        return new CloudflareApiClient(certificate);
    }

    public void Dispose() { Client.Dispose(); _certificate.Dispose(); }
}
