using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask CommandForwardsAndCompletes()
    {
        (SidecarHostSession session, SidecarConnection plugin, _, _) = await ActivatedSession();

        ValueTask<XsrResult> sent = session.SendCommandAsync(
            XsrSemanticId_Parse("plugin.download.start"),
            "http://example.com");

        SidecarFrame request = await DataPlaneReceiveAsync(plugin);
        AssertEqual(SidecarMessageType.CommandRequest, request.MessageType);
        (XsrSemanticId semantic, string argument) = SidecarDataPlane.DecodeRequest(request.Payload.Span);
        AssertEqual("plugin.download.start", semantic.Value);
        AssertEqual("http://example.com", argument);

        await plugin.SendAsync(Result(request, success: true, value: "started", error: null));
        XsrResult result = await sent;

        AssertTrue(result.IsSuccess);
    }

    private static async ValueTask CommandFailureCarriesStableCode()
    {
        (SidecarHostSession session, SidecarConnection plugin, _, _) = await ActivatedSession();

        ValueTask<XsrResult> sent = session.SendCommandAsync(XsrSemanticId_Parse("plugin.save"));

        SidecarFrame request = await DataPlaneReceiveAsync(plugin);
        await plugin.SendAsync(Result(request, success: false, value: string.Empty, error: "plugin.disk_full"));

        XsrResult result = await sent;
        AssertFalse(result.IsSuccess);
        AssertEqual("plugin.disk_full", result.Error!.Code.Value);
    }

    private static async ValueTask QueryReturnsTheSidecarValue()
    {
        (SidecarHostSession session, SidecarConnection plugin, _, _) = await ActivatedSession();

        Task<XsrResult<string>> sent = session
            .SendQueryAsync(XsrSemanticId_Parse("plugin.download.status"))
            .AsTask();
        SidecarFrame request = await DataPlaneReceiveAsync(plugin);
        AssertEqual(SidecarMessageType.QueryRequest, request.MessageType);
        await plugin.SendAsync(Result(request, success: true, value: "running", error: null));

        XsrResult<string> result = await sent;
        AssertTrue(result.IsSuccess);
        AssertEqual("running", result.Value);
    }

    private static async ValueTask CommandTimeoutReturnsStableErrorAndReleasesPending()
    {
        (SidecarHostSession session, SidecarConnection plugin, _, _) = await ActivatedSession();

        XsrResult result = await session.SendCommandAsync(
            XsrSemanticId_Parse("plugin.never"),
            timeout: TimeSpan.FromMilliseconds(50));

        AssertFalse(result.IsSuccess);
        AssertEqual(XsrRuntimeErrors.TimedOutCode, result.Error!.Code);
        AssertEqual(0, session.PendingCount);
    }

    private static async ValueTask StateDeltasPublishIntoTheMirror()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, Task loop) =
            await ActivatedSession();
        XsrStateId progress = mirror.TryResolve(XsrSemanticId_Parse("plugin.download.progress"))
            ?? throw new InvalidOperationException("The mirrored state is missing.");

        await plugin.SendAsync(Delta("plugin.download.progress", "50"));
        await WaitUntil(() => mirror.Store.Read<string>(progress).Revision == 1);
        XsrStateValue<string> value = mirror.Store.Read<string>(progress);
        AssertEqual("50", value.Value);
        AssertTrue(value.IsAvailable);

        await plugin.SendAsync(Delta("plugin.download.progress", "100"));
        await WaitUntil(() => mirror.Store.Read<string>(progress).Revision == 2);
        AssertEqual("100", mirror.Store.Read<string>(progress).Value);

        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Shutdown,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual(SidecarSessionState.Closed, session.State);
    }

    private static readonly string[] ExpectedEvents =
        ["event-0", "event-1", "event-2", "event-3", "event-4"];

    private static async ValueTask EventsDeliverInOrderWithoutCoalescing()
    {
        (SidecarHostSession session, SidecarConnection plugin, _, Task loop) = await ActivatedSession();
        List<string> events = [];
        session.AttachEventObserver(new RecordingDataEventObserver(events));

        for (int index = 0; index < 5; index++)
        {
            await plugin.SendAsync(Event("plugin.log", $"event-{index}"));
        }

        await WaitUntil(() => events.Count == 5);
        SessionAssertSequence(ExpectedEvents, events.ToArray());

        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Shutdown,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async ValueTask CrashFailsSessionAndMarksMirrorUnavailable()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, Task loop) =
            await ActivatedSession();
        XsrStateId progress = mirror.TryResolve(XsrSemanticId_Parse("plugin.download.progress"))
            ?? throw new InvalidOperationException("The mirrored state is missing.");

        await plugin.SendAsync(Delta("plugin.download.progress", "80"));
        await WaitUntil(() => mirror.Store.Read<string>(progress).Revision == 1);

        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Crash,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        AssertEqual(SidecarSessionState.Failed, session.State);
        XsrStateValue<string> stale = mirror.Store.Read<string>(progress);
        AssertEqual("80", stale.Value);
        AssertEqual(XsrStateAvailability.Unavailable, stale.Availability);
    }

    private static async ValueTask StreamFailureMarksSessionFailed()
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        using SidecarConnection hostConnection = new(hostStream);
        using SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "TestPlugin");
        await CompleteHandshake(session, pluginConnection);

        // RegisterOneState drives the whole registration sequence itself.
        SidecarStateMirror mirror = await RegisterOneState(session, pluginConnection);
        await session.ActivateAsync();
        XsrStateId progress = mirror.TryResolve(XsrSemanticId_Parse("plugin.download.progress"))
            ?? throw new InvalidOperationException("The mirrored state is missing.");

        Task loop = session.RunReceiveLoopAsync().AsTask();
        await pluginConnection.SendAsync(Delta("plugin.download.progress", "5"));
        await WaitUntil(() => mirror.Store.Read<string>(progress).Revision == 1);

        pluginStream.Close();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        AssertEqual(SidecarSessionState.Failed, session.State);
        AssertTrue(session.FailureReason is not null);
    }

    private static async ValueTask ReconnectReplacesMirrorWithFreshSnapshot()
    {
        // Session one: publish progress, then crash. The mirror keeps the value but goes stale.
        XsrSemanticId semantic = XsrSemanticId_Parse("plugin.download.progress");

        (SidecarHostSession first, SidecarConnection firstPlugin, SidecarStateMirror firstMirror, Task firstLoop) =
            await ActivatedSession();
        XsrStateId firstProgress = firstMirror.TryResolve(semantic)
            ?? throw new InvalidOperationException("The mirrored state is missing.");

        await firstPlugin.SendAsync(Delta("plugin.download.progress", "80"));
        await WaitUntil(() => firstMirror.Store.Read<string>(firstProgress).Revision == 1);
        await firstPlugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Crash,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await firstLoop.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual(XsrStateAvailability.Unavailable, firstMirror.Store.Read<string>(firstProgress).Availability);

        // Session two: a fresh mirror with the same semantic ID takes a coherent snapshot
        // before activation; the old mirror is untouched.
        (SidecarHostSession second, SidecarConnection secondPlugin, SidecarStateMirror secondMirror, Task secondLoop) =
            await ActivatedSession();
        XsrStateId secondProgress = secondMirror.TryResolve(semantic)
            ?? throw new InvalidOperationException("The mirrored state is missing.");

        await secondPlugin.SendAsync(Delta("plugin.download.progress", "95"));
        await WaitUntil(() => secondMirror.Store.Read<string>(secondProgress).Revision == 1);
        AssertEqual("95", secondMirror.Store.Read<string>(secondProgress).Value);
        AssertTrue(secondMirror.Store.Read<string>(secondProgress).IsAvailable);

        // The old mirror retains its last value, unavailable, untouched by the new session.
        AssertEqual("80", firstMirror.Store.Read<string>(firstProgress).Value);
        AssertEqual(XsrStateAvailability.Unavailable, firstMirror.Store.Read<string>(firstProgress).Availability);

        await secondPlugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Shutdown,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await secondLoop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async ValueTask PendingBackpressureRejectsWithStableError()
    {
        (SidecarHostSession session, SidecarConnection plugin, _, Task loop) = await ActivatedSession(maxPending: 2);

        ValueTask<XsrResult> first = session.SendCommandAsync(XsrSemanticId_Parse("plugin.a"));
        ValueTask<XsrResult> second = session.SendCommandAsync(XsrSemanticId_Parse("plugin.b"));
        ValueTask<XsrResult> third = session.SendCommandAsync(XsrSemanticId_Parse("plugin.c"));

        XsrResult rejected = await third;
        AssertFalse(rejected.IsSuccess);
        AssertEqual(XsrRuntimeErrors.BackpressureCode, rejected.Error!.Code);

        // Drain the two pending commands so nothing dangles.
        for (int index = 0; index < 2; index++)
        {
            SidecarFrame request = await DataPlaneReceiveAsync(plugin);
            await plugin.SendAsync(Result(request, success: true, value: string.Empty, error: null));
        }

        AssertTrue((await first).IsSuccess);
        AssertTrue((await second).IsSuccess);

        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Shutdown,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static readonly byte[] RegisterEndPayload = [];

    private static async ValueTask<(SidecarHostSession, SidecarConnection, SidecarStateMirror, Task)> ActivatedSession(
        int maxPending = 1024)
    {
        (SidecarHostSession session, SidecarConnection plugin) = await HandshakeAndRegister(maxPending);
        await session.ActivateAsync();
        await DataPlaneReceiveAsync(plugin); // drain the ACTIVATE frame
        SidecarStateMirror mirror = session.Mirror!;
        Task loop = session.RunReceiveLoopAsync().AsTask();
        return (session, plugin, mirror, loop);
    }

    private static async ValueTask<(SidecarHostSession, SidecarConnection)> HandshakeAndRegister(
        int maxPending = 1024)
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        SidecarConnection hostConnection = new(hostStream);
        SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "TestPlugin", maxPending: maxPending);
        await CompleteHandshake(session, pluginConnection);

        ValueTask<SidecarStateMirror> registration = session.AcceptRegistrationAsync();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeBegin(3)));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.Command, "plugin.download.start"));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.State, "plugin.download.progress"));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.Event, "plugin.download.completed"));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            RegisterEndPayload));
        _ = await registration;
        return (session, pluginConnection);
    }

    private static async Task<SidecarStateMirror> RegisterOneState(
        SidecarHostSession session,
        SidecarConnection pluginConnection)
    {
        ValueTask<SidecarStateMirror> registration = session.AcceptRegistrationAsync();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeBegin(1)));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.State, "plugin.download.progress"));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        return await registration;
    }

    private static SidecarFrame Result(
        SidecarFrame request,
        bool success,
        string value,
        string? error) => new(
        SidecarProtocol.Version,
        request.MessageType == SidecarMessageType.CommandRequest
            ? SidecarMessageType.CommandResult
            : SidecarMessageType.QueryResult,
        SidecarFrameTraits.Final,
        request.CorrelationId,
        SidecarDataPlane.EncodeResult(success, value, error));

    private static SidecarFrame Delta(string semantic, string value) => new(
        SidecarProtocol.Version,
        SidecarMessageType.StateDelta,
        SidecarFrameTraits.None,
        SidecarCorrelationId.Create(),
        SidecarDataPlane.EncodeStateDelta(XsrSemanticId.Parse(semantic), value));

    private static SidecarFrame Event(string semantic, string payload) => new(
        SidecarProtocol.Version,
        SidecarMessageType.Event,
        SidecarFrameTraits.None,
        SidecarCorrelationId.Create(),
        SidecarDataPlane.EncodeEvent(XsrSemanticId.Parse(semantic), payload));

    private static async ValueTask WaitUntil(Func<bool> condition, int attempts = 200)
    {
        for (int index = 0; index < attempts; index++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected condition was not reached in time.");
    }

    private static Task<SidecarFrame> DataPlaneReceiveAsync(SidecarConnection connection) =>
        connection.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static XsrSemanticId XsrSemanticId_Parse(string value) => XsrSemanticId.Parse(value);

    private sealed class RecordingDataEventObserver(List<string> events) : ISidecarSessionEventObserver
    {
        public void OnEvent(XsrSemanticId semantic, string payload) => events.Add(payload);
    }
}
