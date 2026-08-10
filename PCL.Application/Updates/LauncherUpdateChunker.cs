// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Security.Cryptography;

namespace PCL.Application.Updates;

/// <summary>
/// Content-defined chunking profiles for launcher update block maps.
/// Gear table and cut rule are shared; only size bounds / dual masks differ.
/// </summary>
internal sealed record LauncherUpdateChunkProfile(
    string Algorithm,
    int MinimumSize,
    int AverageSize,
    int MaximumSize,
    ulong EarlyMask,
    ulong LateMask)
{
    /// <summary>pcln-fastcdc-v1: 256 KiB / 1 MiB / 2 MiB (masks 21/19).</summary>
    public static LauncherUpdateChunkProfile V1 { get; } = new(
        Algorithm: "pcln-fastcdc-v1",
        MinimumSize: 256 * 1024,
        AverageSize: 1024 * 1024,
        MaximumSize: 2 * 1024 * 1024,
        EarlyMask: (1UL << 21) - 1,
        LateMask: (1UL << 19) - 1);

    /// <summary>
    /// pcln-fastcdc-v2: 128 KiB / 512 KiB / 1 MiB (masks 20/18).
    /// Mask spacing matches v1 relative to log2(avg).
    /// </summary>
    public static LauncherUpdateChunkProfile V2 { get; } = new(
        Algorithm: "pcln-fastcdc-v2",
        MinimumSize: 128 * 1024,
        AverageSize: 512 * 1024,
        MaximumSize: 1024 * 1024,
        EarlyMask: (1UL << 20) - 1,
        LateMask: (1UL << 18) - 1);

    public static bool TryGet(string? algorithm, out LauncherUpdateChunkProfile profile)
    {
        if (string.Equals(algorithm, V1.Algorithm, StringComparison.Ordinal))
        {
            profile = V1;
            return true;
        }

        if (string.Equals(algorithm, V2.Algorithm, StringComparison.Ordinal))
        {
            profile = V2;
            return true;
        }

        profile = V1;
        return false;
    }
}

internal static class LauncherUpdateChunker
{
    /// <summary>Legacy alias for v1 algorithm id (existing callers / tests).</summary>
    internal const string Algorithm = "pcln-fastcdc-v1";

    /// <summary>Legacy alias for v1 minimum chunk size.</summary>
    internal const int MinimumSize = 256 * 1024;

    /// <summary>Legacy alias for v1 average chunk size.</summary>
    internal const int AverageSize = 1024 * 1024;

    /// <summary>Legacy alias for v1 maximum chunk size.</summary>
    internal const int MaximumSize = 2 * 1024 * 1024;

    internal const string AlgorithmV2 = "pcln-fastcdc-v2";
    internal const string BlockMapLayoutV1 = "pcln-blockmap-v1";
    internal const string SingleFileBlockMapLayoutV1 = "pcln-blockmap-file-v1";
    internal const string BlockMapLayoutV2 = "pcln-blockmap-v2";
    internal const string SingleFileBlockMapLayoutV2 = "pcln-blockmap-file-v2";

    private const int ReadBufferSize = 256 * 1024;

    private static readonly ulong[] GearTable = BuildGearTable();

    internal static Task<IReadOnlyList<LauncherUpdateChunkSlice>> ChunkFileAsync(
        string path,
        CancellationToken cancellationToken) =>
        ChunkFileAsync(path, LauncherUpdateChunkProfile.V1, cancellationToken);

    /// <summary>
    /// Single sequential scan with ArrayPool slab buffers (protocol v2 §14).
    /// Hash is computed on the same in-memory slice before the buffer is reused.
    /// </summary>
    internal static async Task<IReadOnlyList<LauncherUpdateChunkSlice>> ChunkFileAsync(
        string path,
        LauncherUpdateChunkProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        List<LauncherUpdateChunkSlice> chunks = [];
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(profile.MaximumSize);
        try
        {
            int chunkLength = 0;
            long chunkOffset = 0;
            ulong rolling = 0;

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                int read = await stream.ReadAsync(
                        readBuffer.AsMemory(0, ReadBufferSize),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                for (int index = 0; index < read; index++)
                {
                    byte value = readBuffer[index];
                    chunkBuffer[chunkLength++] = value;
                    rolling = unchecked((rolling << 1) + GearTable[value]);
                    if (chunkLength < profile.MinimumSize)
                        continue;
                    ulong mask = chunkLength < profile.AverageSize ? profile.EarlyMask : profile.LateMask;
                    if ((rolling & mask) != 0 && chunkLength < profile.MaximumSize)
                        continue;
                    AddChunk(chunks, chunkBuffer.AsSpan(0, chunkLength), chunkOffset);
                    chunkOffset += chunkLength;
                    chunkLength = 0;
                    rolling = 0;
                }
            }

            if (chunkLength > 0 || chunks.Count == 0)
                AddChunk(chunks, chunkBuffer.AsSpan(0, chunkLength), chunkOffset);
            return chunks;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }
    }

    private static void AddChunk(
        List<LauncherUpdateChunkSlice> chunks,
        ReadOnlySpan<byte> content,
        long offset)
    {
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        chunks.Add(new LauncherUpdateChunkSlice(sha256, offset, content.Length));
    }

    private static ulong[] BuildGearTable()
    {
        ulong[] table = new ulong[256];
        for (int index = 0; index < table.Length; index++)
            table[index] = SplitMix64((ulong)index);
        return table;
    }

    private static ulong SplitMix64(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}

internal sealed record LauncherUpdateChunkSlice(string Sha256, long Offset, int Size);
