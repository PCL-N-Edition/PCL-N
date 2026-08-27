// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL.Application.Updates;

namespace PCL.Application.Test;

[TestClass]
public sealed class LauncherUpdateBlockTests
{
    [TestMethod]
    public async Task BlockCodec_UsesActualGzipMagicWhenMapDeclaresZstd()
    {
        byte[] payload = Encoding.UTF8.GetBytes("canonical CAS block created by an older gzip release");
        byte[] compressed = Gzip(payload);
        string temporary = Path.Combine(Path.GetTempPath(), "pcln-codec-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using MemoryStream source = new(compressed, writable: false);
            await LauncherUpdateBlockCodec.DecompressAndVerifyAsync(
                source,
                LauncherUpdateBlockCodec.Zstd,
                Hash(payload),
                payload.Length,
                temporary,
                CancellationToken.None);

            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(temporary));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [TestMethod]
    public async Task Chunker_MatchesReleaseGeneratorVector()
    {
        string path = await WriteChunkerGoldenPayloadAsync().ConfigureAwait(false);
        try
        {
            IReadOnlyList<LauncherUpdateChunkSlice> chunks = await LauncherUpdateChunker.ChunkFileAsync(
                path,
                CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { 441644, 488587, 960062, 1911641, 1441069 },
                chunks.Select(static chunk => chunk.Size).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    "1a66421d9e4e731202105990f7be9e1d479d1d7126e5e998561e6a9b5a0c4233",
                    "d5b76bd36a4a90e36111075ec45eafe1a69b40810c8d26abed246d6d65890362",
                    "fc9f0e70f17011dd8fa48b9cc4c6b69a42d583b26346af5e53d9332538600963",
                    "8e6d652a837af555b9be913d2080bf8c58a3b8d85d37a0b9c53b19f881272020",
                    "df4cffffca7c46bbe2b8a5f21e0a2e29430c965ec1042ee4a41bba72e80ebdf9"
                },
                chunks.Select(static chunk => chunk.Sha256).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Chunker_V2MatchesReleaseGeneratorVector()
    {
        string path = await WriteChunkerGoldenPayloadAsync().ConfigureAwait(false);
        try
        {
            IReadOnlyList<LauncherUpdateChunkSlice> chunks = await LauncherUpdateChunker.ChunkFileAsync(
                path,
                LauncherUpdateChunkProfile.V2,
                CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { 441644, 488587, 587966, 372096, 596725, 715315, 599601, 489564, 489713, 461792 },
                chunks.Select(static chunk => chunk.Size).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    "1a66421d9e4e731202105990f7be9e1d479d1d7126e5e998561e6a9b5a0c4233",
                    "d5b76bd36a4a90e36111075ec45eafe1a69b40810c8d26abed246d6d65890362",
                    "31cf6b43722d232b66227f6d7a927628624a3ac181a90377b3f8043a13c95ba7",
                    "e76fb10f493466c4031f96b0a2c63d6b76749e8d4d7f6a76e19c1401387de236",
                    "90e92eb20874a71306b097c6d315648a5e0e1d4e7e15c08c698bcb82a7e75518",
                    "c3c222a7e0352f5cc0f0742c3344c1e080140219e53b54b7fc7fb103deae0820",
                    "8ffcc231ea1ae070b5df83bd24d640e4ca2777484886c7921042855f7e7f29f9",
                    "3f42647f1c84bd39b27613a6b29fadba83b6516282b316a86611d977d15b9d91",
                    "edc38a95b4a2e2e8a2b168465e8ee5ac070c794391d11334f52316721a703296",
                    "b6e33aa5b1e408c9c709895ba3c54a39a431108cfec4444150f3635170fcf668"
                },
                chunks.Select(static chunk => chunk.Sha256).ToArray());
            Assert.IsTrue(chunks.All(static chunk => chunk.Size <= LauncherUpdateChunkProfile.V2.MaximumSize));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteChunkerGoldenPayloadAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "pcln-chunker-" + Guid.NewGuid().ToString("N"));
        byte[] buffer = new byte[128 * 1024];
        uint state = 123456789;
        int remaining = 5 * 1024 * 1024 + 123;
        await using FileStream stream = File.Create(path);
        while (remaining > 0)
        {
            int count = Math.Min(buffer.Length, remaining);
            for (int index = 0; index < count; index++)
            {
                state = unchecked(state * 1664525 + 1013904223);
                buffer[index] = (byte)(state >> 24);
            }
            await stream.WriteAsync(buffer.AsMemory(0, count));
            remaining -= count;
        }

        return path;
    }

    [TestMethod]
    public async Task Installer_RetriesTransientBlock404AndDownloadsOnlyMissingBlock()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-block-update-" + Guid.NewGuid().ToString("N"));
        string installRoot = Path.Combine(root, "install");
        string cacheRoot = Path.Combine(root, "cache");
        string hostRoot = Path.Combine(installRoot, "host");
        string sidecarRoot = Path.Combine(installRoot, "sidecar");
        Directory.CreateDirectory(hostRoot);
        Directory.CreateDirectory(sidecarRoot);
        try
        {
            string entryName = OperatingSystem.IsWindows() ? "PCL-N-Edition.exe" : "PCL-N-Edition";
            string helperName = OperatingSystem.IsWindows() ? "PCL-N-Host.exe" : "PCL-N-Host";
            string helperRelative = "host/" + helperName;
            byte[] entry = Encoding.UTF8.GetBytes("unchanged native launcher");
            byte[] oldHost = Encoding.UTF8.GetBytes("old update helper");
            byte[] newHost = Encoding.UTF8.GetBytes("new update helper");
            byte[] shared = Encoding.UTF8.GetBytes("this block already exists under another managed path");
            byte[] layout = Encoding.UTF8.GetBytes("pcln-scatter-v2-expanded\n");
            await File.WriteAllBytesAsync(Path.Combine(installRoot, entryName), entry);
            await File.WriteAllBytesAsync(Path.Combine(hostRoot, helperName), oldHost);
            await File.WriteAllBytesAsync(Path.Combine(sidecarRoot, "shared.bin"), shared);
            await File.WriteAllBytesAsync(Path.Combine(installRoot, "pcln-layout"), layout);

            Dictionary<string, byte[]> targetContent = new(StringComparer.Ordinal)
            {
                [entryName] = entry,
                [helperRelative] = newHost,
                ["native/shared.bin"] = shared,
                ["pcln-layout"] = layout
            };
            List<LauncherUpdateBlockFile> files = targetContent
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => CreateBlockFile(pair.Key, pair.Value))
                .ToList();
            LauncherUpdateBlockMap map = new()
            {
                FormatVersion = 1,
                Layout = "pcln-blockmap-v1",
                Algorithm = LauncherUpdateChunker.Algorithm,
                Compression = "gzip",
                BlockBasePath = "/v1/updates/block",
                TargetTag = "v2.0.0-beta",
                TargetVersion = "2.0.0-beta",
                RuntimeId = "win-x64",
                RuntimeVariant = "SelfContained",
                Configuration = "Beta",
                TargetAssetName = "PCL_N_Beta_win-x64_SelfContained.zip",
                TargetManifestSha256 = ManifestHash(files),
                TargetFiles = files
            };
            byte[] mapJson = JsonSerializer.SerializeToUtf8Bytes(
                map,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            string newHostSha = Hash(newHost);
            byte[] newHostCompressed = Gzip(newHost);
            byte[] fallbackArchive = Zip(entryName, entry);
            int blockRequests = 0;
            string? requestedBlockPath = null;
            using HttpClient client = new(new RoutingHandler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith(".blockmap.json", StringComparison.Ordinal))
                    return BytesResponse(mapJson, "application/json");
                if (path.EndsWith(".asc", StringComparison.Ordinal))
                    return BytesResponse([1], "application/pgp-signature");
                if (path.StartsWith("/v1/updates/block/", StringComparison.Ordinal))
                {
                    int requestNumber = Interlocked.Increment(ref blockRequests);
                    requestedBlockPath = path;
                    if (requestNumber == 1)
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    return path == $"/v1/updates/block/{newHostSha[..2]}/{newHostSha}"
                        ? BytesResponse(newHostCompressed, "application/gzip")
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                if (path.EndsWith(".zip", StringComparison.Ordinal))
                    return BytesResponse(fallbackArchive, "application/zip");
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }));
            using LauncherUpdateInstaller installer = new(client, new AcceptAllGpgVerifier());
            string? blockFailure = null;
            installer.ProgressChanged += (_, progress) =>
            {
                if (progress.Stage == LauncherUpdateStage.FallingBack)
                    blockFailure = progress.Message;
            };
            LauncherUpdatePackage package = new(
                "2.0.0-beta",
                "v2.0.0-beta",
                "https://api.test/v1/updates/releases/v2.0.0-beta/PCL_N_Beta_win-x64_SelfContained.zip",
                "PCL_N_Beta_win-x64_SelfContained.zip",
                entryName,
                Hash(entry),
                entry.Length,
                [],
                "win-x64",
                "SelfContained",
                "Beta",
                "https://api.test/full.asc",
                "https://api.test/binary.asc",
                BlockMapUrl: "https://api.test/v1/updates/releases/v2.0.0-beta/PCL_N_Beta_win-x64_SelfContained.blockmap.json",
                BlockMapSignatureUrl: "https://api.test/map.asc");

            PreparedLauncherUpdate prepared = await installer.PrepareWithBlockCacheAsync(
                package,
                Path.Combine(hostRoot, helperName),
                null,
                cacheRoot,
                CancellationToken.None);

            Assert.IsTrue(
                prepared.UsedBlockMap,
                $"{blockFailure}；blockRequests={blockRequests}；requested={requestedBlockPath}");
            Assert.IsFalse(prepared.UsedPatch);
            Assert.AreEqual(2, blockRequests);
            Assert.AreEqual($"/v1/updates/block/{newHostSha[..2]}/{newHostSha}", requestedBlockPath);
            CollectionAssert.AreEqual(newHost, await File.ReadAllBytesAsync(prepared.StagedExecutablePath));
            CollectionAssert.AreEqual(
                shared,
                await File.ReadAllBytesAsync(Path.Combine(prepared.StagedInstallDirectory!, "native", "shared.bin")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Installer_AcceptsGatewayContentLengthAndStillVerifiesBlockPayload()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-block-content-length-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string entryName = OperatingSystem.IsWindows() ? "PCL-N-Edition.exe" : "PCL-N-Edition";
            string currentExecutable = Path.Combine(root, entryName);
            byte[] current = Encoding.UTF8.GetBytes("old launcher");
            byte[] replacement = Encoding.UTF8.GetBytes("new launcher delivered through a recoding gateway");
            await File.WriteAllBytesAsync(currentExecutable, current);

            LauncherUpdateBlockFile target = CreateBlockFile(entryName, replacement);
            LauncherUpdateBlock block = target.Chunks.Single();
            LauncherUpdateBlockMap map = new()
            {
                FormatVersion = 1,
                Layout = "pcln-blockmap-file-v1",
                Algorithm = LauncherUpdateChunker.Algorithm,
                Compression = "gzip",
                BlockBasePath = "/v1/updates/block",
                TargetTag = "v2.0.0-beta",
                TargetVersion = "2.0.0-beta",
                RuntimeId = "win-x64",
                RuntimeVariant = "SelfContained",
                Configuration = "Beta",
                TargetAssetName = "PCL_N_Beta_win-x64_SelfContained_Portable.exe",
                TargetManifestSha256 = ManifestHash([target]),
                TargetFiles = [target]
            };
            byte[] mapJson = JsonSerializer.SerializeToUtf8Bytes(
                map,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            byte[] compressed = Gzip(replacement);

            using HttpClient client = new(new RoutingHandler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith(".blockmap.json", StringComparison.Ordinal))
                    return BytesResponse(mapJson, "application/json");
                if (path.EndsWith(".asc", StringComparison.Ordinal))
                    return BytesResponse([1], "application/pgp-signature");
                if (path == $"/v1/updates/block/{block.Sha256![..2]}/{block.Sha256}")
                {
                    HttpResponseMessage response = BytesResponse(compressed, "application/gzip");
                    response.Content.Headers.ContentLength = compressed.Length + 37;
                    return response;
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using LauncherUpdateInstaller installer = new(client, new AcceptAllGpgVerifier());
            LauncherUpdatePackage package = new(
                "2.0.0-beta",
                "v2.0.0-beta",
                "https://api.test/v1/updates/releases/v2.0.0-beta/PCL_N_Beta_win-x64_SelfContained_Portable.exe",
                "PCL_N_Beta_win-x64_SelfContained_Portable.exe",
                entryName,
                Hash(replacement),
                replacement.Length,
                [],
                "win-x64",
                "SelfContained",
                "Beta",
                "https://api.test/portable.asc",
                "https://api.test/portable.asc",
                BlockMapUrl: "https://api.test/v1/updates/releases/v2.0.0-beta/PCL_N_Beta_win-x64_SelfContained_Portable.blockmap.json",
                BlockMapSignatureUrl: "https://api.test/map.asc");

            PreparedLauncherUpdate prepared = await installer.PrepareWithBlockCacheAsync(
                package,
                currentExecutable,
                null,
                Path.Combine(root, "cache"),
                CancellationToken.None);

            Assert.IsTrue(prepared.UsedBlockMap);
            CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(prepared.StagedExecutablePath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Installer_DoesNotDownloadFullArchiveWhenRequiredBlockMapIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-block-required-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string entryName = OperatingSystem.IsWindows() ? "PCL-N-Edition.exe" : "PCL-N-Edition";
        string currentExecutable = Path.Combine(root, entryName);
        await File.WriteAllTextAsync(currentExecutable, "current launcher");
        int fullArchiveRequests = 0;
        try
        {
            using HttpClient client = new(new RoutingHandler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith(".blockmap.json", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path.EndsWith(".zip", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref fullArchiveRequests);
                    return BytesResponse(Zip(entryName, Encoding.UTF8.GetBytes("replacement")), "application/zip");
                }
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }));
            using LauncherUpdateInstaller installer = new(client, new AcceptAllGpgVerifier());
            LauncherUpdatePackage package = new(
                "2.0.0-beta",
                "v2.0.0-beta",
                "https://api.test/v1/updates/releases/v2.0.0-beta/PCL_N_Beta_win-x64_SelfContained.zip",
                "PCL_N_Beta_win-x64_SelfContained.zip",
                entryName,
                null,
                null,
                [],
                "win-x64",
                "SelfContained",
                "Beta",
                BlockMapUrl: "https://api.test/v1/updates/releases/v2.0.0-beta/PCL_N_Beta_win-x64_SelfContained.blockmap.json",
                BlockMapSignatureUrl: "https://api.test/map.asc");

            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                installer.PrepareWithBlockCacheAsync(
                    package,
                    currentExecutable,
                    null,
                    Path.Combine(root, "cache"),
                    CancellationToken.None));

            StringAssert.Contains(error.Message, "未执行不安全的整包回退");
            StringAssert.Contains(error.Message, package.BlockMapUrl!);
            Assert.AreEqual(0, fullArchiveRequests);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Installer_RebuildsSingleFileMapWithoutUsingScatterEntryName()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-portable-block-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string currentExecutable = Path.Combine(root, "PCL_N_Beta_win-x64_NoRuntime_Portable.exe");
        byte[] current = Encoding.UTF8.GetBytes("old portable launcher");
        byte[] replacement = Encoding.UTF8.GetBytes("new portable launcher with embedded payload");
        await File.WriteAllBytesAsync(currentExecutable, current);
        try
        {
            LauncherUpdateBlockFile target = CreateBlockFile("PCL-N-Edition.exe", replacement);
            LauncherUpdateBlockMap map = new()
            {
                FormatVersion = 1,
                Layout = "pcln-blockmap-file-v1",
                Algorithm = LauncherUpdateChunker.Algorithm,
                Compression = "gzip",
                BlockBasePath = "/v1/updates/block",
                TargetTag = "v1.4.4-beta",
                TargetVersion = "1.4.4-beta",
                RuntimeId = "win-x64",
                RuntimeVariant = "NoRuntime",
                Configuration = "Beta",
                TargetAssetName = "PCL_N_Beta_win-x64_NoRuntime_Portable.exe",
                TargetManifestSha256 = ManifestHash([target]),
                TargetFiles = [target]
            };
            byte[] mapJson = JsonSerializer.SerializeToUtf8Bytes(
                map,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            LauncherUpdateBlock block = target.Chunks.Single();
            byte[] compressed = Gzip(replacement);
            int fullRequests = 0;
            using HttpClient client = new(new RoutingHandler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith(".blockmap.json", StringComparison.Ordinal))
                    return BytesResponse(mapJson, "application/json");
                if (path.EndsWith(".asc", StringComparison.Ordinal))
                    return BytesResponse([1], "application/pgp-signature");
                if (path == $"/v1/updates/block/{block.Sha256![..2]}/{block.Sha256}")
                    return BytesResponse(compressed, "application/gzip");
                if (path.EndsWith("_Portable.exe", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref fullRequests);
                    return BytesResponse(replacement, "application/octet-stream");
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            using LauncherUpdateInstaller installer = new(client, new AcceptAllGpgVerifier());
            LauncherUpdatePackage package = new(
                "1.4.4-beta",
                "v1.4.4-beta",
                "https://api.test/v1/updates/releases/v1.4.4-beta/PCL_N_Beta_win-x64_NoRuntime_Portable.exe",
                "PCL_N_Beta_win-x64_NoRuntime_Portable.exe",
                "PCL-N-Edition.exe",
                Hash(replacement),
                replacement.Length,
                [],
                "win-x64",
                "NoRuntime",
                "Beta",
                "https://api.test/portable.asc",
                "https://api.test/portable.asc",
                BlockMapUrl: "https://api.test/v1/updates/releases/v1.4.4-beta/PCL_N_Beta_win-x64_NoRuntime_Portable.blockmap.json",
                BlockMapSignatureUrl: "https://api.test/map.asc");

            PreparedLauncherUpdate prepared = await installer.PrepareWithBlockCacheAsync(
                package,
                currentExecutable,
                null,
                Path.Combine(root, "cache"),
                CancellationToken.None);

            Assert.IsTrue(prepared.UsedBlockMap);
            Assert.IsNull(prepared.StagedInstallDirectory);
            Assert.IsNull(prepared.InstallPlanPath);
            CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(prepared.StagedExecutablePath));
            Assert.AreEqual(0, fullRequests);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static LauncherUpdateBlockFile CreateBlockFile(string path, byte[] content)
    {
        string sha256 = Hash(content);
        byte[] compressed = Gzip(content);
        return new LauncherUpdateBlockFile
        {
            Path = path,
            Sha256 = sha256,
            Size = content.Length,
            Chunks =
            [
                new LauncherUpdateBlock
                {
                    Sha256 = sha256,
                    Size = content.Length,
                    CompressedSize = compressed.Length,
                    Path = $"block/{sha256[..2]}/{sha256}"
                }
            ]
        };
    }

    private static string ManifestHash(IEnumerable<LauncherUpdateBlockFile> files)
    {
        string canonical = string.Concat(files.OrderBy(static file => file.Path, StringComparer.Ordinal)
            .Select(file => $"{file.Path}\t{file.Sha256}\t{file.Size}\n"));
        return Hash(Encoding.UTF8.GetBytes(canonical));
    }

    private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] Gzip(byte[] content)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(content);
        return result.ToArray();
    }

    private static byte[] Zip(string name, byte[] content)
    {
        using MemoryStream result = new();
        using (ZipArchive archive = new(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using Stream stream = entry.Open();
            stream.Write(content);
        }
        return result.ToArray();
    }

    private static HttpResponseMessage BytesResponse(byte[] content, string contentType)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private sealed class AcceptAllGpgVerifier : ILauncherGpgVerifier
    {
        public Task VerifyAsync(Stream content, Stream detachedSignature, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
