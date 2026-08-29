using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PCL.Services.Updates;

namespace PCL.Services.Tests;

// XSR-516: payload extraction and patch orchestration — zip/tar trees unpacked into a
// verified staged root, HDiffPatch through a process port, binary patch chains, and scatter
// bundle operations. A fake process runner and in-memory downloads keep everything offline.
internal static partial class Program
{
    private static string OrchSha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string Executable, string[] Arguments)> Invocations { get; } = [];

        public int ExitCode { get; set; }

        public string? OutputContent { get; set; }

        /// <summary>Models an identity patch: the output becomes the patch payload.</summary>
        public bool CopyPatchToOutput { get; set; }

        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add((executable, [.. arguments]));
            if (CopyPatchToOutput && arguments.Count >= 3)
            {
                File.Copy(arguments[1], arguments[2], overwrite: true);
            }
            else if (OutputContent is not null && arguments.Count >= 3)
            {
                File.WriteAllBytes(arguments[2], Encoding.ASCII.GetBytes(OutputContent));
            }

            return Task.FromResult(ExitCode);
        }
    }

    private static (string Source, string Patch, string Target) MakePatchScenario(
        string directory,
        string name,
        byte[] sourceContent,
        byte[] targetContent)
    {
        string source = Path.Combine(directory, name + ".src");
        File.WriteAllBytes(source, sourceContent);
        string patch = Path.Combine(directory, name + ".patch");
        File.WriteAllBytes(patch, targetContent); // identity patch: output equals patch payload
        string target = Path.Combine(directory, name + ".out");
        return (source, patch, target);
    }

    internal static void ZipPayloadsExtractWithTraversalRefusal()
    {
        string directory = CreateTempDirectory();
        try
        {
            string archivePath = Path.Combine(directory, "payload.zip");
            byte[] exe = [0x01, 0x02, 0x03, 0x04];
            byte[] config = "{\"mode\":1}"u8.ToArray();
            using (FileStream stream = File.Create(archivePath))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry exeEntry = archive.CreateEntry("bundle/app/PCL-N-Edition.exe");
                exeEntry.ExternalAttributes = 493 << 16;
                using (var exeStream = exeEntry.Open())
                {
                    exeStream.Write(exe);
                }

                using (var configStream = archive.CreateEntry("bundle/data/config.json").Open())
                {
                    configStream.Write(config);
                }
            }

            string staged = Path.Combine(directory, "staged");
            List<UpdateFileEntry> inventory = UpdatePayloadExtractor.ExtractZipAsync(archivePath, staged).GetAwaiter().GetResult();

            AssertEqual(2, inventory.Count);
            AssertTrue(inventory.All(entry => entry.Path!.StartsWith("bundle/", StringComparison.Ordinal)));
            AssertTrue(inventory.Any(entry => entry.Path == "bundle/app/PCL-N-Edition.exe" && entry.UnixMode == 493));
            AssertTrue(File.ReadAllBytes(Path.Combine(staged, "bundle", "app", "PCL-N-Edition.exe")).SequenceEqual(exe));
            AssertTrue(inventory.Single(entry => entry.Path!.EndsWith("config.json", StringComparison.Ordinal)).Sha256
                == OrchSha(config));

            // The wrapper root flattens so the tree is installable.
            UpdateStaging.FlattenSingleRoot(staged);
            AssertTrue(File.Exists(Path.Combine(staged, "app", "PCL-N-Edition.exe")));

            // Traversal entries are refused.
            string evilPath = Path.Combine(directory, "evil.zip");
            using (FileStream stream = File.Create(evilPath))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
            {
                archive.CreateEntry("../../evil.bin");
            }

            bool refused = false;
            try
            {
                UpdatePayloadExtractor.ExtractZipAsync(evilPath, Path.Combine(directory, "staged2")).GetAwaiter().GetResult();
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            AssertTrue(refused);
            AssertFalse(File.Exists(Path.Combine(directory, "evil.bin")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async ValueTask TarPayloadsExtractWithModes()
    {
        string directory = CreateTempDirectory();
        try
        {
            string archivePath = Path.Combine(directory, "payload.tar");
            byte[] content = [0xDE, 0xAD, 0xBE, 0xEF];
            await using (FileStream stream = File.Create(archivePath))
            await using (TarWriter writer = new(stream))
            {
                UstarTarEntry directoryEntry = new(TarEntryType.Directory, "data")
                {
                    Mode = (UnixFileMode)493,
                };
                await writer.WriteEntryAsync(directoryEntry);
                UstarTarEntry file = new(TarEntryType.RegularFile, "data/a.bin")
                {
                    Mode = (UnixFileMode)384,
                };
                file.DataStream = new MemoryStream(content);
                await writer.WriteEntryAsync(file);
            }

            string staged = Path.Combine(directory, "staged");
            List<UpdateFileEntry> inventory = await UpdatePayloadExtractor.ExtractTarAsync(archivePath, staged);

            AssertEqual(1, inventory.Count);
            AssertEqual("data/a.bin", inventory[0].Path);
            AssertEqual(4, inventory[0].Size);
            AssertEqual(384, inventory[0].UnixMode);
            AssertTrue(File.ReadAllBytes(Path.Combine(staged, "data", "a.bin")).SequenceEqual(content));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async ValueTask HpatchzToolRunsThroughTheProcessPort()
    {
        string directory = CreateTempDirectory();
        try
        {
            (string source, string patch, string target) = MakePatchScenario(
                directory, "run", [0x01], [0x02]);

            FakeRunner runner = new() { OutputContent = "out!" };
            HDiffPatchTool tool = new(runner, "hpatchz");
            string output = Path.Combine(directory, "out.bin");
            await tool.ApplyAsync(source, patch, output);

            AssertEqual(1, runner.Invocations.Count);
            AssertEqual("hpatchz", runner.Invocations[0].Executable);
            AssertTrue(runner.Invocations[0].Arguments.SequenceEqual([source, patch, output]));
            AssertEqual("out!", File.ReadAllText(output));

            FakeRunner failing = new() { ExitCode = 2 };
            HDiffPatchTool failingTool = new(failing, "hpatchz");
            bool failed = false;
            try
            {
                await failingTool.ApplyAsync(source, patch, Path.Combine(directory, "out2.bin"));
            }
            catch (InvalidDataException failure)
            {
                failed = failure.Message.Contains("exit 2", StringComparison.Ordinal);
            }

            AssertTrue(failed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async ValueTask BinaryPatchChainsVerifyDownloadAndApply()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] current = [0x11, 0x22];
            string currentPath = Path.Combine(directory, "current.bin");
            File.WriteAllBytes(currentPath, current);
            byte[] hopPatch = [0xAA, 0xBB];
            byte[] finalPatch = [0xCC, 0xDD, 0xEE];

            UpdatePatchStep[] steps =
            [
                new UpdatePatchStep(
                    "1.4.10", "1.4.11", "mem://p1", OrchSha(hopPatch), hopPatch.Length,
                    OrchSha(current), current.Length, OrchSha(finalPatch), finalPatch.Length),
                new UpdatePatchStep(
                    "1.4.11", "1.4.12", "mem://p2", OrchSha(finalPatch), finalPatch.Length,
                    OrchSha(hopPatch), hopPatch.Length, OrchSha(finalPatch), finalPatch.Length),
            ];

            // The fake tool copies the patch payload to the output, so the final output must
            // equal the last patch payload and verify against the last target digest.
            FakeRunner runner = new() { CopyPatchToOutput = true };
            UpdatePatchApplier applier = new(runner);
            List<string> downloaded = [];
            string stagedOutput = Path.Combine(directory, "staged.bin");
            await applier.ApplyBinaryChainAsync(
                steps,
                currentPath,
                stagedOutput,
                (step, destination, _) =>
                {
                    downloaded.Add(step.DownloadUrl);
                    File.WriteAllBytes(destination, step.DownloadUrl == "mem://p1" ? hopPatch : finalPatch);
                    return Task.CompletedTask;
                });

            AssertTrue(File.ReadAllBytes(stagedOutput).SequenceEqual(finalPatch));
            AssertTrue(downloaded.SequenceEqual(["mem://p1", "mem://p2"]));
            AssertEqual(2, runner.Invocations.Count);
            AssertEqual(0, Directory.GetFileSystemEntries(directory, ".patch-work-*").Length);

            // A current file that does not match the first hop's source digest refuses fast.
            File.WriteAllBytes(currentPath, [0x00]);
            bool refused = false;
            try
            {
                await applier.ApplyBinaryChainAsync(
                    steps,
                    currentPath,
                    stagedOutput + ".2",
                    (_, destination, _) =>
                    {
                        File.WriteAllBytes(destination, [0x01]);
                        return Task.CompletedTask;
                    });
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            AssertTrue(refused);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async ValueTask ScatterOpsProduceAVerifiedStagedTree()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] sourceApp = [0x01, 0x01];
            byte[] targetApp = [0x02, 0x02, 0x02];
            byte[] newConfig = "{\"new\":true}"u8.ToArray();

            string sourceRoot = Path.Combine(directory, "install");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "data"));
            File.WriteAllBytes(Path.Combine(sourceRoot, "app.exe"), sourceApp);
            File.WriteAllBytes(Path.Combine(sourceRoot, "data", "old.json"), [0x33]);

            string bundlePath = Path.Combine(directory, "bundle.zip");
            using (FileStream stream = File.Create(bundlePath))
            using (ZipArchive bundle = new(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry blob = bundle.CreateEntry("blobs/data/new.json");
                using (var blobStream = blob.Open())
                {
                    blobStream.Write(newConfig);
                }

                ZipArchiveEntry patch = bundle.CreateEntry("patches/app.patch");
                using (var patchStream = patch.Open())
                {
                    patchStream.Write(targetApp);
                }
            }

            UpdateScatterPatchManifest manifest = new()
            {
                FormatVersion = 1,
                Layout = UpdateChunker.SingleFileBlockMapLayoutV1,
                FromVersion = "1.4.11",
                ToVersion = "1.4.12",
                TargetFiles =
                [
                    new UpdateFileEntry { Path = "app.exe", Sha256 = OrchSha(targetApp), Size = targetApp.Length, UnixMode = 493 },
                    new UpdateFileEntry { Path = "data/new.json", Sha256 = OrchSha(newConfig), Size = newConfig.Length },
                ],
                Ops =
                [
                    new UpdateScatterPatchOperation
                    {
                        Path = "app.exe",
                        Op = "hdiff",
                        Patch = "patches/app.patch",
                        PatchSha256 = OrchSha(targetApp),
                        PatchSize = targetApp.Length,
                        FromSha256 = OrchSha(sourceApp),
                        FromSize = sourceApp.Length,
                        ToSha256 = OrchSha(targetApp),
                        ToSize = targetApp.Length,
                    },
                    new UpdateScatterPatchOperation
                    {
                        Path = "data/new.json",
                        Op = "add",
                        Blob = "blobs/data/new.json",
                        BlobSha256 = OrchSha(newConfig),
                        BlobSize = newConfig.Length,
                    },
                    new UpdateScatterPatchOperation
                    {
                        Path = "data/old.json",
                        Op = "delete",
                    },
                ],
            };

            FakeRunner runner = new() { CopyPatchToOutput = true };
            UpdatePatchApplier applier = new(runner);
            string stagedRoot = Path.Combine(directory, "staged");
            await applier.ApplyScatterOpsAsync(manifest, bundlePath, sourceRoot, stagedRoot);

            AssertEqual(2, Directory.GetFiles(stagedRoot, "*", SearchOption.AllDirectories).Length);
            AssertTrue(File.ReadAllBytes(Path.Combine(stagedRoot, "app.exe")).SequenceEqual(targetApp));
            AssertTrue(File.ReadAllText(Path.Combine(stagedRoot, "data", "new.json")) == "{\"new\":true}");
            AssertEqual(1, runner.Invocations.Count);
            (string Executable, string[] Arguments) invocation = runner.Invocations[0];
            AssertTrue(invocation.Arguments[0].EndsWith("app.exe", StringComparison.Ordinal));
            AssertTrue(invocation.Arguments[1].Contains(".payload-", StringComparison.Ordinal));
            AssertTrue(invocation.Arguments[2].EndsWith("app.exe", StringComparison.Ordinal));

            // A corrupted bundle payload is refused before anything is staged.
            using (FileStream stream = File.Create(bundlePath))
            using (ZipArchive bundle = new(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry blob = bundle.CreateEntry("blobs/data/new.json");
                using (var blobStream = blob.Open())
                {
                    blobStream.Write([0x00, 0x01]);
                }
            }

            string otherStaged = Path.Combine(directory, "staged2");
            bool refused = false;
            try
            {
                await applier.ApplyScatterOpsAsync(manifest, bundlePath, sourceRoot, otherStaged);
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            AssertTrue(refused);
            AssertFalse(Directory.Exists(otherStaged) && Directory.GetFiles(otherStaged, "*", SearchOption.AllDirectories).Length > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
