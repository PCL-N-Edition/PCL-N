namespace PCL.Xsr.Runtime;

/// <summary>
/// Dispatches typed commands through compact runtime identifiers.
/// </summary>
public sealed class XsrCommandRouter
{
    private readonly XsrRegistrySnapshot<IXsrCommandRoute> _routes;
    private readonly IXsrDispatchObserver _observer;
    private readonly TimeProvider _timeProvider;

    internal XsrCommandRouter(
        XsrRegistrySnapshot<IXsrCommandRoute> routes,
        IXsrDispatchObserver observer,
        TimeProvider timeProvider)
    {
        _routes = routes;
        _observer = observer;
        _timeProvider = timeProvider;
    }

    public int Count => _routes.Count;

    public bool TryResolve(XsrSemanticId semanticId, out XsrCommandId commandId)
    {
        if (_routes.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId))
        {
            commandId = new XsrCommandId(runtimeId);
            return true;
        }

        commandId = default;
        return false;
    }

    public XsrCommandDispatch Dispatch<TCommand>(
        XsrCommandId commandId,
        TCommand command,
        XsrCorrelationId correlationId = default,
        CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        correlationId = EnsureCorrelationId(correlationId);

        if (!_routes.TryGet(commandId.Value, out XsrRegistryEntry<IXsrCommandRoute> entry))
        {
            return Reject(commandId, correlationId, XsrRuntimeErrors.RouteNotFound());
        }

        if (entry.Descriptor is not XsrCommandRoute<TCommand> route)
        {
            return Reject(
                commandId,
                correlationId,
                XsrRuntimeErrors.ContractMismatch(),
                entry.SemanticId);
        }

        Task<XsrResult> completion = CompleteAsync(
            route,
            entry,
            command,
            correlationId,
            cancellationToken);

        return new XsrCommandDispatch(
            correlationId,
            commandId,
            XsrResult.Success(),
            completion);
    }

    private async Task<XsrResult> CompleteAsync<TCommand>(
        XsrCommandRoute<TCommand> route,
        XsrRegistryEntry<IXsrCommandRoute> entry,
        TCommand command,
        XsrCorrelationId correlationId,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        long startedAt = _timeProvider.GetTimestamp();
        XsrResult result;
        string? faultType = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await route.Handler(command, cancellationToken).ConfigureAwait(false)
                ?? XsrResult.Failure(XsrRuntimeErrors.HandlerFaulted());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = XsrResult.Failure(XsrRuntimeErrors.Cancelled());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            faultType = exception.GetType().FullName;
            result = XsrResult.Failure(XsrRuntimeErrors.HandlerFaulted());
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

    private XsrCommandDispatch Reject(
        XsrCommandId commandId,
        XsrCorrelationId correlationId,
        XsrError error,
        XsrSemanticId semanticId = default)
    {
        XsrResult result = XsrResult.Failure(error);
        Observe(correlationId, semanticId, commandId.Value, _timeProvider.GetTimestamp(), error, null);
        return new XsrCommandDispatch(correlationId, commandId, result, Task.FromResult(result));
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
                XsrDispatchKind.Command,
                semanticId,
                runtimeId,
                _timeProvider.GetElapsedTime(startedAt),
                error,
                faultType));
    }

    private static XsrCorrelationId EnsureCorrelationId(XsrCorrelationId correlationId) =>
        correlationId.IsAssigned ? correlationId : XsrCorrelationId.Create();
}
