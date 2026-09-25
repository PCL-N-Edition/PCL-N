using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    internal static async ValueTask UpdateArchivesEnforceActualBudgets()
    {
        string root = CreateTempDirectory();
        try
        {
            string archive = Path.Combine(root, "input.zip");
            void WriteZip(params string[] names)
            {
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
                zip.CreateEntry("./");
                foreach (string name in names)
                {
                    using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                    stream.Write(new byte[32]);
                }
            }
            async Task Rejected(Func<Task> action)
            {
                bool rejected = false;
                try { await action(); } catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected);
            }
            WriteZip("a");
            byte[] valid = File.ReadAllBytes(archive);
            foreach (int declared in new[] { 4, 64 })
            {
                byte[] corrupt = (byte[])valid.Clone();
                for (int i = 0; i <= corrupt.Length - 28; i++)
                    if (BinaryPrimitives.ReadUInt32LittleEndian(corrupt.AsSpan(i)) == 0x02014b50)
                        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(i + 24), (uint)declared);
                File.WriteAllBytes(archive, corrupt);
                string staged = Path.Combine(root, "mismatch" + declared);
                await Rejected(() => UpdatePayloadExtractor.ExtractZipAsync(archive, staged));
                AssertFalse(File.Exists(Path.Combine(staged, "a")));
                var manifest = new UpdateScatterPatchManifest
                {
                    Ops = [new() { Op = "add", Path = "a", Blob = "a", BlobSize = declared }],
                };
                string scatter = Path.Combine(root, "scatter" + declared);
                await Rejected(() => new UpdatePatchApplier(new FakeRunner()).ApplyScatterOpsAsync(manifest, archive, root, scatter));
                AssertFalse(File.Exists(Path.Combine(scatter, "a")));
            }
            File.Delete(archive);
            WriteZip("a", "b");
            var limits = new UpdateArchiveLimits { MaximumExpandedBytes = 40 };
            string total = Path.Combine(root, "total");
            await Rejected(() => UpdatePayloadExtractor.ExtractZipAsync(archive, total, limits));
            AssertEqual(32L, new FileInfo(Path.Combine(total, "a")).Length);
            AssertFalse(File.Exists(Path.Combine(total, "b")));
            var pair = new UpdateScatterPatchManifest
            {
                Ops = [
                new() { Op = "add", Path = "a", Blob = "a", BlobSize = 32 },
                new() { Op = "add", Path = "b", Blob = "b", BlobSize = 32 }]
            };
            string scatterTotal = Path.Combine(root, "scatter-total");
            await Rejected(() => new UpdatePatchApplier(new FakeRunner()).ApplyScatterOpsAsync(pair, archive, root, scatterTotal, limits));
            AssertFalse(File.Exists(Path.Combine(scatterTotal, "b")));
            await Rejected(() => UpdatePayloadExtractor.ExtractZipAsync(archive, Path.Combine(root, "file-limit"), limits with { MaximumFileBytes = 16 }));
            string tar = Path.Combine(root, "input.tar");
            using (var writer = new TarWriter(File.Create(tar)))
                foreach (string name in new[] { "a", "b" })
                {
                    using var data = new MemoryStream(new byte[32]);
                    writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
                }
            string tarTotal = Path.Combine(root, "tar-total");
            await Rejected(() => UpdatePayloadExtractor.ExtractTarAsync(tar, tarTotal, limits));
            AssertFalse(File.Exists(Path.Combine(tarTotal, "b")));
            var inventory = await UpdatePayloadExtractor.ExtractZipAsync(archive, Path.Combine(root, "valid"), limits with { MaximumExpandedBytes = 64 });
            AssertEqual(2, inventory.Count);
            // CreateNew failure must never remove another transaction's existing file.
            bool collision = false;
            try { await UpdatePayloadExtractor.ExtractZipAsync(archive, Path.Combine(root, "valid")); }
            catch (IOException) { collision = true; }
            AssertTrue(collision);
            AssertEqual(32L, new FileInfo(Path.Combine(root, "valid", "a")).Length);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool stopped = false;
            try { await UpdatePayloadExtractor.ExtractZipAsync(archive, Path.Combine(root, "cancelled"), cancellationToken: cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            AssertTrue(stopped);
            AssertFalse(File.Exists(Path.Combine(root, "cancelled", "a")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
