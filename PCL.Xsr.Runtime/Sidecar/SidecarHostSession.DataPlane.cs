using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Xsr.Runtime;

/// <summary>
/// Data-plane behavior of the host session: command and query forwarding by session-local
/// contract ID, state deltas applied to the mirror, ordered event delivery, bounded pending
/// exchanges, and cancellation that reaches the sidecar. The data plane is a capability
/// boundary: a semantic that was not registered under the requested kind is rejected locally
/// with the stable route-not-found error and never touches the wire. The receive loop is the
/// session's single reader; renderer reads stay local to the mirror store and perform no IPC.
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

    private string LastValue { get; set; } = string.Empty;

    /// <summary>
    /// Attaches the event observer. Call once before activation.
    /// </summary>
    public void AttachEventObserver(ISidecarSessionEventObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _eventObserver = observer;
    }

    /// <summary>
    /// Forwards one command to the sidecar by its session-local contract ID. Unregistered
    /// semantics are rejected locally without IPC. Cancellation sends CANCEL so the sidecar
    /// aborts the operation instead of running it to completion.
    /// </summary>
    public async ValueTask<XsrResult> SendCommandAsync(
        XsrSemanticId command,
        string? argument = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Active);
        SidecarRegistrationEntry? entry = RequireContract(SidecarRegistrationKind.Command, command);
        if (entry is null)
        {
            return XsrResult.Failure(XsrRuntimeErrors.RouteNotFound());
        }

        SidecarExchangeOutcome outcome =
            await RunExchangeAsync(SidecarMessageType.CommandRequest, entry, argument, timeout, cancellationToken)
                .ConfigureAwait(false);
        return OutcomeToResult(outcome);
    }

    /// <summary>
    /// Forwards one query to the sidecar by contract ID and returns its string-encoded result.
    /// </summary>
    public async ValueTask<XsrResult<string>> SendQueryAsync(
        XsrSemanticId query,
        string? argument = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Active);
        SidecarRegistrationEntry? entry = RequireContract(SidecarRegistrationKind.Query, query);
        if (entry is null)
        {
            return XsrResult.Failure<string>(XsrRuntimeErrors.RouteNotFound());
        }

        SidecarExchangeOutcome outcome =
            await RunExchangeAsync(SidecarMessageType.QueryRequest, entry, argument, timeout, cancellationToken)
                .ConfigureAwait(false);
        if (outcome.Success)
        {
            LastValue = outcome.Value;
            return XsrResult.Success(outcome.Value);
        }

        return XsrResult.Failure<string>(OutcomeToResult(outcome).Error!);
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
                FailWithMirrorUnavailable($"The sidecar stream failed: {exception.Message}");
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
                    FailWithMirrorUnavailable("The sidecar reported a crash.");
                    return;
                case SidecarMessageType.Shutdown:
                    Transition(SidecarSessionState.Closed);
                    _connection.Close();
                    return;
                default:
                    FailWithMirrorUnavailable($"The data plane received unexpected message {frame.MessageType}.");
                    return;
            }
        }
    }

    private SidecarRegistrationEntry? RequireContract(SidecarRegistrationKind kind, XsrSemanticId semantic) =>
        _registration?.TryResolve(kind, semantic);

    private async ValueTask<SidecarExchangeOutcome> RunExchangeAsync(
        SidecarMessageType requestType,
        SidecarRegistrationEntry entry,
        string? argument,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        lock (_pendingGate)
        {
            if (_pending.Count >= _maxPending)
            {
                return SidecarExchangeOutcome.Backpressure();
            }
        }

        SidecarCorrelationId correlation = SidecarCorrelationId.Create();
        TaskCompletionSource<SidecarExchangeOutcome> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            _pending[correlation.Value] = completion;
        }

        await _connection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            requestType,
            SidecarFrameTraits.None,
            correlation,
            SidecarDataPlane.EncodeRequest(entry.ContractId, argument)),
            CancellationToken.None).ConfigureAwait(false);

        try
        {
            Task<SidecarExchangeOutcome> wait = completion.Task;
            if (timeout is { } bounded)
            {
                wait = wait.WaitAsync(bounded, CancellationToken.None);
            }

            return await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RemovePending(correlation.Value);
            await SendCancelAsync(correlation, "host timeout", CancellationToken.None).ConfigureAwait(false);
            return SidecarExchangeOutcome.TimedOut();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemovePending(correlation.Value);
            await SendCancelAsync(correlation, "host cancelled", CancellationToken.None).ConfigureAwait(false);
            return SidecarExchangeOutcome.Cancelled();
        }
    }

    /// <summary>
    /// Sends CANCEL for one exchange so the sidecar aborts the operation. Best-effort: a
    /// failure to deliver the cancel never changes the host's outcome.
    /// </summary>
    private async ValueTask SendCancelAsync(
        SidecarCorrelationId correlation,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _connection.SendAsync(new SidecarFrame(
                SidecarProtocol.Version,
                SidecarMessageType.Cancel,
                SidecarFrameTraits.None,
                correlation,
                SidecarStateSnapshot.EncodeCancel(correlation.Value, reason)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // A failed cancel delivery never changes the caller's outcome.
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
        (uint contractId, byte[] encodedValue) = SidecarDataPlane.DecodeStateDelta(payload);
        SidecarRegistrationEntry? entry = _registration?.Entries.FirstOrDefault(
            candidate => candidate.Kind == SidecarRegistrationKind.State && candidate.ContractId == contractId);
        if (entry is null || _mirror is null)
        {
            // A delta for an undeclared contract is dropped; the mirror only carries declared
            // cells.
            return;
        }

        _mirror.PublishFromWire(entry, encodedValue);
    }

    private void DeliverEvent(ReadOnlySpan<byte> payload)
    {
        (uint contractId, string payloadText) = SidecarDataPlane.DecodeEvent(payload);
        SidecarRegistrationEntry? entry = _registration?.Entries.FirstOrDefault(
            candidate => candidate.Kind == SidecarRegistrationKind.Event && candidate.ContractId == contractId);
        if (entry is null)
        {
            return;
        }

        try
        {
            _eventObserver?.OnEvent(entry.SemanticId, payloadText);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Event delivery must not be changed by an observer failure.
        }
    }

    private void FailWithMirrorUnavailable(string reason)
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

        return XsrResult.Failure(new XsrError(
            XsrErrorKind.Rejected,
            XsrSemanticId.Parse(outcome.ErrorCode.Length == 0 ? "xsr.handler_faulted" : outcome.ErrorCode),
            "The sidecar rejected the exchange."));
    }
}

/// <summary>
/// One concluded exchange: success with a value, or failure with the stable error code.
/// </summary>
public sealed record SidecarExchangeOutcome(bool Success, string Value, string ErrorCode)
{
    public static SidecarExchangeOutcome TimedOut() => new(false, string.Empty, "xsr.timed_out");

    public static SidecarExchangeOutcome Cancelled() => new(false, string.Empty, "xsr.cancelled");

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
