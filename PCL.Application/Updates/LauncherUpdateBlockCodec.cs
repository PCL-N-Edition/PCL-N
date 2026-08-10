// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using ZstdSharp;

namespace PCL.Application.Updates;

/// <summary>
/// Full-block compression codecs for content-addressed update blocks.
/// Raw SHA-256 identity is always over the decompressed payload.
/// </summary>
internal static class LauncherUpdateBlockCodec
{
    internal const string Gzip = "gzip";
    internal const string Zstd = "zstd";

    internal static string Normalize(string? compression) =>
        string.IsNullOrWhiteSpace(compression)
            ? Gzip
            : compression.Trim().ToLowerInvariant() switch
            {
                "gzip" or "gz" => Gzip,
                "zstd" or "zst" or "zstandard" => Zstd,
                _ => throw new InvalidDataException($"不支持的分块压缩算法：{compression}。")
            };

    internal static bool IsSupported(string? compression)
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

    /// <summary>Open a decompressing stream over compressed full-block bytes.</summary>
    internal static Stream OpenDecompressor(Stream compressed, string? compression, bool leaveOpen = false)
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
    /// Decompress into <paramref name="destination"/> while verifying size and SHA-256,
    /// using a rented slab buffer (protocol v2 §14).
    /// </summary>
    internal static async Task DecompressAndVerifyAsync(
        Stream compressedNetwork,
        string? compression,
        string expectedSha256,
        long expectedRawSize,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        await using Stream decompressor = OpenDecompressor(compressedNetwork, compression, leaveOpen: false);
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
                    break;
                checked { written += read; }
                if (written > expectedRawSize)
                    throw new InvalidDataException($"更新分块解压后大小超限：{expectedSha256}。");
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (written != expectedRawSize || !string.Equals(actual, expectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"更新分块 SHA-256 校验失败：{expectedSha256}。");
            output.Close();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
