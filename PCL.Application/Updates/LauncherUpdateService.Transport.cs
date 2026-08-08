// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

public sealed partial class LauncherUpdateService
{
    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        string url,
        CancellationToken cancellationToken)
    {
        string current = url;
        for (int redirect = 0; redirect < 6; redirect++)
        {
            HttpResponseMessage response = await GetAsyncSafe(current, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                return response;
            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(new Uri(current), response.Headers.Location);
            if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(next.UserInfo))
            {
                response.Dispose();
                throw new InvalidOperationException("更新元数据重定向到了不安全的地址。");
            }
            response.Dispose();
            current = next.AbsoluteUri;
        }

        throw new InvalidOperationException("补丁下载地址重定向次数过多。");
    }

    private async Task<long?> TryGetContentLengthAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(url, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PortableLog.Debug("Update", $"无法读取完整包大小，将使用补丁协议阈值：{ex.Message}");
            return null;
        }
    }

    private async Task<HttpResponseMessage> GetAsyncSafe(string url, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            PortableLog.Debug("Update", $"请求更新元数据：{url}");
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            PortableLog.Debug("Update", $"更新元数据响应：{url}；HTTP={(int)response.StatusCode}。");
            return response;
        }
        catch (ObjectDisposedException ex)
        {
            PortableLog.Error(ex, "Update", "更新检查服务已关闭。");
            throw new InvalidOperationException("更新检查服务已关闭，请重新打开软件更新页后再试。");
        }
    }

    private static bool IsSuccessOrRedirect(HttpStatusCode code) =>
        ((int)code is >= 200 and < 300) ||
        IsRedirect(code);

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            or HttpStatusCode.Found or HttpStatusCode.SeeOther;

}


