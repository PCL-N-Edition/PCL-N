using System.Buffers.Binary;

namespace PCL.Sidecar.Protocol;

/// <summary>
/// Encodes and decodes Sidecar frames over byte buffers. The wire layout is little-endian with
/// a fixed 32-byte header; no CLR struct is mapped onto the wire and no object graph crosses
/// the process boundary.
/// </summary>
public static class SidecarFrameCodec
{
    /// <summary>
    /// Gets the exact encoded size of one frame.
    /// </summary>
    public static int GetFrameSize(int payloadLength) => SidecarProtocol.HeaderSize + payloadLength;

    /// <summary>
    /// Encodes one frame into <paramref name="buffer"/>. The buffer must be exactly
    /// <see cref="GetFrameSize"/> long.
    /// </summary>
    public static void Encode(SidecarFrame frame, Span<byte> buffer)
    {
        int payloadLength = frame.Payload.Length;
        if (payloadLength > SidecarProtocol.MaxPayloadLength)
        {
            throw new SidecarProtocolException(
                $"The frame payload of {payloadLength} bytes exceeds the protocol maximum.");
        }

        if (buffer.Length != SidecarProtocol.HeaderSize + payloadLength)
        {
            throw new SidecarProtocolException(
                $"The frame buffer must be exactly {SidecarProtocol.HeaderSize + payloadLength} bytes.");
        }

        if (!frame.CorrelationId.IsAssigned)
        {
            throw new SidecarProtocolException("Every frame carries an assigned correlation ID.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer, SidecarProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..], SidecarProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], frame.ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..], (ushort)frame.MessageType);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..], (ushort)frame.Flags);
        _ = frame.CorrelationId.Value.TryWriteBytes(buffer[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[28..], (uint)payloadLength);
        frame.Payload.Span.CopyTo(buffer[SidecarProtocol.HeaderSize..]);
    }

    /// <summary>
    /// Decodes the frame header from exactly <see cref="SidecarProtocol.HeaderSize"/> bytes and
    /// copies the payload into a fresh buffer.
    /// </summary>
    public static SidecarFrame Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SidecarProtocol.HeaderSize)
        {
            throw new SidecarProtocolException(
                $"A frame needs at least {SidecarProtocol.HeaderSize} bytes for the header.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (magic != SidecarProtocol.Magic)
        {
            throw new SidecarProtocolException("The frame magic does not match the Sidecar protocol.");
        }

        ushort headerVersion = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
        if (headerVersion != SidecarProtocol.Version)
        {
            throw new SidecarProtocolException(
                $"The frame header version {headerVersion} is not supported; {SidecarProtocol.Version} is required.");
        }

        ushort protocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]);
        if (protocolVersion != SidecarProtocol.Version)
        {
            throw new SidecarProtocolException(
                $"The frame protocol version {protocolVersion} does not match the negotiated version {SidecarProtocol.Version}.");
        }

        ushort messageType = BinaryPrimitives.ReadUInt16LittleEndian(buffer[8..]);
        if (!Enum.IsDefined((SidecarMessageType)messageType))
        {
            throw new SidecarProtocolException($"The message type {messageType} is unknown to this protocol version.");
        }

        var flags = (SidecarFrameTraits)BinaryPrimitives.ReadUInt16LittleEndian(buffer[10..]);
        if (!Enum.IsDefined(flags))
        {
            throw new SidecarProtocolException("The frame flags carry undefined bits.");
        }

        var correlationId = new SidecarCorrelationId(new Guid(buffer.Slice(12, 16)));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..]);
        if (payloadLength > SidecarProtocol.MaxPayloadLength)
        {
            throw new SidecarProtocolException(
                $"The declared payload of {payloadLength} bytes exceeds the protocol maximum.");
        }

        if (buffer.Length != SidecarProtocol.HeaderSize + (int)payloadLength)
        {
            throw new SidecarProtocolException(
                $"The frame declares {payloadLength} payload bytes but carries {buffer.Length - SidecarProtocol.HeaderSize}.");
        }

        byte[] payload = buffer.Slice(SidecarProtocol.HeaderSize).ToArray();
        return new SidecarFrame(
            protocolVersion,
            (SidecarMessageType)messageType,
            flags,
            correlationId,
            payload);
    }
}
