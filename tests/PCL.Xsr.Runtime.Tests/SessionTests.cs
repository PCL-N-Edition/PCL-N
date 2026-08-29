using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask SessionCompletesTheLockedLifecycle()
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        using SidecarConnection hostConnection = new(hostStream);
        using SidecarConnection pluginConnection = new(pluginStream);
        List<SidecarSessionState> transitions = [];
        SidecarHostSession session = new(
            hostConnection,
            "TestPlugin",
            new RecordingSessionObserver(transitions));

        ValueTask handshake = session.HandshakeAsync();
        SidecarFrame hello = await SessionReceiveAsync(pluginConnection);
        AssertEqual(SidecarMessageType.Hello, hello.MessageType);
        AssertEqual((uint)SidecarProtocol.Version, SidecarHandshake.DecodeHello(hello.Payload.Span).ProtocolVersion);
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Welcome,
            SidecarFrameTraits.None,
            hello.CorrelationId,
            SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid())));
        await handshake;

        Task<SidecarStateMirror> registration = session.AcceptRegistrationAsync().AsTask();
        // The plugin drives registration: BEGIN, one ITEM per declaration, END.
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeBegin(4)));
        AssertEqual(4u, SidecarRegistration.DecodeBegin(
            SidecarRegistration.EncodeBegin(4)));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.Command, "plugin.download.start"));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.Query, "plugin.download.status"));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.State, "plugin.download.progress"));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.Event, "plugin.download.completed"));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        SidecarStateMirror mirror = await registration;

        // Registration alone does not make the session ready: the sidecar must deliver the
        // state snapshot, and READY comes back on the wire only after it commits.
        AssertEqual(SidecarSessionState.Registering, session.State);
        ValueTask snapshot = session.AcceptStateSnapshotAsync();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateSnapshotBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarStateSnapshot.EncodeBegin(1)));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateSnapshotItem,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarStateSnapshot.EncodeItem(1, "0"u8.ToArray())));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateSnapshotEnd,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await snapshot;
        SidecarFrame ready = await SessionReceiveAsync(pluginConnection);
        AssertEqual(SidecarMessageType.Ready, ready.MessageType);
        AssertEqual(SidecarSessionState.Ready, session.State);

        // The snapshot committed before activation: the mirrored cell is coherent and
        // available already.
        XsrStateId snapshotProgress = mirror.TryResolve(XsrSemanticId.Parse("plugin.download.progress"))
            ?? throw new InvalidOperationException("The mirrored state is missing.");
        AssertEqual("0", mirror.Store.Read<string>(snapshotProgress).Value);
        AssertTrue(mirror.Store.Read<string>(snapshotProgress).IsAvailable);
        AssertEqual(1, mirror.Store.Count);
        XsrStateId progress = mirror.TryResolve(XsrSemanticId.Parse("plugin.download.progress"))
            ?? throw new InvalidOperationException("The mirrored state is missing.");
        // The snapshot published revision 1 before READY; the mirror is coherent, not empty.
        AssertEqual(1L, mirror.Store.Read<string>(progress).Revision);
        AssertEqual(1, session.Registration!.Commands.Count());
        AssertEqual(1, session.Registration!.Queries.Count());
        AssertEqual(1, session.Registration.Events.Count());

        await session.ActivateAsync();
        AssertEqual(SidecarSessionState.Active, session.State);
        SidecarFrame activate = await SessionReceiveAsync(pluginConnection);
        AssertEqual(SidecarMessageType.Activate, activate.MessageType);

        await session.DeactivateAsync();
        SidecarFrame deactivate = await SessionReceiveAsync(pluginConnection);
        AssertEqual(SidecarMessageType.Deactivate, deactivate.MessageType);

        await session.ShutdownAsync();
        SidecarFrame shutdown = await SessionReceiveAsync(pluginConnection);
        AssertEqual(SidecarMessageType.Shutdown, shutdown.MessageType);
        AssertEqual(SidecarConnectionState.Closed, hostConnection.State);

        SessionAssertSequence(
            new[]
            {
                SidecarSessionState.Registering,
                SidecarSessionState.Ready,
                SidecarSessionState.Active,
                SidecarSessionState.Ready,
                SidecarSessionState.Closed,
            },
            transitions.ToArray());
    }

    private static async ValueTask SessionHandshakeRejectsVersionMismatch()
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        using SidecarConnection hostConnection = new(hostStream);
        using SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "TestPlugin");

        ValueTask handshake = session.HandshakeAsync();
        SidecarFrame hello = await SessionReceiveAsync(pluginConnection);
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Welcome,
            SidecarFrameTraits.None,
            hello.CorrelationId,
            SidecarHandshake.EncodeWelcome(SidecarProtocol.Version + 5, Guid.NewGuid())));

        await AssertThrowsAsync<SidecarProtocolException>(() => handshake.AsTask());
        AssertEqual(SidecarSessionState.Failed, session.State);
        AssertEqual(SidecarConnectionState.Closed, hostConnection.State);
    }

    private static async ValueTask SessionRegistrationRejectsDuplicates()
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        using SidecarConnection hostConnection = new(hostStream);
        using SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "TestPlugin");
        await CompleteHandshake(session, pluginConnection);

        Task<SidecarStateMirror> registration = session.AcceptRegistrationAsync().AsTask();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeBegin(2)));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.State, "plugin.progress"));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.State, "plugin.progress"));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));

        await AssertThrowsAsync<SidecarProtocolException>(() => registration);
        AssertEqual(SidecarSessionState.Failed, session.State);
        AssertTrue(session.Mirror is null);

        // A failed session is terminal.
        await AssertThrowsAsync<InvalidOperationException>(() => session.ActivateAsync().AsTask());
    }

    private static async ValueTask SessionFailsOnUnexpectedMessage()
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        using SidecarConnection hostConnection = new(hostStream);
        using SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "TestPlugin");
        await CompleteHandshake(session, pluginConnection);

        Task<SidecarStateMirror> registration = session.AcceptRegistrationAsync().AsTask();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Event,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));

        await AssertThrowsAsync<SidecarProtocolException>(() => registration);
        AssertEqual(SidecarSessionState.Failed, session.State);
    }

    private static async ValueTask CompleteHandshake(SidecarHostSession session, SidecarConnection plugin)
    {
        ValueTask handshake = session.HandshakeAsync();
        SidecarFrame hello = await SessionReceiveAsync(plugin);
        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Welcome,
            SidecarFrameTraits.None,
            hello.CorrelationId,
            SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid())));
        await handshake;
    }

    private static SidecarFrame Item(SidecarRegistrationKind kind, string semanticId) => new(
        SidecarProtocol.Version,
        SidecarMessageType.RegisterItem,
        SidecarFrameTraits.None,
        SidecarCorrelationId.Create(),
        SidecarRegistration.EncodeItem(new SidecarRegistrationItem(kind, semanticId, 0, 0)));

    private static Task<SidecarFrame> SessionReceiveAsync(SidecarConnection connection) =>
        connection.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static void SessionAssertSequence<T>(T[] expected, T[] actual)
        where T : notnull
    {
        if (expected.Length != actual.Length
            || !expected.Zip(actual, (left, right) => EqualityComparer<T>.Default.Equals(left, right)).All(equal => equal))
        {
            throw new InvalidOperationException(
                $"Expected sequence [{string.Join(", ", expected)}] but received [{string.Join(", ", actual)}].");
        }
    }

    private sealed class RecordingSessionObserver(List<SidecarSessionState> transitions) : ISidecarSessionObserver
    {
        public void OnStateChanged(SidecarSessionState state) => transitions.Add(state);
    }
}
