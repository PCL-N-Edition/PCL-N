// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Desktop.Features.Launching;

internal sealed record LittleSkinAuthorizationCallback(
    string? Code,
    string? Error,
    string? ErrorDescription,
    string? State);

/// <summary>
/// Minimal cross-platform loopback receiver for the LittleSkin authorization-code callback.
/// TcpListener avoids HttpListener URL ACL requirements on Windows.
/// </summary>
internal sealed class LittleSkinOAuthCallbackListener : IDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private readonly TcpListener _listener;
    private readonly Uri _redirectUri;
    private bool _started;
    private bool _disposed;

    public LittleSkinOAuthCallbackListener(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !redirectUri.IsLoopback ||
            redirectUri.Port <= 0)
        {
            throw new InvalidOperationException(
                "LittleSkin 授权代码流当前要求使用带固定端口的 HTTP 回环回调地址。");
        }

        _redirectUri = redirectUri;
        IPAddress address = string.Equals(redirectUri.Host, "::1", StringComparison.Ordinal)
            ? IPAddress.IPv6Loopback
            : IPAddress.Loopback;
        _listener = new TcpListener(address, redirectUri.Port);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;
        _listener.Start(backlog: 4);
        _started = true;
    }

    public async Task<LittleSkinAuthorizationCallback> WaitForCallbackAsync(
        string expectedState,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);
        if (!_started)
            throw new InvalidOperationException("LittleSkin OAuth 回调监听器尚未启动。");

        while (true)
        {
            using TcpClient client = await _listener
                .AcceptTcpClientAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NetworkStream stream = client.GetStream();
            string requestHeaders = await ReadHeadersAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            string? target = ReadRequestTarget(requestHeaders);
            LittleSkinAuthorizationCallback? callback = target is null
                ? null
                : ParseCallbackTarget(target, _redirectUri);
            if (callback is null)
            {
                await WriteResponseAsync(
                        stream,
                        HttpStatusCode.NotFound,
                        "未找到 PCL N OAuth 回调。",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!HasExpectedState(callback.State, expectedState))
            {
                await WriteResponseAsync(
                        stream,
                        HttpStatusCode.BadRequest,
                        "授权状态校验失败，请返回 PCL N 重新登录。",
                        cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("LittleSkin OAuth 回调 state 校验失败。");
            }

            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                await WriteResponseAsync(
                        stream,
                        HttpStatusCode.BadRequest,
                        "LittleSkin 授权未完成，可以关闭此页面并返回 PCL N。",
                        cancellationToken)
                    .ConfigureAwait(false);
                return callback;
            }

            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                await WriteResponseAsync(
                        stream,
                        HttpStatusCode.BadRequest,
                        "回调中缺少授权码，请返回 PCL N 重试。",
                        cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("LittleSkin OAuth 回调缺少授权码。");
            }

            await WriteResponseAsync(
                    stream,
                    HttpStatusCode.OK,
                    "LittleSkin 授权成功，可以关闭此页面并返回 PCL N。",
                    cancellationToken)
                .ConfigureAwait(false);
            return callback;
        }
    }

    internal static LittleSkinAuthorizationCallback? ParseCallbackTarget(
        string target,
        Uri redirectUri)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;
        Uri? requestUri;
        if (!Uri.TryCreate(target, UriKind.Absolute, out requestUri))
        {
            Uri authority = new(redirectUri.GetLeftPart(UriPartial.Authority));
            if (!Uri.TryCreate(authority, target, out requestUri))
                return null;
        }

        if (!string.Equals(
                requestUri.AbsolutePath.TrimEnd('/'),
                redirectUri.AbsolutePath.TrimEnd('/'),
                StringComparison.Ordinal))
        {
            return null;
        }

        Dictionary<string, string> query = ParseQuery(requestUri.Query);
        return new LittleSkinAuthorizationCallback(
            query.GetValueOrDefault("code"),
            query.GetValueOrDefault("error"),
            query.GetValueOrDefault("error_description"),
            query.GetValueOrDefault("state"));
    }

    internal static bool HasExpectedState(string? actual, string expected)
    {
        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected))
            return false;
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    internal static string CreateState()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static async Task<string> ReadHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        using MemoryStream data = new();
        while (data.Length < MaximumHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;
            data.Write(buffer, 0, read);
            if (ContainsHeaderTerminator(data.GetBuffer().AsSpan(0, (int)data.Length)))
                break;
        }

        if (data.Length >= MaximumHeaderBytes)
            throw new InvalidDataException("LittleSkin OAuth 回调请求头过大。");
        return Encoding.ASCII.GetString(data.GetBuffer(), 0, (int)data.Length);
    }

    private static string? ReadRequestTarget(string headers)
    {
        int lineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
        string firstLine = lineEnd >= 0 ? headers[..lineEnd] : headers;
        string[] parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
               string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = DecodeQueryComponent(parts[0]);
            if (string.IsNullOrEmpty(key))
                continue;
            result[key] = DecodeQueryComponent(parts.ElementAtOrDefault(1) ?? string.Empty);
        }

        return result;
    }

    private static string DecodeQueryComponent(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static bool ContainsHeaderTerminator(ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> terminator = "\r\n\r\n"u8;
        return value.IndexOf(terminator) >= 0;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        string title = statusCode == HttpStatusCode.OK ? "授权完成" : "授权失败";
        string html =
            "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>" + title + "</title><body style=\"font-family:system-ui;" +
            "max-width:42rem;margin:12vh auto;padding:0 24px;color:#172033\">" +
            "<h1>" + title + "</h1><p>" + message + "</p></body></html>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        string reason = statusCode == HttpStatusCode.OK ? "OK" : "Bad Request";
        string headers =
            $"HTTP/1.1 {(int)statusCode} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_started)
            _listener.Stop();
    }
}
