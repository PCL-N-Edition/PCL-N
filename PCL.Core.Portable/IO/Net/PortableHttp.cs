// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;

namespace PCL.Core.IO.Net;

/// <summary>
/// Minimal cross-platform HTTP entry point for portable services.
/// Honors <see cref="HttpClient.DefaultProxy"/> and optional DNS-over-HTTPS via
/// <see cref="PortableNetworkOptions.EnableDoH"/>.
/// </summary>
public static class PortableHttp
{
    private static readonly Lazy<HttpClient> SharedClient = new(CreateClient);

    public static HttpClient Client => SharedClient.Value;

    public static Task<string> ReadStringAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
            ConnectCallback = ConnectAsync
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else if (PortableNetworkOptions.EnableDoH)
        {
            addresses = await PortableDohResolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Length == 0)
            throw new HttpRequestException($"DNS resolution failed for {host}.");

        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                socket?.Dispose();
                lastError = ex;
            }
        }

        throw new HttpRequestException($"Connection failed for {host}:{port}.", lastError);
    }
}
