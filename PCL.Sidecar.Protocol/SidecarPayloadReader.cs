using System.Buffers.Binary;
using System.Text;

namespace PCL.Sidecar.Protocol;

/// <summary>
/// The payload field types. The tag travels on the wire so readers can skip unknown fields of
/// any type by length; a known field ID with an unexpected tag is a schema violation.
/// </summary>
public enum SidecarFieldTag : byte
{
    Boolean = 1,
    U32 = 2,
    U64 = 3,
    I64 = 4,
    F64 = 5,
    Str = 6,
    Bytes = 7,
    Id128 = 8,
}

/// <summary>
/// One decoded payload field. Field values are read lazily from the payload bytes, so skipping
/// an unknown field allocates nothing.
/// </summary>
public readonly ref struct SidecarPayloadField
{
    private readonly ReadOnlySpan<byte> _value;

    internal SidecarPayloadField(ushort id, SidecarFieldTag tag, ReadOnlySpan<byte> value)
    {
        Id = id;
        Tag = tag;
        _value = value;
    }

    public ushort Id { get; }

    public SidecarFieldTag Tag { get; }

    public bool ReadBoolean() => Require(SidecarFieldTag.Boolean, 1)[0] != 0;

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Require(SidecarFieldTag.U32, 4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Require(SidecarFieldTag.U64, 8));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Require(SidecarFieldTag.I64, 8));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Require(SidecarFieldTag.F64, 8));

    public Guid ReadGuid() => new(Require(SidecarFieldTag.Id128, 16));

    public string ReadString() => Encoding.UTF8.GetString(Require(SidecarFieldTag.Str, null));

    public byte[] ReadBytes() => Require(SidecarFieldTag.Bytes, null).ToArray();

    private ReadOnlySpan<byte> Require(SidecarFieldTag tag, int? length)
    {
        if (Tag != tag)
        {
            throw new SidecarProtocolException(
                $"Payload field {Id} carries tag {Tag} but was read as {tag}.");
        }

        if (length is { } expected && _value.Length != expected)
        {
            throw new SidecarProtocolException(
                $"Payload field {Id} carries {_value.Length} bytes; {tag} requires {expected}.");
        }

        return _value;
    }
}

/// <summary>
/// Reads payload fields written by <see cref="SidecarPayloadWriter"/>. Unknown field IDs are
/// skipped by length — that is the protocol's forward-compatibility contract — while a known ID
/// with an unexpected type tag is rejected.
/// </summary>
public ref struct SidecarPayloadReader
{
    private ReadOnlySpan<byte> _remaining;

    public SidecarPayloadReader(ReadOnlySpan<byte> payload)
    {
        _remaining = payload;
    }

    /// <summary>
    /// Gets a value indicating whether another field is present.
    /// </summary>
    public bool HasMore => _remaining.Length > 0;

    /// <summary>
    /// Reads the next field header. The caller decides by <see cref="SidecarPayloadField.Id"/>
    /// whether to read the value or leave it: letting the field go out of scope skips it.
    /// </summary>
    public SidecarPayloadField ReadNext()
    {
        if (_remaining.Length < 5)
        {
            throw new SidecarProtocolException(
                "The payload ends inside a field header; the layout is malformed.");
        }

        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(_remaining);
        if (id == 0)
        {
            throw new SidecarProtocolException("Field ID zero is reserved.");
        }

        var tag = (SidecarFieldTag)_remaining[2];
        if (!Enum.IsDefined(tag))
        {
            throw new SidecarProtocolException($"Payload field {id} carries unknown tag {tag}.");
        }

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(_remaining[3..]);
        if (_remaining.Length < 5 + length)
        {
            throw new SidecarProtocolException(
                $"Payload field {id} declares {length} bytes but the payload ends early.");
        }

        SidecarPayloadField field = new(id, tag, _remaining.Slice(5, length));
        _remaining = _remaining[(5 + length)..];
        return field;
    }
}
