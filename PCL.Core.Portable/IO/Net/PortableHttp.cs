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
    private static readonly ReloadableHttpMessageHandler SharedHandler = new(CreateHandler());
    private static readonly HttpClient SharedClient = new(SharedHandler, disposeHandler: false);

    public static HttpClient Client => SharedClient;

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
    /// Apply launcher network settings. Safe to call after requests have started: the shared
    /// client keeps its identity while subsequent requests are routed through a new handler.
    /// </summary>
    public static void Configure(bool enableDoH, IWebProxy? proxy, bool useProxy)
    {
        PortableNetworkOptions.EnableDoH = enableDoH;

        lock (Gate)
        {
            SharedHandler.Replace(CreateHandler(useProxy, proxy));
            ActiveProxyDescription = DescribeProxy(useProxy, proxy);
        }
    }

    private static SocketsHttpHandler CreateHandler(bool useProxy = true, IWebProxy? proxy = null)
    {
        return new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = useProxy,
            // Explicit Proxy assignment is required once ConnectCallback is installed.
            // Null preserves the system-default behavior before launcher settings are loaded.
            Proxy = useProxy ? proxy : new WebProxy(),
            ConnectCallback = ConnectAsync
        };
    }

    /// <summary>
    /// Keeps the public HttpClient stable for services that cache it, while allowing immutable
    /// SocketsHttpHandler options (such as Proxy and UseProxy) to be changed safely. Retired
    /// invokers remain alive for the process lifetime so response streams already handed to a
    /// caller cannot be interrupted by a settings change.
    /// </summary>
    private sealed class ReloadableHttpMessageHandler(HttpMessageHandler initialHandler)
        : HttpMessageHandler
    {
        private readonly List<HttpMessageInvoker> _retiredInvokers = [];
        private HttpMessageInvoker _currentInvoker = new(initialHandler, disposeHandler: true);

        public void Replace(HttpMessageHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            HttpMessageInvoker next = new(handler, disposeHandler: true);
            HttpMessageInvoker previous = Interlocked.Exchange(ref _currentInvoker, next);
            _retiredInvokers.Add(previous);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Volatile.Read(ref _currentInvoker).SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Volatile.Read(ref _currentInvoker).Dispose();
                foreach (HttpMessageInvoker invoker in _retiredInvokers)
                    invoker.Dispose();
                _retiredInvokers.Clear();
            }

            base.Dispose(disposing);
        }
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
