namespace PCL.Xsr.Runtime;

/// <summary>
/// Dispatches typed one-shot queries through compact runtime identifiers.
/// </summary>
public sealed class XsrQueryRouter
{
    private readonly XsrRegistrySnapshot<IXsrQueryRoute> _routes;
    private readonly IXsrDispatchObserver _observer;
    private readonly TimeProvider _timeProvider;

    internal XsrQueryRouter(
        XsrRegistrySnapshot<IXsrQueryRoute> routes,
        IXsrDispatchObserver observer,
        TimeProvider timeProvider)
    {
        _routes = routes;
        _observer = observer;
        _timeProvider = timeProvider;
    }

    public int Count => _routes.Count;

    public bool TryResolve(XsrSemanticId semanticId, out XsrQueryId queryId)
    {
        if (_routes.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId))
        {
            queryId = new XsrQueryId(runtimeId);
            return true;
        }

        queryId = default;
        return false;
    }

    public async ValueTask<XsrResult<TResponse>> QueryAsync<TQuery, TResponse>(
        XsrQueryId queryId,
        TQuery query,
        TimeSpan? timeout = null,
        XsrCorrelationId correlationId = default,
        CancellationToken cancellationToken = default)
        where TQuery : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateTimeout(timeout);
        correlationId = EnsureCorrelationId(correlationId);
        long startedAt = _timeProvider.GetTimestamp();

        if (!_routes.TryGet(queryId.Value, out XsrRegistryEntry<IXsrQueryRoute> entry))
        {
            return Reject<TResponse>(queryId, correlationId, startedAt, XsrRuntimeErrors.RouteNotFound());
        }

        if (entry.Descriptor is not XsrQueryRoute<TQuery, TResponse> route)
        {
            return Reject<TResponse>(
                queryId,
                correlationId,
                startedAt,
                XsrRuntimeErrors.ContractMismatch(),
                entry.SemanticId);
        }

        XsrDispatchNotifier.NotifyStarted(_observer,
            new XsrDispatchStarted(correlationId, XsrDispatchKind.Query, entry.SemanticId, entry.RuntimeId));
        CancellationTokenSource? timeoutSource = null;
        CancellationTokenSource? linkedSource = null;
        CancellationToken effectiveToken = cancellationToken;
        string? faultType = null;
        XsrResult<TResponse> result;

        try
        {
            if (timeout is { } timeoutValue && timeoutValue != Timeout.InfiniteTimeSpan)
            {
                timeoutSource = new CancellationTokenSource(timeoutValue);
                linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutSource.Token);
                effectiveToken = linkedSource.Token;
            }

            effectiveToken.ThrowIfCancellationRequested();
            result = await route.Handler(query, effectiveToken).ConfigureAwait(false)
                ?? XsrResult.Failure<TResponse>(XsrRuntimeErrors.HandlerFaulted());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = XsrResult.Failure<TResponse>(XsrRuntimeErrors.Cancelled());
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true)
        {
            result = XsrResult.Failure<TResponse>(XsrRuntimeErrors.TimedOut());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            faultType = exception.GetType().FullName;
            result = XsrResult.Failure<TResponse>(XsrRuntimeErrors.HandlerFaulted());
        }
        finally
        {
            linkedSource?.Dispose();
            timeoutSource?.Dispose();
        }

        Observe(
            correlationId,
            entry.SemanticId,
            entry.RuntimeId,
            startedAt,
            result.Error,
            faultType);

        return result;
    }

    private XsrResult<TResponse> Reject<TResponse>(
        XsrQueryId queryId,
        XsrCorrelationId correlationId,
        long startedAt,
        XsrError error,
        XsrSemanticId semanticId = default)
    {
        Observe(correlationId, semanticId, queryId.Value, startedAt, error, null);
        return XsrResult.Failure<TResponse>(error);
    }

    private void Observe(
        XsrCorrelationId correlationId,
        XsrSemanticId semanticId,
        XsrRuntimeId runtimeId,
        long startedAt,
        XsrError? error,
        string? faultType)
    {
        XsrDispatchNotifier.Notify(
            _observer,
            new XsrDispatchObservation(
                correlationId,
                XsrDispatchKind.Query,
                semanticId,
                runtimeId,
                _timeProvider.GetElapsedTime(startedAt),
                error,
                faultType));
    }

    private static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is { } value && value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "A query timeout must be positive or infinite.");
        }
    }

    private static XsrCorrelationId EnsureCorrelationId(XsrCorrelationId correlationId) =>
        correlationId.IsAssigned ? correlationId : XsrCorrelationId.Create();
}
