using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Nexa.Core.Media;

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
        => Create(bytes, 1_048_576, 1024);

    /// <summary>Bounded encoded local screenshot; decoding remains a backend concern.</summary>
    public static PngImage? TryCreatePreview(ReadOnlySpan<byte> bytes)
        => Create(bytes, 16 * 1_048_576, 4096);

    /// <summary>Static PNG/WebP resource icons. The historical carrier name is retained;
    /// skin and screenshot factories remain strictly PNG-only.</summary>
    public static PngImage? TryCreateResourceIcon(ReadOnlySpan<byte> bytes)
    {
        if (TryCreate(bytes) is { } png) return png;
        if (bytes.Length is < 30 or > 1_048_576 || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes[8..12].SequenceEqual("WEBP"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]) != bytes.Length - 8) return null;
        int width = 0, height = 0, imageWidth = 0, imageHeight = 0;
        for (int offset = 12; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 8) return null;
            var kind = bytes.Slice(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (size > bytes.Length - offset - 8) return null;
            var payload = bytes.Slice(offset + 8, (int)size);
            if (kind.SequenceEqual("ANIM"u8) || kind.SequenceEqual("ANMF"u8)) return null;
            if (kind.SequenceEqual("VP8X"u8))
            {
                if (offset != 12 || size != 10 || (payload[0] & 2) != 0) return null;
                width = 1 + payload[4] + (payload[5] << 8) + (payload[6] << 16);
                height = 1 + payload[7] + (payload[8] << 8) + (payload[9] << 16);
            }
            else if (kind.SequenceEqual("VP8L"u8))
            {
                if (imageWidth != 0 || size < 5 || payload[0] != 0x2f) return null;
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]);
                if ((bits >> 29) != 0) return null;
                imageWidth = 1 + (int)(bits & 0x3fff); imageHeight = 1 + (int)((bits >> 14) & 0x3fff);
            }
            else if (kind.SequenceEqual("VP8 "u8))
            {
                if (imageWidth != 0 || size < 10 || (payload[0] & 1) != 0
                    || !payload[3..6].SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a })) return null;
                imageWidth = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]) & 0x3fff;
                imageHeight = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..10]) & 0x3fff;
            }
            offset += 8 + (int)size + (int)(size & 1);
            if (offset > bytes.Length) return null;
        }
        if (width == 0) { width = imageWidth; height = imageHeight; }
        return width is > 0 and <= 1024 && height is > 0 and <= 1024 && imageWidth == width && imageHeight == height
            ? new(bytes.ToArray(), width, height) : null;
    }

    private static PngImage? Create(ReadOnlySpan<byte> bytes, int byteLimit, int dimensionLimit)
    {
        if (bytes.Length < 33 || bytes.Length > byteLimit || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(bytes[8..12]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8)) return null;
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]), height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
        return width > 0 && width <= dimensionLimit && height > 0 && height <= dimensionLimit ? new(bytes.ToArray(), width, height) : null;
    }
}
