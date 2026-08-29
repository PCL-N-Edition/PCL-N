using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;
using PCL.Xsr;

namespace PCL.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask PendingTableStaysBounded()
    {
        (SidecarHostSession session, SidecarConnection plugin, _, Task loop) =
            await ActivatedSession(maxPending: 4);

        // Five exchanges against a registered command: the fifth rejects with backpressure.
        Task<XsrResult>[] tasks =
        [
            session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask(),
            session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask(),
            session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask(),
            session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask(),
            session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask(),
        ];
        XsrResult rejected = await tasks[4];
        AssertFalse(rejected.IsSuccess);
        AssertEqual(XsrRuntimeErrors.BackpressureCode, rejected.Error!.Code);

        // The session's own receive loop completes the four pending exchanges; the plugin
        // answers each request with a success result.
        for (int index = 0; index < 4; index++)
        {
            SidecarFrame request = await DataPlaneReceiveAsync(plugin);
            await plugin.SendAsync(Result(request, success: true, value: string.Empty, error: null));
        }

        AssertTrue((await tasks[0]).IsSuccess);
        AssertTrue((await tasks[3]).IsSuccess);
        _ = loop;
    }

    private static async ValueTask UnregisteredCommandsRejectLocally()
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        using SidecarConnection hostConnection = new(hostStream);
        using SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "TestPlugin");
        await CompleteHandshake(session, pluginConnection);

        ValueTask<SidecarStateMirror> registration = session.AcceptRegistrationAsync();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeBegin(1)));
        await pluginConnection.SendAsync(Item(SidecarRegistrationKind.Command, "plugin.known"));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await registration;
        // Snapshot + READY before activation: an empty snapshot (0 items) is coherent.
        ValueTask snapshot = session.AcceptStateSnapshotAsync();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateSnapshotBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarStateSnapshot.EncodeBegin(0)));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateSnapshotEnd,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await snapshot;
        SidecarFrame ready = await DataPlaneReceiveAsync(pluginConnection);
        AssertEqual(SidecarMessageType.Ready, ready.MessageType);
        await session.ActivateAsync();
        await DataPlaneReceiveAsync(pluginConnection); // drain ACTIVATE

        // An unregistered semantic is a capability violation: local reject, zero wire bytes.
        XsrResult result = await session.SendCommandAsync(XsrSemanticId_Parse("plugin.unknown"));
        AssertFalse(result.IsSuccess);
        AssertEqual(XsrRuntimeErrors.RouteNotFoundCode, result.Error!.Code);
        AssertEqual(0, session.PendingCount);

        // Prove nothing reached the wire: any read attempt on the plugin side times out with
        // zero bytes consumed (the loopback delivers EOF only on close, never silence).
        byte[] probe = new byte[1];
        bool timedOut = false;
        try
        {
            await pluginStream.ReadAsync(probe).AsTask().WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        AssertTrue(timedOut);
    }
}
