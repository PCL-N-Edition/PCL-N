using PCL.Xsr;
using PCL.Xsr.State;
using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;

namespace PCL.Xsr.Runtime;

/// <summary>
/// Data-plane behavior of the host session: command and query forwarding with correlated
/// results, state deltas applied to the mirror, event delivery, and crash handling that marks
/// the mirror unavailable while retaining values. The receive loop is the session's single
/// reader; renderer reads stay local to the mirror store and perform no IPC.
/// </summary>
public sealed partial class SidecarHostSession
{
    private readonly Dictionary<Guid, TaskCompletionSource<SidecarExchangeOutcome>> _pending = [];
    private readonly object _pendingGate = new();
    private readonly int _maxPending;
    private ISidecarSessionEventObserver? _eventObserver;

    /// <summary>
    /// Gets the number of exchanges waiting for their correlated result.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_pendingGate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Attaches the event observer. Call once before activation.
    /// </summary>
    public void AttachEventObserver(ISidecarSessionEventObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _eventObserver = observer;
    }

    /// <summary>
    /// Forwards one command to the sidecar with a fresh correlation ID and an optional timeout.
    /// Failures cross the boundary as stable error codes; a timeout or cancellation removes the
    /// pending exchange and returns the stable timed-out or cancelled error.
    /// </summary>
    public async ValueTask<XsrResult> SendCommandAsync(
        XsrSemanticId command,
        string? argument = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Active);
        (SidecarCorrelationId correlation, Task<SidecarExchangeOutcome> completion) =
            BeginExchange(SidecarMessageType.CommandRequest, command, argument, timeout);
        try
        {
            SidecarExchangeOutcome outcome = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return OutcomeToResult(outcome);
        }
        catch (TimeoutException)
        {
            RemovePending(correlation.Value);
            return XsrResult.Failure(XsrRuntimeErrors.TimedOut());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemovePending(correlation.Value);
            return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
        }
    }

    /// <summary>
    /// Forwards one query to the sidecar and returns its string-encoded result.
    /// </summary>
    public async ValueTask<XsrResult<string>> SendQueryAsync(
        XsrSemanticId query,
        string? argument = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Active);
        (SidecarCorrelationId correlation, Task<SidecarExchangeOutcome> completion) =
            BeginExchange(SidecarMessageType.QueryRequest, query, argument, timeout);
        try
        {
            SidecarExchangeOutcome outcome = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return outcome.Success
                ? XsrResult.Success(outcome.Value)
                : XsrResult.Failure<string>(OutcomeToResult(outcome).Error!);
        }
        catch (TimeoutException)
        {
            RemovePending(correlation.Value);
            return XsrResult.Failure<string>(XsrRuntimeErrors.TimedOut());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemovePending(correlation.Value);
            return XsrResult.Failure<string>(XsrRuntimeErrors.Cancelled());
        }
    }

    /// <summary>
    /// Runs the receive loop for the active session: results complete pending exchanges, state
    /// deltas publish into the mirror, events deliver in order without coalescing, a crash or
    /// stream failure marks the session failed and the mirror unavailable, and SHUTDOWN closes
    /// the session. Unknown correlations are dropped as late results.
    /// </summary>
    public async ValueTask RunReceiveLoopAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            SidecarFrame frame;
            try
            {
                frame = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                FailWithMirrorStale($"The sidecar stream failed: {exception.Message}");
                return;
            }

            switch (frame.MessageType)
            {
                case SidecarMessageType.CommandResult or SidecarMessageType.QueryResult:
                    CompleteExchange(frame);
                    break;
                case SidecarMessageType.StateDelta:
                    ApplyStateDelta(frame.Payload.Span);
                    break;
                case SidecarMessageType.Event:
                    DeliverEvent(frame.Payload.Span);
                    break;
                case SidecarMessageType.Crash:
                    FailWithMirrorStale("The sidecar reported a crash.");
                    return;
                case SidecarMessageType.Shutdown:
                    Transition(SidecarSessionState.Closed);
                    _connection.Close();
                    return;
                default:
                    FailWithMirrorStale($"The data plane received unexpected message {frame.MessageType}.");
                    return;
            }
        }
    }

    private (SidecarCorrelationId Correlation, Task<SidecarExchangeOutcome> Completion) BeginExchange(
        SidecarMessageType requestType,
        XsrSemanticId semantic,
        string? argument,
        TimeSpan? timeout)
    {
        lock (_pendingGate)
        {
            if (_pending.Count >= _maxPending)
            {
                return (default, Task.FromResult(SidecarExchangeOutcome.Backpressure()));
            }
        }

        SidecarCorrelationId correlation = SidecarCorrelationId.Create();
        TaskCompletionSource<SidecarExchangeOutcome> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            _pending[correlation.Value] = completion;
        }

        ValueTask send = _connection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            requestType,
            SidecarFrameTraits.None,
            correlation,
            SidecarDataPlane.EncodeRequest(semantic, argument)),
            CancellationToken.None);
        if (!send.IsCompletedSuccessfully)
        {
            _ = DeliverSendFailureAsync(send, correlation.Value);
        }

        return (correlation, timeout is { } bounded
            ? completion.Task.WaitAsync(bounded)
            : completion.Task);
    }

    private async Task DeliverSendFailureAsync(ValueTask send, Guid correlation)
    {
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            CompletePending(correlation, SidecarExchangeOutcome.Unavailable());
        }
    }

    private void CompleteExchange(SidecarFrame frame)
    {
        (bool Success, string Value, string ErrorCode) decoded =
            SidecarDataPlane.DecodeResult(frame.Payload.Span);
        CompletePending(frame.CorrelationId.Value, new SidecarExchangeOutcome(
            decoded.Success,
            decoded.Value,
            decoded.ErrorCode));
    }

    private void ApplyStateDelta(ReadOnlySpan<byte> payload)
    {
        (XsrSemanticId semantic, string value) = SidecarDataPlane.DecodeStateDelta(payload);
        if (_mirror?.TryResolve(semantic) is not { } stateId)
        {
            // A delta for an unregistered state is dropped; the mirror only carries declared
            // cells.
            return;
        }

        _mirror.Store.Publish(stateId, value);
    }

    private void DeliverEvent(ReadOnlySpan<byte> payload)
    {
        (XsrSemanticId semantic, string payloadText) = SidecarDataPlane.DecodeEvent(payload);
        try
        {
            _eventObserver?.OnEvent(semantic, payloadText);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Event delivery must not be changed by an observer failure.
        }
    }

    private void FailWithMirrorStale(string reason)
    {
        Fail(reason);
        if (_mirror is { } mirror && _registration is { } registration)
        {
            foreach (SidecarRegistrationEntry entry in registration.Entries
                         .Where(entry => entry.Kind == SidecarRegistrationKind.State))
            {
                if (mirror.TryResolve(entry.SemanticId) is { } stateId)
                {
                    mirror.Store.MarkAvailability(stateId, XsrStateAvailability.Unavailable);
                }
            }
        }
    }

    private bool RemovePending(Guid correlation)
    {
        lock (_pendingGate)
        {
            return _pending.Remove(correlation);
        }
    }

    private void CompletePending(Guid correlation, SidecarExchangeOutcome outcome)
    {
        TaskCompletionSource<SidecarExchangeOutcome>? completion;
        lock (_pendingGate)
        {
            if (!_pending.Remove(correlation, out completion))
            {
                return;
            }
        }

        completion.TrySetResult(outcome);
    }

    private static XsrResult OutcomeToResult(SidecarExchangeOutcome outcome)
    {
        if (outcome.Success)
        {
            return XsrResult.Success();
        }

        string code = outcome.ErrorCode.Length == 0 ? "xsr.handler_faulted" : outcome.ErrorCode;
        XsrErrorKind kind = code == XsrRuntimeErrors.BackpressureCode.Value
            ? XsrErrorKind.Backpressure
            : XsrErrorKind.Rejected;
        return XsrResult.Failure(new XsrError(kind, XsrSemanticId.Parse(code), "The sidecar rejected the exchange."));
    }
}

/// <summary>
/// One concluded exchange: success with a value, or failure with the stable error code.
/// </summary>
public sealed record SidecarExchangeOutcome(bool Success, string Value, string ErrorCode)
{
    internal static SidecarExchangeOutcome Unavailable() => new(false, string.Empty, "xsr.unavailable");

    internal static SidecarExchangeOutcome Backpressure() => new(false, string.Empty, "xsr.backpressure");
}

/// <summary>
/// Delivers sidecar events to the host. Events are transient facts delivered in order and never
/// coalesced.
/// </summary>
public interface ISidecarSessionEventObserver
{
    void OnEvent(XsrSemanticId SemanticId, string Payload);
}
