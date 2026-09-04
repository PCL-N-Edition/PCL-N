namespace PCL.Services.Logging;

/// <summary>
/// Transport breadcrumbs for composition-owned clients. No URL paths, query strings, userinfo,
/// headers or bodies are logged. OAuth polling responses (including expected 400s) stay Debug;
/// transport/server failures remain visible without changing HTTP behavior.
/// </summary>
public sealed class DiagnosticHttpHandler(LogService log, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using LogOperation operation = log.BeginOperation("HTTP", "SendRequest",
            $"method={request.Method} host={request.RequestUri?.IdnHost}", LogLevel.Debug);
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500) operation.Reject($"http.{(int)response.StatusCode}");
            else operation.Complete($"http_status={(int)response.StatusCode}");
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            operation.Fail(exception);
            throw;
        }
    }
}
