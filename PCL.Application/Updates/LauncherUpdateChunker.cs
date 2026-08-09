// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;

namespace PCL.Application.Updates;

internal static class LauncherUpdateChunker
{
    internal const string Algorithm = "pcln-fastcdc-v1";
    internal const int MinimumSize = 256 * 1024;
    internal const int AverageSize = 1024 * 1024;
    internal const int MaximumSize = 2 * 1024 * 1024;
    private const ulong EarlyMask = (1UL << 21) - 1;
    private const ulong LateMask = (1UL << 19) - 1;
    private static readonly ulong[] GearTable = BuildGearTable();

    internal static async Task<IReadOnlyList<LauncherUpdateChunkSlice>> ChunkFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        List<LauncherUpdateChunkSlice> chunks = [];
        byte[] readBuffer = new byte[128 * 1024];
        byte[] chunkBuffer = new byte[MaximumSize];
        int chunkLength = 0;
        long chunkOffset = 0;
        ulong rolling = 0;

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            readBuffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            int read = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            for (int index = 0; index < read; index++)
            {
                byte value = readBuffer[index];
                chunkBuffer[chunkLength++] = value;
                rolling = unchecked((rolling << 1) + GearTable[value]);
                if (chunkLength < MinimumSize)
                    continue;
                ulong mask = chunkLength < AverageSize ? EarlyMask : LateMask;
                if ((rolling & mask) != 0 && chunkLength < MaximumSize)
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
