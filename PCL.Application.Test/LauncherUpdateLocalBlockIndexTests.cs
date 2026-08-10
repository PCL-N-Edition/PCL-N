// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using PCL.Application.Updates;

namespace PCL.Application.Test;

[TestClass]
public sealed class LauncherUpdateLocalBlockIndexTests
{
    [TestMethod]
    public async Task SaveAndLoad_RoundTripsInstalledMap()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-lbi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            LauncherUpdateBlockMap map = new()
            {
                FormatVersion = 2,
                Layout = "pcln-blockmap-file-v2",
                Algorithm = LauncherUpdateChunkProfile.V2.Algorithm,
                Compression = "gzip",
                BlockBasePath = "/v1/updates/block",
                TargetFiles =
                [
                    new LauncherUpdateBlockFile
                    {
                        Path = "PCL-N-Edition.exe",
                        Sha256 = Convert.ToHexStringLower(SHA256.HashData("abc"u8.ToArray())),
                        Size = 3,
                        Chunks =
                        [
                            new LauncherUpdateBlock
                            {
                                Sha256 = Convert.ToHexStringLower(SHA256.HashData("abc"u8.ToArray())),
                                Size = 3,
                                CompressedSize = 10,
                                Path = "block/00/placeholder"
                            }
                        ]
                    }
                ]
            };

            await LauncherUpdateLocalBlockIndex.SaveInstalledMapAsync(root, map, CancellationToken.None);
            Assert.IsTrue(File.Exists(LauncherUpdateLocalBlockIndex.GetInstalledMapPath(root)));

            LauncherUpdateBlockMap? loaded =
                await LauncherUpdateLocalBlockIndex.TryLoadInstalledMapAsync(root, CancellationToken.None);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(map.Algorithm, loaded!.Algorithm);
            Assert.AreEqual(1, loaded.TargetFiles.Count);
            Assert.AreEqual("PCL-N-Edition.exe", loaded.TargetFiles[0].Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task IndexFromInstalledMap_ResolvesOffsetsWhenFileMatches()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-lbi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] a = Encoding.UTF8.GetBytes("AAAA");
            byte[] b = Encoding.UTF8.GetBytes("BBBBBB");
            byte[] file = a.Concat(b).ToArray();
            string relative = "host/PCL-N-Host.exe";
            string absolute = Path.Combine(root, "host", "PCL-N-Host.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            await File.WriteAllBytesAsync(absolute, file);

            string shaA = Convert.ToHexStringLower(SHA256.HashData(a));
            string shaB = Convert.ToHexStringLower(SHA256.HashData(b));
            string fileSha = Convert.ToHexStringLower(SHA256.HashData(file));

            LauncherUpdateBlockMap installed = new()
            {
                Algorithm = LauncherUpdateChunkProfile.V2.Algorithm,
                TargetFiles =
                [
                    new LauncherUpdateBlockFile
                    {
                        Path = relative.Replace('\\', '/'),
                        Sha256 = fileSha,
                        Size = file.Length,
                        Chunks =
                        [
                            new LauncherUpdateBlock { Sha256 = shaA, Size = a.Length },
                            new LauncherUpdateBlock { Sha256 = shaB, Size = b.Length }
                        ]
                    }
                ]
            };

            HashSet<string> needed = new(StringComparer.Ordinal) { shaB };
            Dictionary<string, LocalBlockSource> index =
                await LauncherUpdateLocalBlockIndex.TryIndexFromInstalledMapAsync(
                    root,
                    installed,
                    LauncherUpdateChunkProfile.V2.Algorithm,
                    needed,
                    CancellationToken.None);

            Assert.AreEqual(1, index.Count);
            Assert.IsTrue(index.ContainsKey(shaB));
            Assert.AreEqual(a.Length, index[shaB].Offset);
            Assert.AreEqual(b.Length, index[shaB].Size);
            Assert.AreEqual(Path.GetFullPath(absolute), Path.GetFullPath(index[shaB].Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SourceWindow_VerifiesConcatenatedChunks()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-lbi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] a = Encoding.UTF8.GetBytes("SRC-A");
            byte[] b = Encoding.UTF8.GetBytes("SRC-B");
            string path = Path.Combine(root, "file.bin");
            await File.WriteAllBytesAsync(path, a.Concat(b).ToArray());
            string shaA = Convert.ToHexStringLower(SHA256.HashData(a));
            string shaB = Convert.ToHexStringLower(SHA256.HashData(b));
            byte[] window = a.Concat(b).ToArray();
            string windowSha = Convert.ToHexStringLower(SHA256.HashData(window));

            Dictionary<string, LocalBlockSource> local = new(StringComparer.Ordinal)
            {
                [shaA] = new LocalBlockSource(path, 0, a.Length),
                [shaB] = new LocalBlockSource(path, a.Length, b.Length)
            };

            byte[]? read = await LauncherUpdateLocalBlockIndex.TryReadSourceWindowAsync(
                [shaA, shaB],
                windowSha,
                window.Length,
                local,
                CancellationToken.None);

            Assert.IsNotNull(read);
            CollectionAssert.AreEqual(window, read);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Vcdiff_RejectsBadMagic()
    {
        Assert.IsFalse(LauncherUpdateVcdiff.TryDecode([1, 2, 3, 4], [], out _));
    }

    [TestMethod]
    public void Vcdiff_DecodesPythonPublisherSample()
    {
        // Sample produced by scripts/pcln_vcdiff.py encode (RFC 3284, no secondary compression).
        string sampleDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "scripts",
            "tests"));
        string deltaPath = Path.Combine(sampleDir, "_vcdiff_sample.bin");
        string sourcePath = Path.Combine(sampleDir, "_vcdiff_source.bin");
        string targetPath = Path.Combine(sampleDir, "_vcdiff_target.bin");
        if (!File.Exists(deltaPath) || !File.Exists(sourcePath) || !File.Exists(targetPath))
            Assert.Inconclusive("Python VCDIFF sample files not generated; run scripts/pcln_vcdiff sample once.");

        byte[] delta = File.ReadAllBytes(deltaPath);
        byte[] source = File.ReadAllBytes(sourcePath);
        byte[] expected = File.ReadAllBytes(targetPath);
        Assert.IsTrue(LauncherUpdateVcdiff.TryDecode(delta, source, out byte[] actual), "VCDIFF decode failed");
        CollectionAssert.AreEqual(expected, actual);
    }
}
