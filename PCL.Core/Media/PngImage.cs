using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PCL.Core.Media;

/// <summary>Bounded, content-addressed encoded media. Native decoding remains a backend concern.</summary>
public sealed class PngImage
{
    private readonly byte[] _bytes;
    private PngImage(byte[] bytes, int width, int height)
    {
        _bytes = bytes; Width = width; Height = height;
        Key = Convert.ToHexString(SHA256.HashData(bytes));
    }
    public string Key { get; }
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Bytes => _bytes;
    public static PngImage? TryCreate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 33 or > 1_048_576 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(bytes[8..12]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8)) return null;
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]), height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
        return width is > 0 and <= 1024 && height is > 0 and <= 1024 ? new(bytes.ToArray(), width, height) : null;
    }
}
