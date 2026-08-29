using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using ZstdSharp;

namespace PCL.Services.Updates;

/// <summary>
/// Full-block compression codecs for content-addressed update blocks. Raw SHA-256 identity is
/// always over the decompressed payload. The declared compression is advisory: the actual
/// codec is detected from the stream magic and the block is decoded by what it is, then
/// verified against the declared identity.
/// </summary>
public static class UpdateBlockCodec
{
    public const string Gzip = "gzip";
    public const string Zstd = "zstd";

    public static string Normalize(string? compression) =>
        string.IsNullOrWhiteSpace(compression)
            ? Gzip
            : compression.Trim().ToLowerInvariant() switch
            {
                "gzip" or "gz" => Gzip,
                "zstd" or "zst" or "zstandard" => Zstd,
                _ => throw new InvalidDataException($"不支持的分块压缩算法：{compression}。")
            };

    public static bool IsSupported(string? compression)
    {
        try
        {
            _ = Normalize(compression);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static string? Detect(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 2 && prefix[0] == 0x1f && prefix[1] == 0x8b)
        {
            return Gzip;
        }

        if (prefix.Length >= 4 &&
            prefix[0] == 0x28 && prefix[1] == 0xb5 && prefix[2] == 0x2f && prefix[3] == 0xfd)
        {
            return Zstd;
        }

        return null;
    }

    /// <summary>Opens a decompressing stream over compressed full-block bytes.</summary>
    public static Stream OpenDecompressor(Stream compressed, string? compression, bool leaveOpen = false)
    {
        string codec = Normalize(compression);
        return codec switch
        {
            Gzip => new GZipStream(compressed, CompressionMode.Decompress, leaveOpen),
            Zstd => new DecompressionStream(compressed, leaveOpen: leaveOpen),
            _ => throw new InvalidDataException($"不支持的分块压缩算法：{codec}。")
        };
    }

    /// <summary>
    /// Decompresses into <paramref name="temporaryPath"/> while verifying the exact raw size
    /// and SHA-256, using a rented slab buffer. The declared compression is advisory; the
    /// actual format is detected from the stream prefix.
    /// </summary>
    public static async Task DecompressAndVerifyAsync(
        Stream compressedNetwork,
        string? compression,
        string expectedSha256,
        long expectedRawSize,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        string declaredCodec = Normalize(compression);
        byte[] prefix = new byte[4];
        int prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            int read = await compressedNetwork
                .ReadAsync(prefix.AsMemory(prefixLength, prefix.Length - prefixLength), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            prefixLength += read;
        }

        string actualCodec = Detect(prefix.AsSpan(0, prefixLength))
            ?? throw new InvalidDataException(
                $"更新分块压缩格式无法识别：{expectedSha256}；声明={declaredCodec}。");

        await using Stream replay = new PrefixReadStream(prefix.AsMemory(0, prefixLength), compressedNetwork, leaveOpen: false);
        await using Stream decompressor = OpenDecompressor(replay, actualCodec, leaveOpen: false);
        await using FileStream output = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long written = 0;
            while (true)
            {
                int read = await decompressor.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                checked
                {
                    written += read;
                }

                if (written > expectedRawSize)
                {
                    throw new InvalidDataException($"更新分块解压后大小超限：{expectedSha256}。");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (written != expectedRawSize || !string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"更新分块 SHA-256 校验失败：{expectedSha256}。");
            }

            await output.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Replays the bytes consumed for codec detection before continuing with the network
    /// stream.
    /// </summary>
    private sealed class PrefixReadStream(
        ReadOnlyMemory<byte> prefix,
        Stream inner,
        bool leaveOpen) : Stream
    {
        private int _offset;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int copied = CopyPrefix(buffer);
            return copied > 0 ? copied : inner.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int copied = CopyPrefix(buffer.Span);
            return copied > 0
                ? copied
                : await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override int ReadByte()
        {
            if (_offset < prefix.Length)
            {
                return prefix.Span[_offset++];
            }

            return inner.ReadByte();
        }

        private int CopyPrefix(Span<byte> destination)
        {
            int count = Math.Min(destination.Length, prefix.Length - _offset);
            if (count <= 0)
            {
                return 0;
            }

            prefix.Span.Slice(_offset, count).CopyTo(destination);
            _offset += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
