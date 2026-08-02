// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.IO.Pipes;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class PluginSidecarProtocolTests
{
    [TestMethod]
    public void SidecarPathResolver_PrefersCurrentHostPayload()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-sidecar-path-" + Guid.NewGuid().ToString("N"));
        string hostDir = Path.Combine(root, "host");
        string baseDir = Path.Combine(root, "base");
        string expected = Path.Combine(hostDir, "sidecar", PluginSidecarPaths.ExecutableFileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            Directory.CreateDirectory(baseDir);
            File.WriteAllBytes(expected, []);

            Assert.AreEqual(
                Path.GetFullPath(expected),
                PluginSidecarPaths.ResolveLooseExecutable(hostDir, baseDir));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProtocolV4Frame_RoundTripsJsonPayload()
    {
        Pipe pipe = new();
        ArrayBufferWriter<byte> payloadBuffer = new();
        PluginSidecarV4Framing.WriteJson(
            pipe.Writer,
            payloadBuffer,
            PluginSidecarMessageType.Request,
            PluginSidecarFrameFlags.None,
            requestId: 42,
            new PluginSidecarV4Request
            {
                Method = PluginSidecarMethods.HealthPing,
                Params = new PluginSidecarParams { Value = "probe" }
            },
            PluginSidecarJsonContext.Default.PluginSidecarV4Request);
        await pipe.Writer.FlushAsync();

        ReadResult read = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = read.Buffer;
        Assert.IsTrue(PluginSidecarV4Framing.TryReadFrame(
            ref buffer,
            out PluginSidecarFrameHeader header,
            out ReadOnlySequence<byte> payload));
        Assert.AreEqual(42UL, header.RequestId);
        Assert.AreEqual(PluginSidecarMessageType.Request, header.MessageType);
        PluginSidecarV4Request? request = PluginSidecarV4Framing.ReadJson(
            payload,
            PluginSidecarJsonContext.Default.PluginSidecarV4Request);
        Assert.AreEqual(PluginSidecarMethods.HealthPing, request?.Method);
        Assert.AreEqual("probe", request?.Params?.Value);
        Assert.IsTrue(buffer.IsEmpty);

        pipe.Reader.AdvanceTo(read.Buffer.End);
        await pipe.Reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public void ProtocolV4Frame_RejectsOversizedInlinePayloadBeforeAllocation()
    {
        byte[] headerBytes = new byte[PluginSidecarV4Framing.HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(
            headerBytes,
            PluginSidecarV4Framing.MaxInlinePayloadBytes + 1);
        BinaryPrimitives.WriteUInt16BigEndian(
            headerBytes.AsSpan(4),
            PluginSidecarProtocolVersions.Current);
        BinaryPrimitives.WriteUInt16BigEndian(
            headerBytes.AsSpan(6),
            (ushort)PluginSidecarMessageType.Request);
        BinaryPrimitives.WriteUInt64BigEndian(headerBytes.AsSpan(12), 1);
        ReadOnlySequence<byte> buffer = new(headerBytes);

        Assert.Throws<InvalidDataException>(() =>
            PluginSidecarV4Framing.TryReadFrame(
                ref buffer,
                out _,
                out _));
    }

    [TestMethod]
    public async Task ProtocolV4Client_MultiplexesAndCancelsWithoutDesynchronizing()
    {
        string pipeName = "pcln-sidecar-test-" + Guid.NewGuid().ToString("N");
        await using NamedPipeServerStream serverStream = new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous |
            System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await using NamedPipeClientStream clientStream = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);

        Task accept = serverStream.WaitForConnectionAsync();
        await clientStream.ConnectAsync();
        await accept;

        Task fakeServer = RunFakeV4ServerAsync(serverStream);
        await using PluginSidecarClient client = new();
        await client.ConnectAsync(clientStream);
        PluginSidecarResult hello = await client.HelloAsync("test-token");
        Assert.AreEqual(PluginSidecarProtocolVersions.Current, hello.ProtocolVersion);
        Assert.AreEqual(PluginSidecarProtocolVersions.Current, client.ProtocolVersion);

        Task<PluginSidecarResult> slow = client.CallAsync("test.slow", null);
        Task<PluginSidecarResult> fast = client.CallAsync("test.fast", null);
        PluginSidecarResult fastResult = await fast.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("fast", fastResult.Message);
        Assert.IsFalse(slow.IsCompleted, "A fast response must not wait for an earlier slow request.");
        Assert.AreEqual("slow", (await slow.WaitAsync(TimeSpan.FromSeconds(2))).Message);

        using CancellationTokenSource cancelSource = new();
        Task<PluginSidecarResult> cancelled = client.CallAsync(
            "test.cancel",
            null,
            cancelSource.Token);
        cancelSource.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await cancelled);

        PluginSidecarResult ping = await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("pong", ping.Message);
        Assert.IsFalse(client.IsBroken, "A v4 cancel frame must not desynchronize the connection.");

        await fakeServer.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task RunFakeV4ServerAsync(Stream stream)
    {
        PluginSidecarRequest? hello = await PluginSidecarFraming.ReadAsync(
            stream,
            PluginSidecarJsonContext.Default.PluginSidecarRequest,
            CancellationToken.None);
        Assert.IsNotNull(hello);
        Assert.AreEqual(PluginSidecarMethods.SystemHello, hello.Method);
        Assert.AreEqual(PluginSidecarProtocolVersions.Current, hello.Params?.MaximumProtocolVersion);
        await PluginSidecarFraming.WriteAsync(
            stream,
            new PluginSidecarResponse
            {
                Id = hello.Id,
                Result = new PluginSidecarResult
                {
                    Ok = true,
                    ProtocolVersion = PluginSidecarProtocolVersions.Current,
                    SidecarVersion = "test"
                }
            },
            PluginSidecarJsonContext.Default.PluginSidecarResponse,
            CancellationToken.None);

        PipeReader reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        PipeWriter writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        ArrayBufferWriter<byte> payloadBuffer = new();
        try
        {
            (PluginSidecarFrameHeader Header, PluginSidecarV4Request? Request) first =
                await ReadFrameAsync(reader);
            (PluginSidecarFrameHeader Header, PluginSidecarV4Request? Request) second =
                await ReadFrameAsync(reader);
            var byMethod = new[] { first, second }.ToDictionary(
                static frame => frame.Request?.Method ?? "",
                StringComparer.Ordinal);

            WriteResponse(
                writer,
                payloadBuffer,
                byMethod["test.fast"].Header.RequestId,
                "fast");
            await writer.FlushAsync();
            await Task.Delay(50);
            WriteResponse(
                writer,
                payloadBuffer,
                byMethod["test.slow"].Header.RequestId,
                "slow");
            await writer.FlushAsync();

            bool sawCancel = false;
            ulong pingId = 0;
            for (int i = 0; i < 4 && (!sawCancel || pingId == 0); i++)
            {
                (PluginSidecarFrameHeader Header, PluginSidecarV4Request? Request) frame =
                    await ReadFrameAsync(reader);
                if (frame.Header.MessageType == PluginSidecarMessageType.Cancel)
                    sawCancel = true;
                else if (frame.Request?.Method == PluginSidecarMethods.HealthPing)
                    pingId = frame.Header.RequestId;
            }

            Assert.IsTrue(sawCancel);
            Assert.AreNotEqual(0UL, pingId);
            WriteResponse(writer, payloadBuffer, pingId, "pong");
            await writer.FlushAsync();
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
    }

    private static async Task<(PluginSidecarFrameHeader Header, PluginSidecarV4Request? Request)>
        ReadFrameAsync(PipeReader reader)
    {
        while (true)
        {
            ReadResult read = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = read.Buffer;
            SequencePosition examined = buffer.End;
            if (PluginSidecarV4Framing.TryReadFrame(
                    ref buffer,
                    out PluginSidecarFrameHeader header,
                    out ReadOnlySequence<byte> payload))
            {
                PluginSidecarV4Request? request = header.MessageType == PluginSidecarMessageType.Request
                    ? PluginSidecarV4Framing.ReadJson(
                        payload,
                        PluginSidecarJsonContext.Default.PluginSidecarV4Request)
                    : null;
                // A second complete frame may already be buffered. Leave it unexamined so
                // the next ReadAsync returns immediately instead of waiting for more bytes.
                reader.AdvanceTo(buffer.Start, buffer.Start);
                return (header, request);
            }

            reader.AdvanceTo(buffer.Start, examined);
            if (read.IsCompleted)
                throw new EndOfStreamException();
        }
    }

    private static void WriteResponse(
        PipeWriter writer,
        ArrayBufferWriter<byte> payloadBuffer,
        ulong requestId,
        string message)
    {
        PluginSidecarV4Framing.WriteJson(
            writer,
            payloadBuffer,
            PluginSidecarMessageType.Response,
            PluginSidecarFrameFlags.Final,
            requestId,
            new PluginSidecarV4Response
            {
                Result = new PluginSidecarResult { Ok = true, Message = message }
            },
            PluginSidecarJsonContext.Default.PluginSidecarV4Response);
    }
}
