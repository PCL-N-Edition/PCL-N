using System.Buffers.Binary;
using System.Text;

namespace PCL.Sidecar.Protocol;

/// <summary>
/// Writes payload fields as tag-length-value records with strictly ascending field IDs. The
/// ascending order gives deterministic layouts, O(1) duplicate detection, and cheap skipping.
/// </summary>
public sealed class SidecarPayloadWriter : IDisposable
{
    private readonly MemoryStream _stream = new();
    private ushort _lastFieldId;

    public int Length => (int)_stream.Length;

    public void WriteBoolean(ushort fieldId, bool value)
    {
        BeginField(fieldId, SidecarFieldTag.Boolean, length: 1);
        _stream.WriteByte(value ? (byte)1 : (byte)0);
    }

    public void WriteUInt32(ushort fieldId, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        BeginField(fieldId, SidecarFieldTag.U32, buffer.Length);
        _stream.Write(buffer);
    }

    public void WriteUInt64(ushort fieldId, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        BeginField(fieldId, SidecarFieldTag.U64, buffer.Length);
        _stream.Write(buffer);
    }

    public void WriteInt64(ushort fieldId, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        BeginField(fieldId, SidecarFieldTag.I64, buffer.Length);
        _stream.Write(buffer);
    }

    public void WriteDouble(ushort fieldId, double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        BeginField(fieldId, SidecarFieldTag.F64, buffer.Length);
        _stream.Write(buffer);
    }

    public void WriteGuid(ushort fieldId, Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        _ = value.TryWriteBytes(buffer);
        BeginField(fieldId, SidecarFieldTag.Id128, buffer.Length);
        _stream.Write(buffer);
    }

    public void WriteString(ushort fieldId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteVariable(fieldId, SidecarFieldTag.Str, Encoding.UTF8.GetBytes(value));
    }

    public void WriteBytes(ushort fieldId, ReadOnlySpan<byte> value)
    {
        WriteVariable(fieldId, SidecarFieldTag.Bytes, value);
    }

    public byte[] ToArray() => _stream.ToArray();

    /// <summary>
    /// Releases the internal buffer. The writer is single-use after this.
    /// </summary>
    public void Dispose() => _stream.Dispose();

    private void WriteVariable(ushort fieldId, SidecarFieldTag tag, ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
        {
            throw new SidecarProtocolException(
                $"A string or bytes payload field cannot exceed {ushort.MaxValue} bytes.");
        }

        BeginField(fieldId, tag, value.Length);
        _stream.Write(value);
    }

    private void BeginField(ushort fieldId, SidecarFieldTag tag, int length)
    {
        if (fieldId == 0)
        {
            throw new SidecarProtocolException("Field ID zero is reserved.");
        }

        if (fieldId <= _lastFieldId)
        {
            throw new SidecarProtocolException(
                $"Payload field IDs must be strictly ascending; {fieldId} follows {_lastFieldId}.");
        }

        if (_stream.Length + 5 + length > SidecarProtocol.MaxPayloadLength)
        {
            throw new SidecarProtocolException("The payload exceeds the protocol maximum.");
        }

        Span<byte> header = stackalloc byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(header, fieldId);
        header[2] = (byte)tag;
        BinaryPrimitives.WriteUInt16LittleEndian(header[3..], (ushort)length);
        _stream.Write(header);
        _lastFieldId = fieldId;
    }
}
