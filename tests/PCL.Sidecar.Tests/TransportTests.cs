using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;

namespace PCL.Sidecar.Tests;

internal static partial class Program
{
    private static async ValueTask ConnectionRoundTripsFramesOverLoopback()
    {
        (SidecarLoopbackStream first, SidecarLoopbackStream second) = SidecarLoopbackStream.CreatePair();
        using SidecarConnection client = new(first);
        using SidecarConnection server = new(second);

        AssertEqual(SidecarConnectionState.Connected, client.State);

        SidecarCorrelationId correlation = SidecarCorrelationId.Create();
        SidecarPayloadWriter writer = new();
        writer.WriteString(1, "ping");
        await client.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.CommandRequest,
            SidecarFrameTraits.None,
            correlation,
            writer.ToArray()));

        SidecarFrame received = await ReceiveAsync(server);
        AssertEqual(SidecarMessageType.CommandRequest, received.MessageType);
        AssertEqual(correlation, received.CorrelationId);
        AssertEqual("ping", new SidecarPayloadReader(received.Payload.Span).ReadNext().ReadString());

        await server.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.CommandResult,
            SidecarFrameTraits.Final,
            correlation,
            Array.Empty<byte>()));
        SidecarFrame result = await ReceiveAsync(client);
        AssertEqual(SidecarMessageType.CommandResult, result.MessageType);
        AssertTrue(result.Flags.HasFlag(SidecarFrameTraits.Final));
        AssertEqual(0, result.Payload.Length);
    }

    private static async ValueTask ConcurrentSendsNeverInterleave()
    {
        (SidecarLoopbackStream first, SidecarLoopbackStream second) = SidecarLoopbackStream.CreatePair();
        using SidecarConnection client = new(first);
        using SidecarConnection server = new(second);

        const int senders = 8;
        const int perSender = 25;
        await Parallel.ForAsync(0, senders, async (sender, cancellationToken) =>
        {
            for (int index = 0; index < perSender; index++)
            {
                SidecarPayloadWriter writer = new();
                writer.WriteUInt32(1, (uint)((sender * 1000) + index));
                writer.WriteBytes(2, new byte[64]);
                await client.SendAsync(new SidecarFrame(
                    SidecarProtocol.Version,
                    SidecarMessageType.Event,
                    SidecarFrameTraits.None,
                    SidecarCorrelationId.Create(),
                    writer.ToArray()),
                    cancellationToken);
            }
        });

        HashSet<uint> seen = [];
        for (int index = 0; index < senders * perSender; index++)
        {
            SidecarFrame frame = await ReceiveAsync(server);
            AssertEqual(SidecarMessageType.Event, frame.MessageType);
            SidecarPayloadReader reader = new(frame.Payload.Span);
            AssertTrue(seen.Add(reader.ReadNext().ReadUInt32()));
            AssertEqual(64, reader.ReadNext().ReadBytes().Length);
        }

        AssertEqual(senders * perSender, seen.Count);
    }

    private static async ValueTask ProtocolFailuresMoveTheConnectionToFailed()
    {
        (SidecarLoopbackStream first, SidecarLoopbackStream second) = SidecarLoopbackStream.CreatePair();
        using SidecarConnection client = new(first);
        using SidecarConnection server = new(second);

        // A full fake header with a broken magic poisons the stream deterministically.
        first.Write(new byte[40]);

        await AssertThrowsAsync<SidecarProtocolException>(() => ReceiveAsync(server));
        AssertEqual(SidecarConnectionState.Failed, server.State);
        AssertTrue(server.FailureReason is not null);

        AssertThrows<InvalidOperationException>(
            () => server.ReceiveAsync().AsTask().GetAwaiter().GetResult());
    }

    private static async ValueTask PeerCloseEndsReceiveWithStreamEnd()
    {
        (SidecarLoopbackStream first, SidecarLoopbackStream second) = SidecarLoopbackStream.CreatePair();
        using SidecarConnection client = new(first);
        using SidecarConnection server = new(second);

        await client.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Hello,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            Array.Empty<byte>()));
        first.Close();

        SidecarFrame frame = await ReceiveAsync(server);
        AssertEqual(SidecarMessageType.Hello, frame.MessageType);

        await AssertThrowsAsync<EndOfStreamException>(() => ReceiveAsync(server));
    }

    private static async ValueTask CloseIsIdempotentAndRejectsFurtherUse()
    {
        (SidecarLoopbackStream first, SidecarLoopbackStream second) = SidecarLoopbackStream.CreatePair();
        SidecarConnection client = new(first);
        using SidecarConnection server = new(second);

        client.Close();
        AssertEqual(SidecarConnectionState.Closed, client.State);
        client.Close();
        AssertEqual(SidecarConnectionState.Closed, client.State);

        await AssertThrowsAsync<InvalidOperationException>(() =>
        {
            SidecarFrame frame = new(
                SidecarProtocol.Version,
                SidecarMessageType.Hello,
                SidecarFrameTraits.None,
                SidecarCorrelationId.Create(),
                Array.Empty<byte>());
            return client.SendAsync(frame).AsTask();
        });
    }

    private static async ValueTask SendCancellationIsObserved()
    {
        (SidecarLoopbackStream first, SidecarLoopbackStream second) = SidecarLoopbackStream.CreatePair();
        using SidecarConnection client = new(first);
        using SidecarConnection server = new(second);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => client.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Event,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            new byte[] { 1, 2, 3 }),
            cancellation.Token).AsTask());

        // The cancellation happened before any bytes were written: the connection stays usable.
        AssertEqual(SidecarConnectionState.Connected, client.State);
        await client.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Event,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            new byte[] { 1 }));
        SidecarFrame frame = await ReceiveAsync(server);
        AssertEqual(SidecarMessageType.Event, frame.MessageType);
    }

    private static Task<SidecarFrame> ReceiveAsync(SidecarConnection connection) =>
        connection.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}
