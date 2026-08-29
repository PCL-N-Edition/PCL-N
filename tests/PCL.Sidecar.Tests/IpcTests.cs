using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;

namespace PCL.Sidecar.Tests;

internal static partial class Program
{
    private static async ValueTask IpcStreamRoundTripsFrames()
    {
        if (!SidecarIpcListener.IsSupported)
        {
            Console.WriteLine("SKIP: local IPC is not supported on this platform.");
            return;
        }

        string pipeName = $"pcln-sidecar-test-{Guid.NewGuid():N}";
        using SidecarIpcListener listener = SidecarIpcListener.Bind(pipeName);

        ValueTask<Stream> accepted = listener.AcceptAsync();
        Stream clientStream = await SidecarIpcConnector.ConnectAsync(listener.Endpoint);
        Stream serverStream = await accepted;

        using SidecarConnection client = new(clientStream);
        using SidecarConnection server = new(serverStream);

        SidecarCorrelationId correlation = SidecarCorrelationId.Create();
        SidecarPayloadWriter writer = new();
        writer.WriteString(1, "ipc");
        writer.WriteBytes(2, new byte[4096]);
        // Start the receive before sending: pipe writes block once the pipe buffer is full,
        // mirroring the concurrent read-loop the session runs in production.
        Task<SidecarFrame> received = server.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await client.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.QueryRequest,
            SidecarFrameTraits.None,
            correlation,
            writer.ToArray()));

        SidecarFrame frame = await received;
        AssertEqual(SidecarMessageType.QueryRequest, frame.MessageType);
        AssertEqual(correlation, frame.CorrelationId);
        SidecarPayloadReader reader = new(frame.Payload.Span);
        AssertEqual("ipc", reader.ReadNext().ReadString());
        AssertEqual(4096, reader.ReadNext().ReadBytes().Length);

        Task<SidecarFrame> resultTask = client.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await server.SendAsync(new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.QueryResult,
            SidecarFrameTraits.Final,
            correlation,
            Array.Empty<byte>()));
        SidecarFrame result = await resultTask;
        AssertEqual(SidecarMessageType.QueryResult, result.MessageType);
        AssertEqual(0, result.Payload.Length);
    }
}
