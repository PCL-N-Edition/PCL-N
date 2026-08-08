// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace PCL.Application.Online;

/// <summary>Creates HTTP clients that authenticate the launcher to api.pcln.top with mTLS.</summary>
public static class PclnApiHttpClientFactory
{
    private const string CertificateResourceName = "PCL.Application.Online.PclnApiClient.pfx";

    public static HttpClient Create(bool allowAutoRedirect, TimeSpan timeout)
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.All,
            ClientCertificateOptions = ClientCertificateOption.Manual,
            UseProxy = true
        };
        X509Certificate2? certificate = LoadClientCertificate();
        if (certificate is not null)
            handler.ClientCertificates.Add(certificate);
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    private static X509Certificate2? LoadClientCertificate()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("PCLN_API_CLIENT_CERT_PATH");
        string? password = Environment.GetEnvironmentVariable("PCLN_API_CLIENT_CERT_PASSWORD");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return ValidateCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                configuredPath,
                password,
                GetStorageFlags()));

        using Stream? stream = typeof(PclnApiHttpClientFactory).Assembly
            .GetManifestResourceStream(CertificateResourceName);
        if (stream is null)
            return null;

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return ValidateCertificate(X509CertificateLoader.LoadPkcs12(
            buffer.ToArray(),
            password,
            GetStorageFlags()));
    }

    private static X509Certificate2 ValidateCertificate(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("PCL N API mTLS 客户端证书不包含私钥。");
        }

        DateTime utcNow = DateTime.UtcNow;
        if (utcNow < certificate.NotBefore.ToUniversalTime() || utcNow > certificate.NotAfter.ToUniversalTime())
        {
            certificate.Dispose();
            throw new InvalidOperationException("PCL N API mTLS 客户端证书尚未生效或已经过期。");
        }

        return certificate;
    }

    private static X509KeyStorageFlags GetStorageFlags() =>
        OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
}
