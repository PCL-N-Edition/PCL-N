using PCL.Sidecar.Protocol;

namespace PCL.Sidecar.Tests;

internal static partial class Program
{
    private static SidecarFrame BuildControlFrame()
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, SidecarProtocol.Version);
        return new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.Hello,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            writer.ToArray());
    }

    private static SidecarFrame BuildFrame()
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, 42);
        writer.WriteString(2, "sidecar");
        byte[] payload = writer.ToArray();
        return new SidecarFrame(
            SidecarProtocol.Version,
            SidecarMessageType.CommandRequest,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            payload);
    }

    private static void FrameRoundTripsHeaderAndPayload()
    {
        SidecarFrame frame = BuildFrame();
        byte[] wire = EncodeFrame(frame);
        AssertEqual(SidecarProtocol.HeaderSize + frame.Payload.Length, wire.Length);

        SidecarFrame decoded = SidecarFrameCodec.Decode(wire);
        AssertEqual(frame.ProtocolVersion, decoded.ProtocolVersion);
        AssertEqual(frame.MessageType, decoded.MessageType);
        AssertEqual(frame.Flags, decoded.Flags);
        AssertEqual(frame.CorrelationId, decoded.CorrelationId);
        AssertTrue(frame.Payload.Span.SequenceEqual(decoded.Payload.Span));
        AssertTrue(decoded.IsDataPlane);
    }

    private static void PayloadFieldsRoundTripEveryType()
    {
        Guid guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        SidecarPayloadWriter writer = new();
        writer.WriteBoolean(1, true);
        writer.WriteUInt32(2, 4_000_000_000);
        writer.WriteUInt64(3, ulong.MaxValue);
        writer.WriteInt64(4, -5);
        writer.WriteDouble(5, 2.5);
        writer.WriteGuid(6, guid);
        writer.WriteString(7, "你好, sidecar");
        writer.WriteBytes(8, [1, 2, 3, 250]);

        SidecarPayloadReader reader = new(writer.ToArray());
        AssertTrue(reader.ReadNext().ReadBoolean());
        AssertEqual(4_000_000_000u, reader.ReadNext().ReadUInt32());
        AssertEqual(ulong.MaxValue, reader.ReadNext().ReadUInt64());
        AssertEqual(-5L, reader.ReadNext().ReadInt64());
        AssertEqual(2.5, reader.ReadNext().ReadDouble());
        AssertEqual(guid, reader.ReadNext().ReadGuid());
        AssertEqual("你好, sidecar", reader.ReadNext().ReadString());
                AssertTrue(reader.ReadNext().ReadBytes().SequenceEqual(new byte[] { 1, 2, 3, 250 }));
        AssertFalse(reader.HasMore);
    }

    private static void UnknownFieldsAreSkippedByLength()
    {
        // A producer from a future protocol version writes a field this reader does not know.
        SidecarPayloadWriter future = new();
        future.WriteBoolean(1, true);
        future.WriteBytes(2, [9, 9, 9, 9, 9, 9, 9, 9]);
        future.WriteUInt32(3, 7);

        SidecarPayloadReader reader = new(future.ToArray());
        SidecarPayloadField first = reader.ReadNext();
        AssertEqual((ushort)1, first.Id);
        AssertTrue(first.ReadBoolean());

        // Unknown field id 2 is skipped by leaving the field unread.
        SidecarPayloadField unknown = reader.ReadNext();
        AssertEqual((ushort)2, unknown.Id);

        SidecarPayloadField known = reader.ReadNext();
        AssertEqual((ushort)3, known.Id);
        AssertEqual(7u, known.ReadUInt32());
        AssertFalse(reader.HasMore);
    }

    private static void FrameMagicAndVersionsAreEnforced()
    {
        SidecarFrame frame = BuildFrame();
        byte[] wire = EncodeFrame(frame);

        byte[] badMagic = (byte[])wire.Clone();
        badMagic[0] ^= 0xFF;
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.Decode(badMagic));

        byte[] badHeaderVersion = (byte[])wire.Clone();
        badHeaderVersion[4] = 9;
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.Decode(badHeaderVersion));

        byte[] badProtocolVersion = (byte[])wire.Clone();
        badProtocolVersion[6] = 9;
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.Decode(badProtocolVersion));
    }

    private static void TruncatedAndOversizedFramesAreRejected()
    {
        SidecarFrame frame = BuildFrame();
        byte[] wire = EncodeFrame(frame);

        AssertThrows<SidecarProtocolException>(
            () => SidecarFrameCodec.Decode(wire.AsSpan(0, SidecarProtocol.HeaderSize - 1).ToArray()));

        // The declared payload length disagrees with the buffer.
        byte[] truncated = wire[..^2];
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.Decode(truncated));

        // A hostile length field beyond the protocol maximum is rejected before allocation.
        byte[] hostile = (byte[])wire.Clone();
        hostile[28] = 0xFF;
        hostile[29] = 0xFF;
        hostile[30] = 0xFF;
        hostile[31] = 0x7F;
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.Decode(hostile));
    }

    private static void UnknownMessageTypesAreRejected()
    {
        SidecarFrame frame = BuildFrame();
        byte[] wire = EncodeFrame(frame with { MessageType = (SidecarMessageType)999 });
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.Decode(wire));
    }

    private static void MessageNumbersAreFrozen()
    {
        // Protocol v1 freezes these numbers; the test fails loudly if anyone renumbers.
        AssertEqual((ushort)1, (ushort)SidecarMessageType.Hello);
        AssertEqual((ushort)2, (ushort)SidecarMessageType.Welcome);
        AssertEqual((ushort)10, (ushort)SidecarMessageType.RegisterEnd);
        AssertEqual((ushort)11, (ushort)SidecarMessageType.Ready);
        AssertEqual((ushort)24, (ushort)SidecarMessageType.Crash);
        AssertEqual((ushort)30, (ushort)SidecarMessageType.Shutdown);
        AssertEqual((ushort)64, (ushort)SidecarMessageType.CommandRequest);
        AssertEqual((ushort)65, (ushort)SidecarMessageType.CommandResult);
        AssertEqual((ushort)67, (ushort)SidecarMessageType.QueryResult);
        AssertEqual((ushort)72, (ushort)SidecarMessageType.StateDelta);
        AssertEqual((ushort)73, (ushort)SidecarMessageType.Event);
        AssertEqual((ushort)80, (ushort)SidecarMessageType.StreamChunk);

        // The control and data planes split below the command range.
        AssertTrue(BuildControlFrame().IsControlPlane);
        AssertFalse(BuildFrame().IsControlPlane);
    }

    private static void AscendingFieldIdsAreEnforced()
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(4, 1);
        AssertThrows<SidecarProtocolException>(() => writer.WriteUInt32(4, 2));
        AssertThrows<SidecarProtocolException>(() => writer.WriteUInt32(3, 2));
        AssertThrows<SidecarProtocolException>(() => writer.WriteBoolean(0, true));
    }

    private static void TagMismatchesAreRejectedOnRead()
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, 5);
        AssertThrows<SidecarProtocolException>(
            () => ReadAs<string>(writer.ToArray(), field => field.ReadString()));
    }

    private static void MalformedPayloadsFailDeterministically()
    {
        // A header that promises five bytes but the payload ends early.
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarPayloadReader([0, 1, 6, 0, 5]).ReadNext());
        // Field id zero is reserved.
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarPayloadReader([0, 0, 2, 4, 0, 1, 2, 3, 4]).ReadNext());
        // Unknown tag.
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarPayloadReader([0, 1, 200, 0, 0]).ReadNext());
        // Trailing garbage inside a field boundary.
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarPayloadReader([0, 1, 2, 4, 0, 1]).ReadNext());
    }

    private static void FrameDecodeAllocatesOnlyThePayload()
    {
        // An empty-payload frame decodes with zero allocation: no per-field, per-header, or
        // codec objects are created on the decode path.
        SidecarPayloadWriter writer = new();
        SidecarFrame frame = new(
            SidecarProtocol.Version,
            SidecarMessageType.CommandRequest,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            writer.ToArray());
        byte[] wire = EncodeFrame(frame);
        _ = SidecarFrameCodec.Decode(wire);

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = SidecarFrameCodec.Decode(wire);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertEqual(0L, allocated);
    }

    private static void CorrelationIdsAreStableIdentities()
    {
        SidecarCorrelationId unassigned = default;
        AssertFalse(unassigned.IsAssigned);
        AssertTrue(SidecarCorrelationId.Create().IsAssigned);

        SidecorrelationLocal();
        static void SidecorrelationLocal()
        {
            SidecarCorrelationId first = SidecarCorrelationId.Create();
            AssertEqual(first, first);
            AssertFalse(first.Equals(SidecarCorrelationId.Create()));
        }
    }
}
