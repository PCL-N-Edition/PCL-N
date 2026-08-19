// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PCL.Core.IO.Net;

/// <summary>
/// Minimal DNS-over-HTTPS resolver (Cloudflare / AliDNS JSON API) without extra packages.
/// Falls back to the system resolver when all DoH endpoints fail.
/// </summary>
internal static class PortableDohResolver
{
    private static readonly string[] Endpoints =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://doh.pub/dns-query"
    ];

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient DohClient = CreateDohClient();

    private static HttpClient CreateDohClient()
    {
        // DoH bootstrap must use system DNS to avoid recursion through ConnectCallback.
        SocketsHttpHandler handler = new()
        {
            UseProxy = true,
            AllowAutoRedirect = true,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (IPAddress.TryParse(host, out IPAddress? literal) && literal is not null)
            return [literal];

        if (Cache.TryGetValue(host, out CacheEntry? cached) &&
            cached is not null &&
            cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Addresses;
        }

        List<IPAddress> addresses = [];
        foreach (string endpoint in Endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<IPAddress> batch = await QueryEndpointAsync(endpoint, host, cancellationToken)
                    .ConfigureAwait(false);
                foreach (IPAddress address in batch)
                {
                    if (!addresses.Any(existing => existing.Equals(address)))
                        addresses.Add(address);
                }

                if (addresses.Count > 0)
                    break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                // Try next endpoint.
            }
        }

        if (addresses.Count == 0)
        {
            return await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        // Prefer IPv6 then IPv4 (happy eyeballs bias).
        IPAddress[] ordered = addresses
            .OrderBy(static address => address.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1)
            .ToArray();
        Cache[host] = new CacheEntry(ordered, DateTime.UtcNow.AddMinutes(5));
        return ordered;
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryEndpointAsync(
        string endpoint,
        string host,
        CancellationToken cancellationToken)
    {
        string url = endpoint.Contains('?', StringComparison.Ordinal)
            ? endpoint + "&name=" + Uri.EscapeDataString(host) + "&type=A"
            : endpoint + "?name=" + Uri.EscapeDataString(host) + "&type=A";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
        using HttpResponseMessage response = await DohClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        List<IPAddress> results = [];
        if (!document.RootElement.TryGetProperty("Answer", out JsonElement answers) ||
            answers.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (JsonElement answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("data", out JsonElement data))
                continue;
            string? text = data.GetString();
            if (!string.IsNullOrWhiteSpace(text) && IPAddress.TryParse(text, out IPAddress? address))
                results.Add(address);
        }

        // Also query AAAA when A succeeded or alone — fire a second request for IPv6.
        string aaaaUrl = endpoint.Contains('?', StringComparison.Ordinal)
            ? endpoint + "&name=" + Uri.EscapeDataString(host) + "&type=AAAA"
            : endpoint + "?name=" + Uri.EscapeDataString(host) + "&type=AAAA";
        try
        {
            using HttpRequestMessage aaaaRequest = new(HttpMethod.Get, aaaaUrl);
            aaaaRequest.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
            using HttpResponseMessage aaaaResponse = await DohClient
                .SendAsync(aaaaRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (aaaaResponse.IsSuccessStatusCode)
            {
                await using Stream aaaaStream = await aaaaResponse.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using JsonDocument aaaaDocument = await JsonDocument
                    .ParseAsync(aaaaStream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (aaaaDocument.RootElement.TryGetProperty("Answer", out JsonElement aaaaAnswers) &&
                    aaaaAnswers.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement answer in aaaaAnswers.EnumerateArray())
                    {
                        if (!answer.TryGetProperty("data", out JsonElement data))
                            continue;
                        string? text = data.GetString();
                        if (!string.IsNullOrWhiteSpace(text) && IPAddress.TryParse(text, out IPAddress? address))
                            results.Add(address);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // IPv4 alone is fine.
        }

        return results;
    }

    private sealed record CacheEntry(IPAddress[] Addresses, DateTime ExpiresUtc);
}
