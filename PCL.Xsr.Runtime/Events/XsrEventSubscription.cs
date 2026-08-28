namespace PCL.Xsr.Runtime;

/// <summary>
/// One ordered delivery cursor over an event scope. A subscription supports one concurrent
/// reader; additional readers may observe duplicated records, which the event contract permits.
/// </summary>
public sealed class XsrEventSubscription<TEvent> : IDisposable
    where TEvent : notnull
{
    private readonly XsrEventScopeInstance _scope;
    private readonly XsrEventId _eventId;
    private long _cursor;

    internal XsrEventSubscription(
        XsrEventScopeInstance scope,
        XsrEventId eventId,
        long cursor,
        XsrSemanticId semanticId,
        XsrSemanticId scopeId,
        string scopeKey)
    {
        _scope = scope;
        _eventId = eventId;
        _cursor = cursor;
        SemanticId = semanticId;
        ScopeId = scopeId;
        ScopeKey = scopeKey;
    }

    public XsrEventId EventId => _eventId;

    public XsrSemanticId SemanticId { get; }

    public XsrSemanticId ScopeId { get; }

    public string ScopeKey { get; }

    /// <summary>
    /// Reads the next retained record of this event in scope order. Waiting is cancellable and
    /// returns the stable cancelled error; an expired replay window returns the not-retained error.
    /// </summary>
    public async ValueTask<XsrResult<XsrEventDelivery<TEvent>>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            long cursor = Volatile.Read(ref _cursor);
            bool hasRecord = _scope.TryRead(
                ref cursor,
                _eventId,
                out XsrEventRecord record,
                out bool notRetained,
                waiter);
            Volatile.Write(ref _cursor, cursor);

            if (notRetained)
            {
                return XsrResult.Failure<XsrEventDelivery<TEvent>>(XsrRuntimeErrors.NotRetained());
            }

            if (hasRecord)
            {
                return XsrResult.Success(new XsrEventDelivery<TEvent>(record, (TEvent)record.Payload!));
            }

            try
            {
                await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return XsrResult.Failure<XsrEventDelivery<TEvent>>(XsrRuntimeErrors.Cancelled());
            }
            finally
            {
                _scope.RemoveWaiter(waiter);
            }
        }
    }

    public void Dispose()
    {
        long cursor = Interlocked.Read(ref _cursor);
        _scope.DetachCursor(cursor);
    }
}
