using System.Security.Cryptography;
using System.Text;
using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask SnapshotDuplicateStateRejected()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, _) =
            await RegisteredTwoStates();

        ValueTask snapshot = session.AcceptStateSnapshotAsync();
        await SnapshotFrames(plugin, [
            (1u, "10"u8.ToArray()),
            (1u, "20"u8.ToArray()),
        ]);
        await AssertThrowsAsync<SidecarProtocolException>(() => snapshot.AsTask());

        AssertEqual(SidecarSessionState.Failed, session.State);
        XsrStateId enabled = mirror.TryResolve(XsrSemanticId_Parse("state.enabled"))
            ?? throw new InvalidOperationException("missing state.enabled");
        XsrStateId count = mirror.TryResolve(XsrSemanticId_Parse("state.count"))
            ?? throw new InvalidOperationException("missing state.count");
        AssertFalse(mirror.Store.Read<bool>(enabled).IsAvailable);
        AssertEqual(0L, mirror.Store.Read<int>(count).Revision);
    }

    private static async ValueTask SnapshotMissingStateRejected()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, _) =
            await RegisteredTwoStates();

        // BEGIN declares two items but only one arrives before END.
        ValueTask snapshot = session.AcceptStateSnapshotAsync();
        await SnapshotFrames(plugin, [(1u, "10"u8.ToArray())], declaredCount: 2);
        await AssertThrowsAsync<SidecarProtocolException>(() => snapshot.AsTask());

        AssertEqual(SidecarSessionState.Failed, session.State);
        XsrStateId enabled = mirror.TryResolve(XsrSemanticId_Parse("state.enabled"))
            ?? throw new InvalidOperationException("missing state.enabled");
        XsrStateId count = mirror.TryResolve(XsrSemanticId_Parse("state.count"))
            ?? throw new InvalidOperationException("missing state.count");
        AssertEqual(0L, mirror.Store.Read<bool>(enabled).Revision);
        AssertEqual(0L, mirror.Store.Read<int>(count).Revision);
    }

    private static async ValueTask SnapshotFailureDoesNotMutateMirror()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, _) =
            await RegisteredTwoStates();

        // An unknown contract in the middle of the snapshot fails it; the mirror must not have
        // applied anything, even the valid item that preceded the failure.
        ValueTask snapshot = session.AcceptStateSnapshotAsync();
        await SnapshotFrames(plugin, [
            (1u, "10"u8.ToArray()),
            (99, "garbage"u8.ToArray()),
        ]);
        await AssertThrowsAsync<SidecarProtocolException>(() => snapshot.AsTask());

        XsrStateId count = mirror.TryResolve(XsrSemanticId_Parse("state.count"))
            ?? throw new InvalidOperationException("missing state.count");
        XsrStateId enabled = mirror.TryResolve(XsrSemanticId_Parse("state.enabled"))
            ?? throw new InvalidOperationException("missing state.enabled");
        AssertEqual(0L, mirror.Store.Read<int>(count).Revision);
        AssertEqual(XsrStateAvailability.Unavailable, mirror.Store.Read<int>(count).Availability);
        AssertEqual(0L, mirror.Store.Read<bool>(enabled).Revision);
    }

    private static async ValueTask SnapshotCommitIsAtomic()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, _) =
            await RegisteredTwoStates();

        await SnapshotAll(
            session,
            plugin,
            (1u, EncodeTyped(SidecarValueCodecs.I32, 42)),
            (2u, EncodeTyped(SidecarValueCodecs.Bool, true)));
        // SnapshotAll already drained READY; the mirror is now coherent.

        // The whole validated snapshot lands as one coherent commit: every cell available at
        // revision 1 with its value.
        XsrStateId count = mirror.TryResolve(XsrSemanticId_Parse("state.count"))
            ?? throw new InvalidOperationException("missing state.count");
        XsrStateId enabled = mirror.TryResolve(XsrSemanticId_Parse("state.enabled"))
            ?? throw new InvalidOperationException("missing state.enabled");
        AssertEqual(42, mirror.Store.Read<int>(count).Value);
        AssertEqual(1L, mirror.Store.Read<int>(count).Revision);
        AssertTrue(mirror.Store.Read<bool>(enabled).Value);
        AssertEqual(1L, mirror.Store.Read<bool>(enabled).Revision);
        await session.ActivateAsync();
        Task loop = session.RunReceiveLoopAsync().AsTask();

        // Typed deltas continue on the same cells through the wire codec.
        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateDelta,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarDataPlane.EncodeStateDelta(1u, EncodeTyped(SidecarValueCodecs.I32, 99))));
        await WaitUntil(() => mirror.Store.Read<int>(count).Revision == 2);
        AssertEqual(99, mirror.Store.Read<int>(count).Value);

        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Shutdown,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual(SidecarSessionState.Closed, session.State);
    }

    private static async ValueTask TypedBoolStateRoundTrip()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, _) =
            await RegisteredTwoStates();
        XsrStateId enabled = mirror.TryResolve(XsrSemanticId_Parse("state.enabled"))
            ?? throw new InvalidOperationException("missing state.enabled");

        await SnapshotAll(
            session,
            plugin,
            (1u, EncodeTyped(SidecarValueCodecs.I32, 0)),
            (2u, EncodeTyped(SidecarValueCodecs.Bool, true)));
        await session.ActivateAsync();
        Task loop = session.RunReceiveLoopAsync().AsTask();
        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateDelta,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarDataPlane.EncodeStateDelta(2u, EncodeTyped(SidecarValueCodecs.Bool, true))));
        await WaitUntil(() => mirror.Store.Read<bool>(enabled).Revision == 2);
        AssertTrue(mirror.Store.Read<bool>(enabled).Value);
        await session.ShutdownAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async ValueTask TypedIntStateRoundTrip()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, _) =
            await RegisteredTwoStates();
        XsrStateId count = mirror.TryResolve(XsrSemanticId_Parse("state.count"))
            ?? throw new InvalidOperationException("missing state.count");

        await SnapshotAll(
            session,
            plugin,
            (1u, EncodeTyped(SidecarValueCodecs.I32, 0)),
            (2u, EncodeTyped(SidecarValueCodecs.Bool, false)));
        await session.ActivateAsync();
        Task loop = session.RunReceiveLoopAsync().AsTask();
        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateDelta,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarDataPlane.EncodeStateDelta(1u, EncodeTyped(SidecarValueCodecs.I32, -7))));
        await WaitUntil(() => mirror.Store.Read<int>(count).Revision == 2);
        AssertEqual(-7, mirror.Store.Read<int>(count).Value);
        await session.ShutdownAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async ValueTask GeneratedDtoRoundTrip()
    {
        // Codec 6 is the generated-DTO container: the host treats the blob as schema-contracted
        // opaque bytes whose field encode/decode the generator emits. The round trip through
        // registration, snapshot, delta, and mirror must preserve the blob exactly.
        SidecarPayloadWriter dto = new();
        dto.WriteUInt32(1, 7);
        dto.WriteString(2, "dto-field");
        dto.WriteBoolean(3, true);
        byte[] blob = dto.ToArray();

        SidecarHostSession session;
        SidecarConnection plugin;
        SidecarStateMirror mirror;
        (session, plugin, mirror, _) = await ActivatedSingleState(SidecarValueCodecs.GeneratedDto);
        Task loop = session.RunReceiveLoopAsync().AsTask();

        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateDelta,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarDataPlane.EncodeStateDelta(1, blob)));
        XsrStateId state = mirror.TryResolve(XsrSemanticId_Parse("state.dto"))
            ?? throw new InvalidOperationException("missing state.dto");
        await WaitUntil(() => mirror.Store.Read<byte[]>(state).Revision == 2);
        AssertTrue(mirror.Store.Read<byte[]>(state).Value.AsSpan().SequenceEqual(blob));
        await session.ShutdownAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async ValueTask CodecMismatchRejected()
    {
        (SidecarHostSession session, SidecarConnection plugin, SidecarStateMirror mirror, _) =
            await RegisteredTwoStates();

        // State contract 1 declares Int32; a 3-byte value is a codec violation and must fail
        // the snapshot without committing.
        ValueTask snapshot = session.AcceptStateSnapshotAsync();
        await SnapshotFrames(plugin, [
            (1u, [1, 2, 3]),
            (2u, [(byte)1]),
        ]);
        await AssertThrowsAsync<SidecarProtocolException>(() => snapshot.AsTask());
        AssertEqual(SidecarSessionState.Failed, session.State);
        XsrStateId count = mirror.TryResolve(XsrSemanticId_Parse("state.count"))
            ?? throw new InvalidOperationException("missing state.count");
        AssertEqual(0L, mirror.Store.Read<int>(count).Revision);
    }

    private static async ValueTask UiModulePayloadCachedAtRegistration()
    {
        byte[] module = [1, 2, 3, 4, 5];
        byte[] resource = [9, 8, 7];
        (SidecarHostSession session, SidecarConnection _, _, _, _) =
            await ActivatedSessionWithContent(module, resource);

        AssertTrue(session.Cache.TryOpenUiModule(
            XsrSemanticId_Parse("plugin.ui.main"), out byte[]? cached));
        AssertTrue(module.AsSpan().SequenceEqual(cached!));
        AssertTrue(session.Cache.TryGetResource(SHA256.HashData(resource), out byte[]? res));
        AssertTrue(resource.AsSpan().SequenceEqual(res!));
    }

    private static async ValueTask UiModuleOpenPerformsZeroIpc()
    {
        byte[] module = [1, 2, 3, 4, 5];
        (SidecarHostSession session, SidecarConnection _, _, _, SidecarLoopbackStream pluginStream) =
            await ActivatedSessionWithContent(module);
        _ = pluginStream;

        // Opening the registered page reads the host cache. Prove zero IPC: the plugin side
        // sees silence while the module opens repeatedly.
        XsrSemanticId page = XsrSemanticId_Parse("plugin.ui.main");
        for (int index = 0; index < 3; index++)
        {
            AssertTrue(session.Cache.TryOpenUiModule(page, out byte[]? cached));
            AssertTrue(module.AsSpan().SequenceEqual(cached!));
        }

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

    private static async ValueTask ResourceHashDeduplicatesTransfer()
    {
        // Two resources with identical content: the host stores one content-addressed blob and
        // verifies both hashes.
        byte[] shared = [5, 5, 5, 5];
        (SidecarHostSession session, SidecarConnection _, SidecarStateMirror _, _, _) =
            await ActivatedSessionWithContent(resourceA: shared, resourceB: shared);

        AssertEqual(1, session.Cache.ResourceCount);
        AssertTrue(session.Cache.TryGetResource(SHA256.HashData(shared), out byte[]? cached));
        AssertTrue(shared.AsSpan().SequenceEqual(cached!));
    }

    private static async ValueTask MissingResourceRejectsUiModule()
    {
        byte[] module = [1, 2, 3];
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
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterItem,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeItem(new SidecarRegistrationItem(
                SidecarRegistrationKind.UiModule,
                "plugin.ui.main",
                0,
                0,
                module,
                SHA256.HashData(module),
                "plugin.res.missing"))));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));

        await AssertThrowsAsync<SidecarProtocolException>(() => registration.AsTask());
        AssertEqual(SidecarSessionState.Failed, session.State);
        AssertTrue(session.Mirror is null);
    }

    private static async ValueTask<(SidecarHostSession, SidecarConnection, SidecarStateMirror, Task)>
        RegisteredTwoStates()
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        SidecarConnection hostConnection = new(hostStream);
        SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "TypedPlugin");
        await CompleteHandshake(session, pluginConnection);

        ValueTask<SidecarStateMirror> registration = session.AcceptRegistrationAsync();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeBegin(2)));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterItem,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeItem(new SidecarRegistrationItem(
                SidecarRegistrationKind.State,
                "state.count",
                0,
                SidecarValueCodecs.I32))));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterItem,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeItem(new SidecarRegistrationItem(
                SidecarRegistrationKind.State,
                "state.enabled",
                0,
                SidecarValueCodecs.Bool))));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        SidecarStateMirror mirror = await registration;
        return (session, pluginConnection, mirror, Task.CompletedTask);
    }

    private static async ValueTask<(SidecarHostSession, SidecarConnection, SidecarStateMirror, Task)>
        ActivatedSingleState(uint codec)
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        SidecarConnection hostConnection = new(hostStream);
        SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "DtoPlugin");
        await CompleteHandshake(session, pluginConnection);

        ValueTask<SidecarStateMirror> registration = session.AcceptRegistrationAsync();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeBegin(1)));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterItem,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeItem(new SidecarRegistrationItem(
                SidecarRegistrationKind.State,
                "state.dto",
                0,
                codec))));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        SidecarStateMirror mirror = await registration;

        // The snapshot must cover every declared state (the DTO state), even if empty.
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
            SidecarStateSnapshot.EncodeItem(1, Array.Empty<byte>())));
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateSnapshotEnd,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await snapshot;
        await DataPlaneReceiveAsync(pluginConnection); // drain READY
        return (session, pluginConnection, mirror, Task.CompletedTask);
    }

    private static async ValueTask<(SidecarHostSession, SidecarConnection, SidecarStateMirror, Task, SidecarLoopbackStream)>
        ActivatedSessionWithContent(byte[]? resourceA = null, byte[]? resourceB = null)
    {
        (SidecarLoopbackStream hostStream, SidecarLoopbackStream pluginStream) =
            SidecarLoopbackStream.CreatePair();
        SidecarConnection hostConnection = new(hostStream);
        SidecarConnection pluginConnection = new(pluginStream);
        SidecarHostSession session = new(hostConnection, "UiPlugin");
        await CompleteHandshake(session, pluginConnection);

        List<SidecarRegistrationItem> items =
        [
            new(
                SidecarRegistrationKind.UiModule,
                "plugin.ui.main",
                0,
                0,
                resourceA is null ? [1, 2, 3, 4, 5] : [1, 2, 3, 4, 5],
                SHA256.HashData(resourceA is null ? [1, 2, 3, 4, 5] : [1, 2, 3, 4, 5])),
        ];
        if (resourceA is not null)
        {
            items.Add(new(
                SidecarRegistrationKind.Resource,
                "plugin.res.icon-a",
                0,
                0,
                resourceA,
                SHA256.HashData(resourceA)));
        }

        if (resourceB is not null)
        {
            items.Add(new(
                SidecarRegistrationKind.Resource,
                "plugin.res.icon-b",
                0,
                0,
                resourceB,
                SHA256.HashData(resourceB)));
        }

        ValueTask<SidecarStateMirror> registration = session.AcceptRegistrationAsync();
        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarRegistration.EncodeBegin((uint)items.Count)));
        foreach (SidecarRegistrationItem item in items)
        {
            await pluginConnection.SendAsync(new SidecarFrame(
                SidecarProtocol.Version,
                SidecarMessageType.RegisterItem,
                SidecarFrameTraits.None,
                SidecarCorrelationId.Create(),
                SidecarRegistration.EncodeItem(item)));
        }

        await pluginConnection.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        await registration;
        return (session, pluginConnection, session.Mirror!, Task.CompletedTask, pluginStream);
    }

    /// <summary>
    /// Sends BEGIN, the items, and END for one snapshot exchange.
    /// </summary>
    private static async ValueTask SnapshotFrames(
        SidecarConnection plugin,
        (uint ContractId, byte[] Value)[] items,
        uint? declaredCount = null)
    {
        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateSnapshotBegin,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            SidecarStateSnapshot.EncodeBegin(declaredCount ?? (uint)items.Length)));
        foreach ((uint contractId, byte[] value) in items)
        {
            await plugin.SendAsync(new SidecarFrame(
                SidecarProtocol.Version,
                SidecarMessageType.StateSnapshotItem,
                SidecarFrameTraits.None,
                SidecarCorrelationId.Create(),
                SidecarStateSnapshot.EncodeItem(contractId, value)));
        }

        await plugin.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.StateSnapshotEnd,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
    }

    private static byte[] EncodeTyped(uint codec, object value) =>
        codec switch
        {
            SidecarValueCodecs.I32 => BitConverter.GetBytes(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)),
            SidecarValueCodecs.Bool => [(bool)value ? (byte)1 : (byte)0],
            _ => throw new NotSupportedException($"The gate does not encode codec {codec}."),
        };
}
