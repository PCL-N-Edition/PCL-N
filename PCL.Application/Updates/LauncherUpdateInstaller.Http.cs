// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

public sealed partial class LauncherUpdateInstaller
{
    private const int UpdateRequestMaximumAttempts = 4;

    private async Task<HttpResponseMessage> GetUpdateResponseAsync(
        string url,
        bool retryNotFound,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= UpdateRequestMaximumAttempts; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(
                        url,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryableTransportFailure(ex) && attempt < UpdateRequestMaximumAttempts)
            {
                PortableLog.Warn(
                    ex,
                    "Update",
                    $"更新资源请求暂时失败，将重试（{attempt}/{UpdateRequestMaximumAttempts}）：{url}");
                await DelayBeforeUpdateRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (IsRetryableTransportFailure(ex))
            {
                throw new HttpRequestException(
                    $"更新资源请求失败（已重试 {UpdateRequestMaximumAttempts - 1} 次）：GET {url}；{ex.Message}",
                    ex);
            }

            if (response.IsSuccessStatusCode ||
                !IsRetryableUpdateStatus(response.StatusCode, retryNotFound) ||
                attempt == UpdateRequestMaximumAttempts)
            {
                return response;
            }

            PortableLog.Warn(
                "Update",
                $"更新资源暂时不可用，将重试（{attempt}/{UpdateRequestMaximumAttempts}）；" +
                $"HTTP={(int)response.StatusCode} {response.ReasonPhrase}；URL={url}");
            response.Dispose();
            await DelayBeforeUpdateRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("更新资源请求重试状态异常。");
    }

    private static void EnsureUpdateResponseSuccess(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode)
            return;

        string source = TryGetResponseHeader(response, "x-pcln-source");
        string ray = TryGetResponseHeader(response, "cf-ray");
        string diagnostics = string.Empty;
        if (!string.IsNullOrWhiteSpace(source))
            diagnostics += $"；Source={source}";
        if (!string.IsNullOrWhiteSpace(ray))
            diagnostics += $"；Ray={ray}";

        throw new HttpRequestException(
            $"更新资源请求失败：GET {url} -> HTTP {(int)response.StatusCode} {response.ReasonPhrase}{diagnostics}",
            null,
            response.StatusCode);
    }

    private static bool IsRetryableUpdateStatus(HttpStatusCode statusCode, bool retryNotFound) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        statusCode == HttpStatusCode.InternalServerError ||
        statusCode == HttpStatusCode.BadGateway ||
        statusCode == HttpStatusCode.ServiceUnavailable ||
        statusCode == HttpStatusCode.GatewayTimeout ||
        (retryNotFound && statusCode == HttpStatusCode.NotFound) ||
        (int)statusCode == 425;

    private static bool IsRetryableTransportFailure(Exception exception) =>
        exception is HttpRequestException or IOException or OperationCanceledException;

    private static Task DelayBeforeUpdateRetryAsync(int failedAttempt, CancellationToken cancellationToken)
    {
        TimeSpan delay = failedAttempt switch
        {
            1 => TimeSpan.FromMilliseconds(150),
            2 => TimeSpan.FromMilliseconds(400),
            _ => TimeSpan.FromSeconds(1)
        };
        return Task.Delay(delay, cancellationToken);
    }

    private static string TryGetResponseHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? string.Join(",", values)
            : string.Empty;
}
