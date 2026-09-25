using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Files;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ArchiveActualByteLimits()
    {
        static async Task Reject(byte[] bytes, long declared, long limit, ArchiveReadBudget budget)
        {
            using var input = new MemoryStream(bytes);
            using var output = new MemoryStream();
            bool rejected = false;
            try { await ArchiveReadBudget.CopyAsync(input, output, declared, limit, budget, default); }
            catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected, "Invalid actual size must be rejected");
            AssertTrue(output.Length <= declared && output.Length <= limit);
        }
        await Reject(new byte[9], 8, 8, new(32));
        await Reject(new byte[7], 8, 8, new(32));
        await Reject(new byte[9], 9, 8, new(32));
        var transaction = new ArchiveReadBudget(12);
        using (var input = new MemoryStream(new byte[8]))
        using (var output = new MemoryStream())
            await ArchiveReadBudget.CopyAsync(input, output, 8, 8, transaction, default);
        AssertEqual(4L, transaction.Remaining);
        await Reject(new byte[8], 8, 8, transaction);

        string temporary = Path.Combine(Path.GetTempPath(), "nexa-stored-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (bool manifestMismatch in new[] { true, false })
            {
                string path = Path.Combine(temporary, manifestMismatch ? "manifest.mrpack" : "override.mrpack");
                using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var (name, content) in new[] { ("modrinth.index.json", MrpackIndex().ToJsonString()), ("overrides/config/test.txt", "abcdefghi") })
                    {
                        var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression);
                        using var writer = new StreamWriter(entry.Open());
                        writer.Write(content);
                    }
                }
                byte[] data = await File.ReadAllBytesAsync(path);
                string tampered = manifestMismatch ? "modrinth.index.json" : "overrides/config/test.txt";
                for (int index = 0; index <= data.Length - 46; index++)
                {
                    if (BitConverter.ToUInt32(data, index) != 0x02014b50) continue;
                    int nameLength = BitConverter.ToUInt16(data, index + 28);
                    if (Encoding.UTF8.GetString(data, index + 46, nameLength) != tampered) continue;
                    // Stored compressed size remains intact: the readable stream is larger than Length.
                    BitConverter.GetBytes(1u).CopyTo(data, index + 24);
                    break;
                }
                await File.WriteAllBytesAsync(path, data);
                if (manifestMismatch)
                {
                    bool rejected = false;
                    try { await MinecraftModpackArchive.InspectAsync(path); }
                    catch (InvalidDataException) { rejected = true; }
                    AssertTrue(rejected, "Stored manifest must not trust its declared size");
                }
                else
                {
                    var preview = await MinecraftModpackArchive.InspectAsync(path);
                    string root = Path.Combine(temporary, "game"); Directory.CreateDirectory(root);
                    using var fixture = new InstallFixture(PackMetadata());
                    var result = await fixture.Install.InstallModpackAsync(new(preview, root));
                    AssertFalse(result.IsSuccess);
                    AssertFalse(Directory.Exists(Path.Combine(root, "versions", preview.InstanceId)));
                    AssertEqual(0, fixture.InstalledRoots.Count);
                    AssertEqual(0, Directory.GetDirectories(root, ".nexa-pack-*").Count(path => Path.GetFileName(path) != ".nexa-pack-jobs"));
                }
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static JsonObject MrpackIndex(string? wrongHash = null) => new()
    {
        ["formatVersion"] = 1,
        ["game"] = "minecraft",
        ["name"] = "Test Pack",
        ["versionId"] = "1.0",
        ["dependencies"] = new JsonObject { ["minecraft"] = "1.20.1", ["fabric-loader"] = "0.16.9" },
        ["files"] = new JsonArray(PackFile("mods/required.jar", "required", wrongHash), PackFile("mods/optional.jar", "optional"), PackFile("mods/server.jar", "unsupported")),
    };
    private static JsonObject PackFile(string path, string environment, string? hash = null) => new()
    {
        ["path"] = path,
        ["fileSize"] = 7,
        ["env"] = new JsonObject { ["client"] = environment, ["server"] = "required" },
        ["hashes"] = new JsonObject { ["sha1"] = hash ?? Sha1Hex("MODJAR!"), ["sha512"] = Convert.ToHexString(SHA512.HashData("MODJAR!"u8)) },
        ["downloads"] = new JsonArray("https://cdn.modrinth.com/data/test/versions/1/mod.jar"),
    };
    private static FakeMetadata PackMetadata() => new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson(), LoaderJson = new JsonObject { ["mainClass"] = "fabric.Main", ["inheritsFrom"] = "1.20.1" } };

    private static async ValueTask MrpackInstallsClientFilesAndOverridesAtomically()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-mrpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "pack.mrpack");
            WriteLocalJar(source, ("modrinth.index.json", MrpackIndex().ToJsonString()), ("overrides/config/test.txt", "base"),
                ("client-overrides/config/test.txt", "client"), ("server-overrides/server.txt", "server"));
            var preview = await MinecraftModpackArchive.InspectAsync(source);
            AssertEqual(1, preview.RequiredFiles); AssertEqual(1, preview.OptionalFiles);
            using var fixture = new InstallFixture(PackMetadata());
            foreach (bool optional in new[] { false, true })
            {
                string root = Path.Combine(temporary, optional ? "all" : "required"); Directory.CreateDirectory(root);
                var result = await fixture.Install.InstallModpackAsync(new(preview, root, optional));
                AssertTrue(result.IsSuccess, result.Error?.Message ?? "pack install");
                string instance = result.Value!.InstanceDirectory;
                AssertTrue(File.Exists(Path.Combine(instance, "mods", "required.jar")));
                AssertEqual(optional, File.Exists(Path.Combine(instance, "mods", "optional.jar")));
                AssertFalse(File.Exists(Path.Combine(instance, "mods", "server.jar")));
                AssertFalse(File.Exists(Path.Combine(instance, "server.txt")));
                AssertEqual("client", await File.ReadAllTextAsync(Path.Combine(instance, "config", "test.txt")));
                AssertTrue((await new MinecraftInstanceMetadataStore().LoadAsync(instance)).InstanceIsolation);
                AssertEqual(0, Directory.GetDirectories(root, ".nexa-pack-*").Count(path => Path.GetFileName(path) != ".nexa-pack-jobs"));
                AssertFalse((await fixture.Install.InstallModpackAsync(new(preview, root))).IsSuccess);
            }
            AssertEqual(2, fixture.InstalledRoots.Count);
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask ModpackFailuresNeverPublishInstance()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-pack-failure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "bad.mrpack"), root = Path.Combine(temporary, "game"); Directory.CreateDirectory(root);
            WriteLocalJar(source, ("modrinth.index.json", MrpackIndex(new string('0', 40)).ToJsonString()));
            var preview = await MinecraftModpackArchive.InspectAsync(source);
            using var fixture = new InstallFixture(PackMetadata());
            var result = await fixture.Install.InstallModpackAsync(new(preview, root));
            AssertFalse(result.IsSuccess);
            AssertFalse(Directory.Exists(Path.Combine(root, "versions", preview.InstanceId)));
            AssertEqual(0, fixture.InstalledRoots.Count);
            AssertEqual(0, Directory.GetDirectories(root, ".nexa-pack-*").Count(path => Path.GetFileName(path) != ".nexa-pack-jobs"));
            string malicious = Path.Combine(temporary, "escape.zip");
            WriteLocalJar(malicious, ("modrinth.index.json", MrpackIndex().ToJsonString()), ("overrides/../escape.txt", "bad"));
            try { await MinecraftModpackArchive.InspectAsync(malicious); throw new InvalidOperationException("Traversal accepted."); }
            catch (InvalidDataException) { }
            var unknown = MrpackIndex(); unknown["dependencies"]!["future-loader"] = "1";
            string future = Path.Combine(temporary, "future.zip"); WriteLocalJar(future, ("modrinth.index.json", unknown.ToJsonString()));
            try { await MinecraftModpackArchive.InspectAsync(future); throw new InvalidOperationException("Unknown dependency accepted."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask CursePackResolvesFilesAndHonorsDownloadRestrictions()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-cf-pack-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        try
        {
            var manifest = new JsonObject
            {
                ["manifestVersion"] = 1,
                ["manifestType"] = "minecraftModpack",
                ["name"] = "CF Pack",
                ["version"] = "1",
                ["minecraft"] = new JsonObject { ["version"] = "1.20.1", ["modLoaders"] = new JsonArray(new JsonObject { ["id"] = "fabric-0.16.9", ["primary"] = true }) },
                ["files"] = new JsonArray(new JsonObject { ["projectID"] = 123, ["fileID"] = 456, ["required"] = true }),
                ["overrides"] = "overrides",
            };
            string source = Path.Combine(temporary, "pack.zip"); WriteLocalJar(source, ("manifest.json", manifest.ToJsonString()), ("overrides/options.txt", "options"));
            var preview = await MinecraftModpackArchive.InspectAsync(source);
            foreach (bool allowed in new[] { true, false })
            {
                using var http = new HttpClient(new PackCurseHandler(allowed));
                using var fixture = new InstallFixture(PackMetadata(), http: http);
                string root = Path.Combine(temporary, allowed ? "allowed" : "blocked"); Directory.CreateDirectory(root);
                var result = await fixture.Install.InstallModpackAsync(new(preview, root));
                AssertEqual(allowed, result.IsSuccess);
                string instance = Path.Combine(root, "versions", preview.InstanceId);
                if (allowed)
                {
                    AssertTrue(File.Exists(Path.Combine(instance, "resourcepacks", "textures.zip")));
                    AssertFalse(File.Exists(Path.Combine(instance, "mods", "textures.zip")));
                }
                else AssertFalse(Directory.Exists(instance));
                AssertEqual(0, Directory.GetDirectories(root, ".nexa-pack-*").Count(path => Path.GetFileName(path) != ".nexa-pack-jobs"));
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private sealed class PackCurseHandler(bool allowed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var data = request.RequestUri!.AbsolutePath.EndsWith("/files/456", StringComparison.Ordinal)
                ? new JsonObject
                {
                    ["id"] = 456,
                    ["modId"] = 123,
                    ["fileName"] = "textures.zip",
                    ["fileLength"] = 7,
                    ["downloadUrl"] = allowed ? "https://edge.forgecdn.net/files/1/1/textures.zip" : null,
                    ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = Sha1Hex("MODJAR!") })
                }
                : new JsonObject { ["id"] = 123, ["classId"] = 12 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    private static async ValueTask ModpackCancellationAndConflictsPreserveExistingData()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-pack-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "pack.mrpack");
            WriteLocalJar(source, ("modrinth.index.json", MrpackIndex().ToJsonString()));
            var preview = await MinecraftModpackArchive.InspectAsync(source);
            foreach (string scenario in new[] { "cancel", "conflict", "override", "changed" })
            {
                string root = Path.Combine(temporary, scenario); Directory.CreateDirectory(root);
                using var cancellation = new CancellationTokenSource();
                using var fixture = new InstallFixture(PackMetadata(), connectionFactory: url =>
                    scenario == "cancel" && url.StartsWith("https://cdn.modrinth.com/", StringComparison.Ordinal)
                        ? new CancelPackConnection(cancellation) : new ServingConnection(PayloadFor(url)));
                string shared = Path.Combine(root, "libraries", "com", "example", "library", "1.0.0", "library-1.0.0.jar");
                if (scenario == "conflict")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
                    await File.WriteAllTextAsync(shared, "existing user content");
                }
                if (scenario == "override")
                {
                    File.Delete(source);
                    WriteLocalJar(source, ("modrinth.index.json", MrpackIndex().ToJsonString()), ("overrides/Nexa/instance.json", "bad"));
                    preview = await MinecraftModpackArchive.InspectAsync(source);
                }
                if (scenario == "changed")
                {
                    File.Delete(source);
                    WriteLocalJar(source, ("modrinth.index.json", MrpackIndex().ToJsonString()), ("overrides/changed.txt", "changed"));
                }
                var result = await fixture.Install.InstallModpackAsync(new(preview, root), cancellation.Token);
                AssertTrue(!result.IsSuccess, scenario);
                AssertTrue(!Directory.Exists(Path.Combine(root, "versions", preview.InstanceId)), scenario);
                AssertEqual(0, fixture.InstalledRoots.Count);
                AssertEqual(0, Directory.GetDirectories(root, ".nexa-pack-*").Count(path => Path.GetFileName(path) != ".nexa-pack-jobs"));
                if (scenario == "cancel") AssertEqual(TaskCenterEntryState.Paused, fixture.Entry().State);
                if (scenario == "conflict") AssertEqual("existing user content", await File.ReadAllTextAsync(shared));
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask ModpackRecoveryUsesPinnedArchiveAndReusesVerifiedDownloads()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-pack-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "pack.mrpack"), root = Path.Combine(temporary, "game");
            Directory.CreateDirectory(root);
            WriteLocalJar(source, ("modrinth.index.json", MrpackIndex().ToJsonString()), ("overrides/config/test.txt", "preserved"));
            var preview = await MinecraftModpackArchive.InspectAsync(source);
            using var cancellation = new CancellationTokenSource();
            int downloads = 0;
            using (var fixture = new InstallFixture(PackMetadata(), connectionFactory: url =>
                url.StartsWith("https://cdn.modrinth.com/", StringComparison.Ordinal) && Interlocked.Increment(ref downloads) == 2
                    ? new CancelPackConnection(cancellation) : new ServingConnection(PayloadFor(url))))
            {
                AssertFalse((await fixture.Install.InstallModpackAsync(new(preview, root, true), cancellation.Token)).IsSuccess);
                AssertFalse(Directory.Exists(Path.Combine(root, "versions", preview.InstanceId)));
            }
            File.Delete(source);
            var metadata = new FakeMetadata(); int resumedDownloads = 0;
            using var resumed = new InstallFixture(metadata, connectionFactory: url =>
            {
                if (url.StartsWith("https://cdn.modrinth.com/", StringComparison.Ordinal)) Interlocked.Increment(ref resumedDownloads);
                return new ServingConnection(PayloadFor(url));
            });
            var result = await resumed.Install.RecoverPendingAsync(new([root]));
            AssertTrue(result.IsSuccess, result.Error?.Message ?? "recovery failed");
            AssertEqual(0, metadata.VanillaReads);
            AssertEqual(1, resumedDownloads);
            AssertEqual("preserved", await File.ReadAllTextAsync(Path.Combine(root, "versions", preview.InstanceId, "config", "test.txt")));
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask ModpackPublicationRecoversLostCompletionAndProtectsChangedFiles()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-pack-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "pack.mrpack");
            WriteLocalJar(source, ("modrinth.index.json", MrpackIndex().ToJsonString()));
            var preview = await MinecraftModpackArchive.InspectAsync(source);
            foreach (string scenario in new[] { "published", "changed", "cancel" })
            {
                string root = Path.Combine(temporary, scenario); Directory.CreateDirectory(root);
                var journal = await ModpackInstallJournal.CreateAsync(root, new(preview, root), CancellationToken.None);
                Directory.CreateDirectory(journal.Instance);
                await File.WriteAllTextAsync(Path.Combine(journal.Instance, preview.InstanceId + ".json"), "{}");
                await journal.PrepareAsync(CancellationToken.None);
                if (scenario == "cancel")
                {
                    await journal.CancelAsync();
                    AssertTrue(journal.Canceled);
                    AssertFalse(Directory.Exists(journal.Destination));
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(journal.Destination)!);
                Directory.Move(journal.Instance, journal.Destination);
                var reopened = await ModpackInstallJournal.OpenAsync(root, journal.Stage, CancellationToken.None);
                if (scenario == "changed")
                {
                    await File.WriteAllTextAsync(Path.Combine(journal.Destination, preview.InstanceId + ".json"), "changed");
                    try { await reopened.CancelAsync(); throw new InvalidOperationException("Changed publication accepted."); }
                    catch (InvalidDataException) { }
                    AssertFalse(reopened.Complete);
                    AssertEqual("changed", await File.ReadAllTextAsync(Path.Combine(journal.Destination, preview.InstanceId + ".json")));
                }
                else
                {
                    await reopened.CancelAsync();
                    AssertTrue(reopened.Complete); AssertFalse(reopened.Canceled);
                    AssertTrue(Directory.Exists(reopened.Destination));
                }
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask CancelQueuedModpackPreventsStartupRecovery()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-pack-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "pack.mrpack"), root = Path.Combine(temporary, "game"); Directory.CreateDirectory(root);
            WriteLocalJar(source, ("modrinth.index.json", MrpackIndex().ToJsonString()));
            var preview = await MinecraftModpackArchive.InspectAsync(source);
            using var cancellation = new CancellationTokenSource();
            using (var fixture = new InstallFixture(PackMetadata(), connectionFactory: url =>
                url.StartsWith("https://cdn.modrinth.com/", StringComparison.Ordinal) ? new CancelPackConnection(cancellation) : new ServingConnection(PayloadFor(url))))
            {
                AssertFalse((await fixture.Install.InstallModpackAsync(new(preview, root), cancellation.Token)).IsSuccess);
                AssertTrue((await fixture.Install.StopAsync(new(false))).IsSuccess);
            }
            using var restarted = new InstallFixture(new FakeMetadata());
            AssertTrue((await restarted.Install.RecoverPendingAsync(new([root]))).IsSuccess);
            AssertFalse(Directory.Exists(Path.Combine(root, "versions", preview.InstanceId)));
        }
        finally { Directory.Delete(temporary, true); }
    }

    private sealed class CancelPackConnection(CancellationTokenSource cancellation) : IDownloadConnection
    {
        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DownloadConnectionInfo(7, 0, 6, false));
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

