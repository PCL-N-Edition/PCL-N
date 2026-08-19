// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;

namespace PCL.Core.IO.Net;

/// <summary>
/// Minimal cross-platform HTTP entry point for portable services.
/// Call <see cref="Configure"/> from launcher settings so DoH / proxy apply to all
/// consumers (vanilla install, modpack mods, metadata, etc.).
/// </summary>
public static class PortableHttp
{
    private static readonly object Gate = new();
    private static SocketsHttpHandler? _handler;
    private static readonly Lazy<HttpClient> SharedClient = new(CreateClient);

    public static HttpClient Client => SharedClient.Value;

    /// <summary>Last proxy configured for logging / diagnostics.</summary>
    public static string? ActiveProxyDescription { get; private set; }

    public static Task<string> ReadStringAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Apply launcher network settings. Safe to call repeatedly; updates the shared handler
    /// in place so already-created <see cref="Client"/> picks up proxy changes.
    /// </summary>
    public static void Configure(bool enableDoH, IWebProxy? proxy, bool useProxy)
    {
        PortableNetworkOptions.EnableDoH = enableDoH;

        // Ensure the shared client/handler exist before mutating Proxy.
        _ = Client;

        lock (Gate)
        {
            if (_handler is null)
                return;

            _handler.UseProxy = useProxy;
            // Explicit Proxy assignment — relying only on HttpClient.DefaultProxy is unreliable
            // once ConnectCallback is installed (mod downloads were effectively direct).
            _handler.Proxy = useProxy ? proxy : new WebProxy();
            ActiveProxyDescription = DescribeProxy(useProxy, proxy);
        }
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
            // Null => fall back to HttpClient.DefaultProxy until Configure() runs.
            Proxy = null,
            ConnectCallback = ConnectAsync
        };
        lock (Gate)
            _handler = handler;
        return new HttpClient(handler, disposeHandler: true);
    }

    private static string DescribeProxy(bool useProxy, IWebProxy? proxy)
    {
        if (!useProxy || proxy is null)
            return "直连";

        try
        {
            Uri probe = new("https://example.com/");
            Uri? resolved = proxy.GetProxy(probe);
            if (resolved is null ||
                string.Equals(resolved.Host, probe.Host, StringComparison.OrdinalIgnoreCase))
            {
                return "系统/直连（目标未走代理）";
            }

            return $"{resolved.Scheme}://{resolved.Host}:{resolved.Port}";
        }
        catch (Exception)
        {
            return proxy.GetType().Name;
        }
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        // When UseProxy+Proxy is set, SocketsHttpHandler passes the *proxy* DnsEndPoint here
        // and performs CONNECT/TLS itself after we return the stream.
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out IPAddress? literal) && literal is not null)
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
