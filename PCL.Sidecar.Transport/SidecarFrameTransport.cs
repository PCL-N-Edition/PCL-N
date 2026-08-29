using System.Buffers.Binary;

using PCL.Sidecar.Protocol;

namespace PCL.Sidecar.Transport;

/// <summary>
/// The connection lifecycle. Every transition is explicit; a protocol failure moves the
/// connection to <see cref="Failed"/> and it never reconnects itself.
/// </summary>
public enum SidecarConnectionState
{
    Connected = 1,
    Closed = 2,
    Failed = 3,
}

/// <summary>
/// Reads and writes Sidecar frames over one duplex stream. Writes are serialized internally so
/// concurrent senders cannot interleave frame bytes; reads are single-caller. Protocol errors
/// surface as <see cref="SidecarProtocolException"/> and poison the stream.
/// </summary>
public sealed class SidecarFrameTransport : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SidecarFrameTransport(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The sidecar stream must be duplex.", nameof(stream));
        }
    }

    /// <summary>
    /// Writes one frame atomically.
    /// </summary>
    public async ValueTask SendAsync(
        SidecarFrame frame,
        CancellationToken cancellationToken = default)
    {
        byte[] wire = new byte[SidecarFrameCodec.GetFrameSize(frame.Payload.Length)];
        SidecarFrameCodec.Encode(frame, wire);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Reads exactly one frame, blocking until the header and payload have arrived.
    /// </summary>
    public async ValueTask<SidecarFrame> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[SidecarProtocol.HeaderSize];
        await ReadExactAsync(header, cancellationToken).ConfigureAwait(false);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != SidecarProtocol.Magic)
        {
            throw new SidecarProtocolException("The stream delivered bytes that are not a Sidecar frame.");
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));
        if (payloadLength > SidecarProtocol.MaxPayloadLength)
        {
            throw new SidecarProtocolException(
                $"The declared payload of {payloadLength} bytes exceeds the protocol maximum.");
        }

        // Decode over the complete frame: header plus payload as one contiguous buffer.
        byte[] wire = new byte[SidecarProtocol.HeaderSize + payloadLength];
        header.CopyTo(wire, 0);
        await ReadExactAsync(wire.AsMemory(SidecarProtocol.HeaderSize), cancellationToken).ConfigureAwait(false);
        return SidecarFrameCodec.Decode(wire);
    }

    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await _stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                throw new EndOfStreamException(
                    $"The sidecar stream ended after {read} of {buffer.Length} expected bytes.");
            }

            read += chunk;
        }
    }

    /// <summary>
    /// Releases the write gate. The transport is single-use after this.
    /// </summary>
    public void Dispose() => _writeGate.Dispose();
}
