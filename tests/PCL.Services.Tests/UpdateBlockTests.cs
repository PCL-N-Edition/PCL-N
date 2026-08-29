using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PCL.Services.Updates;
using ZstdSharp;

namespace PCL.Services.Tests;

// XSR-507: update block data contracts — FastCDC chunking determinism, block codec
// normalization/detection/verification, and the local block index's verify-before-reuse
// rules, all migrated from the legacy updater with format parity.
internal static partial class Program
{
    private static byte[] DeterministicBytes(int length, byte seed = 0x5A)
    {
        byte[] data = new byte[length];
        byte state = seed;
        for (int index = 0; index < length; index++)
        {
            unchecked
            {
                state = (byte)(state * 31 + 17);
                data[index] = state;
            }
        }

        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    internal static ValueTask ChunkProfilesMatchLegacyBounds()
    {
        AssertEqual("pcln-fastcdc-v1", UpdateChunkProfile.V1.Algorithm);
        AssertEqual(256 * 1024, UpdateChunkProfile.V1.MinimumSize);
        AssertEqual(1024 * 1024, UpdateChunkProfile.V1.AverageSize);
        AssertEqual(2 * 1024 * 1024, UpdateChunkProfile.V1.MaximumSize);
        AssertEqual((1UL << 21) - 1, UpdateChunkProfile.V1.EarlyMask);
        AssertEqual((1UL << 19) - 1, UpdateChunkProfile.V1.LateMask);

        AssertEqual("pcln-fastcdc-v2", UpdateChunkProfile.V2.Algorithm);
        AssertEqual(128 * 1024, UpdateChunkProfile.V2.MinimumSize);
        AssertEqual(512 * 1024, UpdateChunkProfile.V2.AverageSize);
        AssertEqual(1024 * 1024, UpdateChunkProfile.V2.MaximumSize);
        AssertEqual((1UL << 20) - 1, UpdateChunkProfile.V2.EarlyMask);
        AssertEqual((1UL << 18) - 1, UpdateChunkProfile.V2.LateMask);

        AssertTrue(UpdateChunkProfile.TryGet("pcln-fastcdc-v2", out UpdateChunkProfile v2) && v2.Algorithm == "pcln-fastcdc-v2");
        AssertFalse(UpdateChunkProfile.TryGet("pcln-fastcdc-v9", out UpdateChunkProfile fallback));
        AssertTrue(ReferenceEquals(UpdateChunkProfile.V1, fallback));
        AssertTrue(UpdateChunker.BlockMapLayoutV1 == "pcln-blockmap-v1");
        AssertTrue(UpdateChunker.SingleFileBlockMapLayoutV1 == "pcln-blockmap-file-v1");
        AssertTrue(UpdateChunker.BlockMapLayoutV2 == "pcln-blockmap-v2");
        AssertTrue(UpdateChunker.SingleFileBlockMapLayoutV2 == "pcln-blockmap-file-v2");
        return ValueTask.CompletedTask;
    }

    internal static async ValueTask ChunkerIsDeterministicAndCoversTheFile()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "payload.bin");
            byte[] payload = DeterministicBytes(6 * 1024 * 1024);
            await File.WriteAllBytesAsync(path, payload);

            IReadOnlyList<UpdateChunkSlice> first = await UpdateChunker.ChunkFileAsync(path);
            IReadOnlyList<UpdateChunkSlice> second = await UpdateChunker.ChunkFileAsync(path);
            AssertTrue(first.Select(static slice => (slice.Sha256, slice.Offset, slice.Size))
                .SequenceEqual(second.Select(static slice => (slice.Sha256, slice.Offset, slice.Size))));

            long offset = 0;
            for (int index = 0; index < first.Count; index++)
            {
                UpdateChunkSlice slice = first[index];
                AssertEqual(offset, slice.Offset);
                AssertTrue(slice.Size > 0);
                AssertTrue(slice.Size <= UpdateChunkProfile.V1.MaximumSize);
                if (index < first.Count - 1)
                {
                    AssertTrue(slice.Size >= UpdateChunkProfile.V1.MinimumSize);
                }

                AssertEqual(
                    Convert.ToHexStringLower(SHA256.HashData(payload[(int)offset..(int)(offset + slice.Size)])),
                    slice.Sha256);
                offset += slice.Size;
            }

            AssertEqual(payload.Length, offset);
            AssertTrue(first.Count > 1);

            // A file below the minimum size is one chunk covering the whole file.
            string smallPath = Path.Combine(directory, "small.bin");
            byte[] small = [0x01, 0x02, 0x03];
            await File.WriteAllBytesAsync(smallPath, small);
            IReadOnlyList<UpdateChunkSlice> smallChunks = await UpdateChunker.ChunkFileAsync(smallPath);
            AssertEqual(1, smallChunks.Count);
            AssertEqual(0, smallChunks[0].Offset);
            AssertEqual(3, smallChunks[0].Size);
            AssertEqual(Convert.ToHexStringLower(SHA256.HashData(small)), smallChunks[0].Sha256);

            // The v2 profile is a different contract: same file, different boundaries.
            IReadOnlyList<UpdateChunkSlice> v2Chunks = await UpdateChunker.ChunkFileAsync(path, UpdateChunkProfile.V2);
            bool boundariesDiffer = v2Chunks.Count != first.Count
                || !v2Chunks.Zip(first, static (v2, v1) => (v2.Offset, v2.Size))
                    .SequenceEqual(first.Select(static v1 => (v1.Offset, v1.Size)));
            AssertTrue(boundariesDiffer);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static ValueTask BlockCodecNormalizesAndDetectsCodecs()
    {
        AssertEqual("gzip", UpdateBlockCodec.Normalize(null));
        AssertEqual("gzip", UpdateBlockCodec.Normalize("  "));
        AssertEqual("gzip", UpdateBlockCodec.Normalize(" gz "));
        AssertEqual("gzip", UpdateBlockCodec.Normalize("GZIP"));
        AssertEqual("zstd", UpdateBlockCodec.Normalize("ZSTD"));
        AssertEqual("zstd", UpdateBlockCodec.Normalize("zstandard"));
        AssertTrue(UpdateBlockCodec.IsSupported("zst"));
        AssertFalse(UpdateBlockCodec.IsSupported("brotli"));

        bool threw = false;
        try
        {
            UpdateBlockCodec.Normalize("lz4");
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        AssertTrue(threw);

        AssertEqual("gzip", UpdateBlockCodec.Detect([0x1f, 0x8b, 0x08, 0x00]));
        AssertEqual("zstd", UpdateBlockCodec.Detect([0x28, 0xb5, 0x2f, 0xfd]));
        AssertNull(UpdateBlockCodec.Detect([0x1f]));
        AssertNull(UpdateBlockCodec.Detect([0x00, 0x01, 0x02, 0x03]));
        return ValueTask.CompletedTask;
    }

    internal static async ValueTask BlockCodecRoundTripsAndVerifiesBothCodecs()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] payload = DeterministicBytes(300_000, 0x77);
            string hash = Sha(payload);

            foreach (string codec in (string[])["gzip", "zstd"])
            {
                MemoryStream compressed = new();
                Stream compressor = codec == "gzip"
                    ? new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true)
                    : new CompressionStream(compressed, leaveOpen: true);
                await using (compressor)
                {
                    await compressor.WriteAsync(payload);
                    await compressor.FlushAsync();
                }

                byte[] compressedBytes = compressed.ToArray();

                // Decompress from a stream that demands multiple reads to exercise the prefix replay.
                string target = Path.Combine(directory, codec + ".bin");
                await UpdateBlockCodec.DecompressAndVerifyAsync(
                    new MemoryStream(compressedBytes),
                    codec,
                    hash,
                    payload.Length,
                    target,
                    CancellationToken.None);
                AssertTrue(File.ReadAllBytes(target).SequenceEqual(payload));

                bool badHash = false;
                try
                {
                    await UpdateBlockCodec.DecompressAndVerifyAsync(
                        new MemoryStream(compressedBytes),
                        codec,
                        new string('0', 64),
                        payload.Length,
                        target + ".bad",
                        CancellationToken.None);
                }
                catch (InvalidDataException failure)
                {
                    badHash = failure.Message.Contains("SHA-256", StringComparison.Ordinal);
                }

                AssertTrue(badHash);

                bool sizeExceeded = false;
                try
                {
                    await UpdateBlockCodec.DecompressAndVerifyAsync(
                        new MemoryStream(compressedBytes),
                        codec,
                        hash,
                        payload.Length - 1,
                        target + ".over",
                        CancellationToken.None);
                }
                catch (InvalidDataException failure)
                {
                    sizeExceeded = failure.Message.Contains("超限", StringComparison.Ordinal);
                }

                AssertTrue(sizeExceeded);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async ValueTask BlockCodecDetectsMismatchedDeclaration()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] payload = DeterministicBytes(64_000, 0xEE);
            MemoryStream compressed = new();
            await using (CompressionStream zstd = new(compressed, leaveOpen: true))
            {
                await zstd.WriteAsync(payload);
                await zstd.FlushAsync();
            }

            string target = Path.Combine(directory, "declared-gzip.bin");
            await UpdateBlockCodec.DecompressAndVerifyAsync(
                new MemoryStream(compressed.ToArray()),
                "gzip",
                Sha(payload),
                payload.Length,
                target,
                CancellationToken.None);
            AssertTrue(File.ReadAllBytes(target).SequenceEqual(payload));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static ValueTask LocalBlockIndexRoundTripsTheInstalledMap()
    {
        string directory = CreateTempDirectory();
        try
        {
            AssertNull(UpdateLocalBlockIndex.TryLoadInstalledMap(directory));

            UpdateBlockMap map = new()
            {
                FormatVersion = 2,
                Layout = UpdateChunker.BlockMapLayoutV2,
                Algorithm = UpdateChunkProfile.V1.Algorithm,
                Compression = UpdateBlockCodec.Zstd,
                BlockBasePath = "blocks",
                Chunking = new UpdateChunkingParameters { Min = 262144, Avg = 1048576, Max = 2097152 },
                TargetTag = "v3.0.0",
                TargetVersion = "3.0.0",
                TargetFiles =
                [
                    new UpdateBlockFile
                    {
                        Path = "PCL.exe",
                        Sha256 = Sha([0x01]),
                        Size = 2,
                        UnixMode = 493,
                        Chunks =
                        [
                            new UpdateBlock
                            {
                                Sha256 = Sha([0x0A]),
                                Size = 1,
                                Full = new UpdateBlockFull { Path = "blocks/aa", CompressedSize = 9, Compression = "zstd" },
                                Deltas =
                                [
                                    new UpdateBlockDelta
                                    {
                                        Algorithm = "vcdiff",
                                        SourceChunks = [Sha([0x01])],
                                        SourceSha256 = Sha([0x01]),
                                        SourceSize = 1,
                                        Path = "delta/1",
                                        Size = 3,
                                    },
                                ],
                            },
                            new UpdateBlock { Sha256 = Sha([0x0B]), Size = 1, CompressedSize = 8, Path = "blocks/bb" },
                        ],
                    },
                ],
            };

            UpdateLocalBlockIndex.SaveInstalledMap(directory, map);
            AssertTrue(File.Exists(UpdateLocalBlockIndex.GetInstalledMapPath(directory)));

            UpdateBlockMap? loaded = UpdateLocalBlockIndex.TryLoadInstalledMap(directory);
            AssertTrue(loaded is not null);
            UpdateBlockMap restored = loaded!;
            AssertEqual(2, restored.FormatVersion);
            AssertEqual(UpdateChunker.BlockMapLayoutV2, restored.Layout);
            AssertEqual("zstd", restored.Compression);
            AssertEqual(262144, restored.Chunking!.Min);
            AssertEqual(1048576, restored.Chunking.Avg);
            AssertEqual(2097152, restored.Chunking.Max);
            UpdateBlock firstChunk = restored.TargetFiles[0].Chunks[0];
            AssertEqual("blocks/aa", firstChunk.ResolveFullPath());
            AssertEqual(9, firstChunk.ResolveCompressedSize());
            AssertEqual("zstd", firstChunk.ResolveCompression("gzip"));
            AssertEqual("blocks/bb", restored.TargetFiles[0].Chunks[1].ResolveFullPath());
            AssertEqual("vcdiff", restored.TargetFiles[0].Chunks[0].Deltas![0].Algorithm);
            AssertEqual(493, restored.TargetFiles[0].UnixMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask LocalBlockIndexVerifiesBeforeReusingChunks()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] partA = DeterministicBytes(100, 0x11);
            byte[] partB = DeterministicBytes(100, 0x22);
            byte[] whole = [.. partA, .. partB];
            string hashA = Sha(partA);
            string hashB = Sha(partB);
            string relative = "data/file.bin";
            string absolutePath = Path.Combine(directory, "data", "file.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.WriteAllBytes(absolutePath, whole);

            UpdateBlockMap map = new()
            {
                Algorithm = "pcln-fastcdc-v1",
                TargetFiles =
                [
                    new UpdateBlockFile
                    {
                        Path = relative,
                        Sha256 = Sha(whole),
                        Size = whole.Length,
                        Chunks =
                        [
                            new UpdateBlock { Sha256 = hashA, Size = partA.Length },
                            new UpdateBlock { Sha256 = hashB, Size = partB.Length },
                        ],
                    },
                ],
            };

            Dictionary<string, LocalBlockSource> index = UpdateLocalBlockIndex.TryIndexFromInstalledMap(
                directory, map, "pcln-fastcdc-v1", [hashA, hashB]);
            AssertEqual(2, index.Count);
            AssertEqual(0, index[hashA].Offset);
            AssertEqual(100, index[hashA].Size);
            AssertEqual(100, index[hashB].Offset);
            AssertTrue(index[hashB].Path.EndsWith(Path.Combine("data", "file.bin"), StringComparison.Ordinal));

            // An unknown needed hash simply is not indexed.
            AssertEqual(0, UpdateLocalBlockIndex.TryIndexFromInstalledMap(
                directory, map, "pcln-fastcdc-v1", [new string('f', 64)]).Count);

            // Algorithm mismatch means the map cannot be trusted.
            AssertEqual(0, UpdateLocalBlockIndex.TryIndexFromInstalledMap(
                directory, map, "pcln-fastcdc-v2", [hashA]).Count);

            // Wrong declared size means the file cannot be trusted.
            map.TargetFiles[0].Size = whole.Length + 1;
            AssertEqual(0, UpdateLocalBlockIndex.TryIndexFromInstalledMap(
                directory, map, "pcln-fastcdc-v1", [hashA]).Count);
            map.TargetFiles[0].Size = whole.Length;

            // A file whose content no longer matches its declared hash cannot be trusted.
            map.TargetFiles[0].Sha256 = Sha([0xFF]);
            AssertEqual(0, UpdateLocalBlockIndex.TryIndexFromInstalledMap(
                directory, map, "pcln-fastcdc-v1", [hashA]).Count);
            map.TargetFiles[0].Sha256 = Sha(whole);

            // Paths escaping the installation root are skipped.
            UpdateBlockMap escaping = new()
            {
                Algorithm = "pcln-fastcdc-v1",
                TargetFiles =
                [
                    new UpdateBlockFile
                    {
                        Path = "../../outside.bin",
                        Size = whole.Length,
                        Chunks = [new UpdateBlock { Sha256 = hashA, Size = partA.Length }],
                    },
                ],
            };
            AssertEqual(0, UpdateLocalBlockIndex.TryIndexFromInstalledMap(
                directory, escaping, "pcln-fastcdc-v1", [hashA]).Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask LocalBlockIndexReadsVerifiedWindows()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] partA = DeterministicBytes(100, 0x33);
            byte[] partB = DeterministicBytes(100, 0x44);
            byte[] whole = [.. partA, .. partB];
            string hashA = Sha(partA);
            string hashB = Sha(partB);
            string absolutePath = Path.Combine(directory, "file.bin");
            File.WriteAllBytes(absolutePath, whole);

            UpdateBlockMap map = new()
            {
                Algorithm = "pcln-fastcdc-v1",
                TargetFiles =
                [
                    new UpdateBlockFile
                    {
                        Path = "file.bin",
                        Sha256 = Sha(whole),
                        Size = whole.Length,
                        Chunks =
                        [
                            new UpdateBlock { Sha256 = hashA, Size = partA.Length },
                            new UpdateBlock { Sha256 = hashB, Size = partB.Length },
                        ],
                    },
                ],
            };

            Dictionary<string, LocalBlockSource> index = UpdateLocalBlockIndex.TryIndexFromInstalledMap(
                directory, map, "pcln-fastcdc-v1", [hashA, hashB]);

            byte[]? window = UpdateLocalBlockIndex.TryReadSourceWindow(
                [hashA, hashB], Sha(whole), whole.Length, index);
            AssertTrue(window is not null);
            AssertTrue(window.SequenceEqual(whole));

            AssertNull(UpdateLocalBlockIndex.TryReadSourceWindow(
                [hashA, hashB], new string('0', 64), whole.Length, index));
            AssertNull(UpdateLocalBlockIndex.TryReadSourceWindow(
                [hashA, new string('e', 64)], Sha(whole), whole.Length, index));
            AssertNull(UpdateLocalBlockIndex.TryReadSourceWindow(
                [hashA, hashB], Sha(whole), whole.Length + 1, index));
            AssertNull(UpdateLocalBlockIndex.TryReadSourceWindow([], Sha(whole), 0, index));

            // A corrupted source file fails the per-chunk hash check.
            byte[] corrupted = [.. whole];
            corrupted[0] ^= 0xFF;
            File.WriteAllBytes(absolutePath, corrupted);
            AssertNull(UpdateLocalBlockIndex.TryReadSourceWindow([hashA, hashB], Sha(whole), whole.Length, index));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
