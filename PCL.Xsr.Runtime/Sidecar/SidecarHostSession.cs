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
    /// as string cells starting unavailable; commands, queries, and events become registration
    /// entries.
    /// </summary>
    public async ValueTask<SidecarStateMirror> AcceptRegistrationAsync(CancellationToken cancellationToken = default)
    {
        ThrowState(SidecarSessionState.Registering);

        SidecarFrame begin = await ReceiveOrFail(
            SidecarMessageType.RegisterBegin,
            cancellationToken).ConfigureAwait(false);
        uint count = SidecarRegistration.DecodeBegin(begin.Payload.Span);
        List<SidecarRegistrationEntry> entries = new((int)count);

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

            entries.Add(new SidecarRegistrationEntry(item.Kind, semantic));
        }

        _ = await ReceiveOrFail(SidecarMessageType.RegisterEnd, cancellationToken).ConfigureAwait(false);

        _registration = new SidecarRegistrationSet(entries);
        _mirror = BuildMirror(entries);
        Transition(SidecarSessionState.Ready);
        return _mirror;
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
    /// Sends DEACTIVATE, returning the session to ready. Runtime behavior stops on the sidecar.
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
        Transition(SidecarSessionState.Registering);
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

    private SidecarStateMirror BuildMirror(IReadOnlyList<SidecarRegistrationEntry> entries)
    {
        XsrStateStoreBuilder builder = new();
        foreach (SidecarRegistrationEntry entry in entries.Where(entry => entry.Kind == SidecarRegistrationKind.State))
        {
            // Sidecar state values arrive as string-encoded payloads from the data plane; typed
            // mirrors come with the generated codecs.
            builder.Cell<string>(entry.SemanticId, PluginName);
        }

        return new SidecarStateMirror(PluginName, builder.Build());
    }

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
