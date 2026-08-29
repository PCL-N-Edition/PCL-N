using System.Buffers.Binary;
using System.Text;
using PCL.Sidecar.Protocol;

namespace PCL.Xsr.Runtime;

/// <summary>
/// One state value codec: converts between the wire bytes of a declared contract and the typed
/// value its mirror cell holds. Codecs are pure, allocation-bounded, and reflection-free; the
/// generated DTO codec (id 6) treats the DTO blob as schema-contracted opaque bytes whose
/// field-level encode/decode is emitted by the generator.
/// </summary>
public interface ISidecarValueCodec
{
    uint Id { get; }

    /// <summary>
    /// Gets the mirror cell type this codec feeds.
    /// </summary>
    Type ValueType { get; }

    /// <summary>
    /// Validates the wire bytes for this codec, throwing on malformed input.
    /// </summary>
    void Validate(ReadOnlySpan<byte> raw);

    /// <summary>
    /// Decodes the wire bytes into the typed value.
    /// </summary>
    object Decode(ReadOnlySpan<byte> raw);

    /// <summary>
    /// Encodes the typed value into wire bytes.
    /// </summary>
    byte[] Encode(object value);
}

/// <summary>
/// The codec registry. IDs are frozen for the protocol draft: 0 = UTF-8 string, 1 = Bool, 2 =
/// Int32, 3 = Int64, 4 = Float64, 5 = Bytes, 6 = generated DTO blob. Unknown IDs are rejected at
/// registration, so later codec additions are protocol-draft revisions, not silent extensions.
/// </summary>
public static class SidecarValueCodecs
{
    public const uint Utf8String = 0;
    public const uint Bool = 1;
    public const uint I32 = 2;
    public const uint I64 = 3;
    public const uint F64 = 4;
    public const uint Bytes = 5;
    public const uint GeneratedDto = 6;

    private static readonly Dictionary<uint, ISidecarValueCodec> Codecs = new()
    {
        [Utf8String] = new SimpleCodec(Utf8String, typeof(string), null, static raw => Encoding.UTF8.GetString(raw), static value => Encoding.UTF8.GetBytes((string)value)),
        [Bool] = new SimpleCodec(Bool, typeof(bool), 1, static raw => raw[0] != 0, static value => [(bool)value ? (byte)1 : (byte)0]),
        [I32] = new SimpleCodec(I32, typeof(int), 4, static raw => BinaryPrimitives.ReadInt32LittleEndian(raw), static value => { byte[] b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, (int)value); return b; }),
        [I64] = new SimpleCodec(I64, typeof(long), 8, static raw => BinaryPrimitives.ReadInt64LittleEndian(raw), static value => { byte[] b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, (long)value); return b; }),
        [F64] = new SimpleCodec(F64, typeof(double), 8, static raw => BinaryPrimitives.ReadDoubleLittleEndian(raw), static value => { byte[] b = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, (double)value); return b; }),
        [Bytes] = new SimpleCodec(Bytes, typeof(byte[]), null, static raw => raw.ToArray(), static value => (byte[])value),
        [GeneratedDto] = new SimpleCodec(GeneratedDto, typeof(byte[]), null, static raw => raw.ToArray(), static value => (byte[])value),
    };

    public static ISidecarValueCodec Get(uint id) =>
        Codecs.TryGetValue(id, out ISidecarValueCodec? codec)
            ? codec
            : throw new SidecarProtocolException($"The state codec {id} is unknown to this protocol draft.");

    /// <summary>
    /// Validates wire bytes for a codec without decoding.
    /// </summary>
    public static void Validate(uint id, ReadOnlySpan<byte> raw) => Get(id).Validate(raw);

    /// <summary>
    /// Decodes wire bytes into the codec's typed value.
    /// </summary>
    public static object Decode(uint id, ReadOnlySpan<byte> raw) => Get(id).Decode(raw);

    private sealed class SimpleCodec(
        uint id,
        Type valueType,
        int? fixedLength,
        Func<ReadOnlySpan<byte>, object> decode,
        Func<object, byte[]> encode) : ISidecarValueCodec
    {
        public uint Id { get; } = id;

        public Type ValueType { get; } = valueType;

        public void Validate(ReadOnlySpan<byte> raw)
        {
            if (fixedLength is { } length && raw.Length != length)
            {
                throw new SidecarProtocolException(
                    $"Codec {Id} requires exactly {length} bytes; the value carries {raw.Length}.");
            }
        }

        public object Decode(ReadOnlySpan<byte> raw)
        {
            Validate(raw);
            return decode(raw);
        }

        public byte[] Encode(object value) => encode(value);
    }
}
