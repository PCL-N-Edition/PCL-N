using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;
using PCL.Xsr.State;

namespace PCL.Xsr.Runtime;

/// <summary>
/// The host side of one Sidecar session: drives the locked lifecycle (HELLO/WELCOME, REGISTER_*,
/// READY, ACTIVATE, shutdown) over a connection, accepts the plugin's declarations, and builds
/// the per-session state mirror. Every await validates the expected message type; any deviation
/// fails the session terminally. One session per connection; reconnection creates a new session.
/// </summary>
public sealed partial class SidecarHostSession : IDisposable
{
    private readonly object _gate = new();

    private readonly SidecarConnection _connection;
    private readonly ISidecarSessionObserver? _observer;
    private readonly TimeProvider _timeProvider;
    private SidecarSessionState _state = SidecarSessionState.Handshaking;
    private string? _failureReason;
    private Guid _sessionId;
    private SidecarRegistrationSet? _registration;
    private SidecarStateMirror? _mirror;

    public SidecarHostSession(
        SidecarConnection connection,
        string pluginName,
        ISidecarSessionObserver? observer = null,
        TimeProvider? timeProvider = null,
        int maxPending = 1024)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        PluginName = pluginName;
        _observer = observer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxPending = maxPending;
    }

    public string PluginName { get; }

    public SidecarSessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public string? FailureReason
    {
        get
        {
            lock (_gate)
            {
                return _failureReason;
            }
        }
    }

    public Guid SessionId => _sessionId;

    public SidecarRegistrationSet? Registration => _registration;

    public SidecarStateMirror? Mirror => _mirror;

    /// <summary>
    /// Gets the content cache populated at registration: UI modules and resources are served
    /// from here with zero IPC.
    /// </summary>
    public SidecarHostCache Cache { get; } = new();

    /// <summary>
    /// Sends HELLO and awaits WELCOME, enforcing the negotiated protocol version.
    /// </summary>
    public async ValueTask HandshakeAsync(CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Handshaking);
        await _connection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Hello,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarHandshake.EncodeHello(SidecarProtocol.Version, PluginName)),
            cancellationToken).ConfigureAwait(false);

        SidecarFrame welcome = await ReceiveOrFail(
            SidecarMessageType.Welcome,
            cancellationToken).ConfigureAwait(false);
        (uint negotiated, Guid sessionId) = SidecarHandshake.DecodeWelcome(welcome.Payload.Span);
        if (negotiated != SidecarProtocol.Version)
        {
            throw Fail($"The sidecar negotiated protocol version {negotiated}; {SidecarProtocol.Version} is required.");
        }

        _sessionId = sessionId;
        Transition(SidecarSessionState.Registering);
    }

    /// <summary>
    /// Accepts REGISTER_BEGIN/ITEM*/END and builds the per-session state mirror. States mirror
    /// declarations become registration entries carrying session-local contract IDs (per-kind
    /// ordinals in declaration order). Registration alone does NOT make the session ready: the
    /// state snapshot must follow, and READY goes on the wire only after it commits.
    /// </summary>
    public async ValueTask<SidecarStateMirror> AcceptRegistrationAsync(CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Registering);

        SidecarFrame begin = await ReceiveOrFail(
            SidecarMessageType.RegisterBegin,
            cancellationToken).ConfigureAwait(false);
        uint count = SidecarRegistration.DecodeBegin(begin.Payload.Span);
        List<SidecarRegistrationEntry> entries = new((int)count);
        Dictionary<XsrSemanticId, SidecarRegistrationItem> declarations = [];
        Dictionary<SidecarRegistrationKind, uint> ordinals = [];

        for (uint index = 0; index < count; index++)
        {
            SidecarFrame itemFrame = await ReceiveOrFail(
                SidecarMessageType.RegisterItem,
                cancellationToken).ConfigureAwait(false);
            SidecarRegistrationItem item = SidecarRegistration.DecodeItem(itemFrame.Payload.Span);
            XsrSemanticId semantic = XsrSemanticId.Parse(item.SemanticId);
            if (entries.Any(entry => entry.SemanticId.Equals(semantic)))
            {
                throw Fail($"The sidecar registered '{semantic}' twice.");
            }

            ordinals[item.Kind] = ordinals.TryGetValue(item.Kind, out uint ordinal) ? ordinal + 1 : 1;
            uint contractId = ordinals[item.Kind];
            declarations[semantic] = item;
            entries.Add(new SidecarRegistrationEntry(item.Kind, semantic, contractId, item.Flags, item.CodecId));
        }

        _ = await ReceiveOrFail(SidecarMessageType.RegisterEnd, cancellationToken).ConfigureAwait(false);

        // Registration is the capability boundary AND the content transfer: UI modules and
        // resources carry their payload inline, verified and cached here so later opens read
        // locally. A UI module that references resources missing from this registration is
        // rejected.
        foreach (SidecarRegistrationEntry entry in entries)
        {
            if (entry.Kind == SidecarRegistrationKind.UiModule)
            {
                SidecarRegistrationItem declaration = declarations[entry.SemanticId];
                foreach (string required in declaration.RequiredResources?
                             .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         ?? [])
                {
                    if (!entries.Any(candidate =>
                            candidate.Kind == SidecarRegistrationKind.Resource
                            && candidate.SemanticId.Equals(XsrSemanticId.Parse(required))))
                    {
                        throw Fail(
                            $"The UI module '{entry.SemanticId}' references missing resource '{required}'.");
                    }
                }
            }
        }

        _registration = new SidecarRegistrationSet(entries);
        _mirror = SidecarStateMirror.Create(
            PluginName,
            entries.Where(entry => entry.Kind == SidecarRegistrationKind.State).ToList());
        foreach (SidecarRegistrationEntry entry in entries)
        {
            SidecarRegistrationItem declaration = declarations[entry.SemanticId];
            if (entry.Kind == SidecarRegistrationKind.UiModule && declaration.Payload is { } module)
            {
                Cache.AddUiModule(entry.SemanticId, module, declaration.ContentHash!);
            }
            else if (entry.Kind == SidecarRegistrationKind.Resource && declaration.Payload is { } resource)
            {
                Cache.AddResource(entry.SemanticId, resource, declaration.ContentHash!);
            }
        }

        return _mirror;
    }

    /// <summary>
    /// Accepts STATE_SNAPSHOT_BEGIN/ITEM*/END, committing the snapshot into the fresh mirror, and
    /// then sends READY — the wire lifecycle finally carries READY as the snapshot-committed
    /// marker. Only after this returns is the mirror coherent and the session ready to activate.
    /// </summary>
    public async ValueTask AcceptStateSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Registering);
        if (_mirror is null || _registration is null)
        {
            throw Fail("The session cannot accept a snapshot before registration.");
        }

        SidecarFrame begin = await ReceiveOrFail(
            SidecarMessageType.StateSnapshotBegin,
            cancellationToken).ConfigureAwait(false);
        uint count = SidecarStateSnapshot.DecodeBegin(begin.Payload.Span);
        List<SidecarRegistrationEntry> stateEntries = _registration.Entries
            .Where(entry => entry.Kind == SidecarRegistrationKind.State)
            .ToList();
        Dictionary<uint, (SidecarRegistrationEntry Entry, byte[] EncodedValue)> collected = [];

        // Collect and validate the whole snapshot before touching the mirror: unknown
        // contracts, duplicates, missing states, and codec mismatches all fail the session
        // terminally without any mutation. The commit below is therefore atomic against this
        // validated set.
        try
        {
            for (uint index = 0; index < count; index++)
            {
                SidecarFrame itemFrame = await ReceiveOrFail(
                    SidecarMessageType.StateSnapshotItem,
                    cancellationToken).ConfigureAwait(false);
                (uint contractId, byte[] encodedValue) = SidecarStateSnapshot.DecodeItem(itemFrame.Payload.Span);
                SidecarRegistrationEntry? entry = stateEntries.FirstOrDefault(
                    candidate => candidate.ContractId == contractId);
                if (entry is null)
                {
                    throw Fail($"The snapshot references unknown state contract {contractId}.");
                }

                if (collected.ContainsKey(contractId))
                {
                    throw Fail($"The snapshot declares state contract {contractId} twice.");
                }

                try
                {
                    SidecarValueCodecs.Validate(entry.CodecId, encodedValue);
                }
                catch (SidecarProtocolException exception)
                {
                    throw Fail(exception.Message);
                }

                collected[contractId] = (entry, encodedValue);
            }

            _ = await ReceiveOrFail(SidecarMessageType.StateSnapshotEnd, cancellationToken)
                .ConfigureAwait(false);

            if (collected.Count != stateEntries.Count)
            {
                IEnumerable<uint> missing = stateEntries
                    .Select(entry => entry.ContractId)
                    .Where(id => !collected.ContainsKey(id));
                throw Fail(
                    $"The snapshot is missing states {string.Join(", ", missing)}; a coherent snapshot must cover every declared state.");
            }
        }
        catch (SidecarProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Fail($"The snapshot failed: {exception.Message}");
        }

        foreach ((SidecarRegistrationEntry entry, byte[] encodedValue) in collected.Values)
        {
            _mirror.PublishFromWire(entry, encodedValue);
        }

        await _connection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Ready,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()),
            cancellationToken).ConfigureAwait(false);
        Transition(SidecarSessionState.Ready);
    }

    /// <summary>
    /// Sends ACTIVATE. The sidecar transitions to runtime behavior; the session becomes active.
    /// </summary>
    public async ValueTask ActivateAsync(CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Ready);
        if (_mirror is null)
        {
            throw Fail("The session cannot activate before registration.");
        }

        await _connection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Activate,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()),
            cancellationToken).ConfigureAwait(false);
        Transition(SidecarSessionState.Active);
    }

    /// <summary>
    /// Sends DEACTIVATE, returning the session to ready without re-registration. Runtime
    /// behavior stops on the sidecar.
    /// </summary>
    public async ValueTask DeactivateAsync(CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Active);
        await _connection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Deactivate,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()),
            cancellationToken).ConfigureAwait(false);
        Transition(SidecarSessionState.Ready);
    }

    /// <summary>
    /// Sends SHUTDOWN and closes the connection.
    /// </summary>
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _connection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Shutdown,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()),
            cancellationToken).ConfigureAwait(false);
        Transition(SidecarSessionState.Closed);
        _connection.Close();
    }

    public void Dispose() => _connection.Dispose();

    private async ValueTask<SidecarFrame> ReceiveOrFail(
        SidecarMessageType expected,
        CancellationToken cancellationToken)
    {
        SidecarFrame frame = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame.MessageType != expected)
        {
            throw Fail($"The session expected {expected} but received {frame.MessageType}.");
        }

        return frame;
    }

    private void Transition(SidecarSessionState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        try
        {
            _observer?.OnStateChanged(state);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Session progression must not be changed by a diagnostics observer failure.
        }
    }

    private SidecarProtocolException Fail(string message)
    {
        lock (_gate)
        {
            _state = SidecarSessionState.Failed;
            _failureReason = message;
        }

        Transition(SidecarSessionState.Failed);
        _connection.Close();
        return new SidecarProtocolException(message);
    }

    private void ThrowState(SidecarSessionState expected)
    {
        SidecarSessionState state = State;
        if (state == SidecarSessionState.Failed)
        {
            throw new InvalidOperationException(
                $"The sidecar session failed: {_failureReason ?? "unknown reason"}.");
        }

        if (state != expected)
        {
            throw new InvalidOperationException(
                $"The sidecar session is {state}; this operation requires {expected}.");
        }
    }
}
