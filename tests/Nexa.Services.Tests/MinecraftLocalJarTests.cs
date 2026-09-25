using System.IO.Compression;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask LocalJarRejectsStoredLengthMismatch()
    {
        string temporary = CreateTempDirectory();
        try
        {
            foreach (string target in new[] { "profile", "version", "patch", "core" })
                foreach (uint declared in new[] { 1u, 999u })
                {
                    bool manifest = target is "profile" or "version";
                    string path = Path.Combine(temporary, target + declared + ".jar");
                    string name = target == "profile" ? "install_profile.json" : target == "version" ? "version.json" : "example.class";
                    using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                    {
                        using (var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.NoCompression).Open()))
                            writer.Write(manifest ? "{\"minecraft\":\"1.20.1\",\"path\":\"net.minecraftforge:forge:1.20.1-47.2.0\"}" : "replacement");
                        if (target == "version")
                        {
                            using var profile = new StreamWriter(archive.CreateEntry("install_profile.json").Open());
                            profile.Write("{\"json\":\"/version.json\"}");
                        }
                    }
                    byte[] data = await File.ReadAllBytesAsync(path);
                    for (int index = 0; index <= data.Length - 46; index++)
                        if (BitConverter.ToUInt32(data, index) == 0x02014b50)
                        { BitConverter.GetBytes(declared).CopyTo(data, index + 24); break; }
                    await File.WriteAllBytesAsync(path, data);
                    if (manifest)
                    {
                        bool rejected = false;
                        try { await MinecraftLocalJarService.InspectAsync(path); }
                        catch (InvalidDataException) { rejected = true; }
                        AssertTrue(rejected, "Installer manifest must enforce actual length before parsing.");
                    }
                    else
                    {
                        string root = Path.Combine(temporary, target + declared);
                        string instance = CreateVersionDirectory(root, "test", new System.Text.Json.Nodes.JsonObject
                        { ["id"] = "test", ["mainClass"] = "main", ["type"] = "release" });
                        string core = Path.Combine(instance, "test.jar");
                        if (target == "core")
                        {
                            File.Copy(path, core);
                            File.Delete(path);
                            WriteLocalJar(path, ("addition.class", "addition"));
                        }
                        else WriteLocalJar(core, ("example.class", "original"));
                        byte[] original = await File.ReadAllBytesAsync(core);
                        using var fixture = new InstallFixture(new FakeMetadata());
                        var service = new MinecraftLocalJarService(fixture.Tasks, fixture.Store, fixture.Install);
                        var artifact = await MinecraftLocalJarService.InspectAsync(path);
                        AssertFalse((await service.ImportAsync(new(artifact, root, "test", LocalJarAction.CorePatch))).IsSuccess);
                        byte[] after = await File.ReadAllBytesAsync(core);
                        AssertTrue(original.SequenceEqual(after), "Failed patch must not replace original core.");
                        AssertEqual(0, Directory.GetFiles(instance, "*.partial*").Length);
                        AssertEqual(0, Directory.GetFiles(instance, "*.backup-*").Length);
                    }
                }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask LocalJarImportsPreserveInstanceAndCore()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-jar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string root = Path.Combine(temporary, "game");
            string instance = Path.Combine(root, "versions", "test");
            Directory.CreateDirectory(instance);
            await File.WriteAllTextAsync(Path.Combine(instance, "test.json"), "{\"id\":\"test\",\"mainClass\":\"main\",\"type\":\"release\"}");
            string source = Path.Combine(temporary, "mod.jar");
            WriteLocalJar(source, ("example.class", "patched"), ("META-INF/signature.SF", "ignore"));
            string core = Path.Combine(instance, "test.jar");
            WriteLocalJar(core, ("example.class", "original"), ("keep.txt", "keep"), ("META-INF/services/provider", "keep"), ("META-INF/OLD.RSA", "obsolete"));
            using var fixture = new InstallFixture(new FakeMetadata());
            var service = new MinecraftLocalJarService(fixture.Tasks, fixture.Store, fixture.Install);
            var artifact = await MinecraftLocalJarService.InspectAsync(source);
            var sessions = fixture.Store.Resolve(Nexa.Services.Minecraft.Process.MinecraftProcessStateComposition.SessionsKey);
            var running = new Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot(Guid.NewGuid(), "test", 123,
                Nexa.Services.Minecraft.Process.MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
            { InstanceDirectory = Path.Combine(temporary, "other", "versions", "test") };
            fixture.Store.PublishDelta(sessions, new Nexa.Xsr.State.XsrCollectionDelta<Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot, Guid>(0, [running], []));
            AssertTrue((await service.ImportAsync(new(artifact, root, "test", LocalJarAction.Mod))).IsSuccess);
            long revision = fixture.Store.ReadCollection<Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot>(sessions).Revision;
            fixture.Store.PublishDelta(sessions, new Nexa.Xsr.State.XsrCollectionDelta<Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot, Guid>(revision,
                [running with { InstanceDirectory = instance }], []));
            var blocked = await service.ImportAsync(new(artifact, root, "test", LocalJarAction.CorePatch));
            AssertFalse(blocked.IsSuccess);
            AssertTrue(blocked.Error!.Message.Contains("游戏进程", StringComparison.Ordinal));
            revision = fixture.Store.ReadCollection<Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot>(sessions).Revision;
            fixture.Store.PublishDelta(sessions, new Nexa.Xsr.State.XsrCollectionDelta<Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot, Guid>(revision, [], [running.SessionId]));
            AssertTrue(File.Exists(Path.Combine(instance, "mods", "mod.jar")), "default isolation directory");
            AssertTrue(!File.Exists(Path.Combine(root, "mods", "mod.jar")), "shared directory untouched");
            AssertFalse((await service.ImportAsync(new(artifact, root, "test", LocalJarAction.Mod))).IsSuccess);
            await new MinecraftInstanceMetadataStore().UpdateAsync(instance, current => current with { InstanceIsolation = false });
            AssertTrue((await service.ImportAsync(new(artifact, root, "test", LocalJarAction.Mod))).IsSuccess);
            AssertTrue(File.Exists(Path.Combine(root, "mods", "mod.jar")), "shared directory follows instance metadata");
            AssertTrue((await service.ImportAsync(new(artifact, root, "test", LocalJarAction.CorePatch))).IsSuccess);
            using (var archive = ZipFile.OpenRead(core))
            {
                using var reader = new StreamReader(archive.GetEntry("example.class")!.Open());
                AssertEqual("patched", reader.ReadToEnd());
                AssertTrue(archive.GetEntry("keep.txt") is not null);
                AssertTrue(archive.GetEntry("META-INF/signature.SF") is null);
                AssertTrue(archive.GetEntry("META-INF/services/provider") is not null);
                AssertTrue(archive.GetEntry("META-INF/OLD.RSA") is null);
            }
            string backup = Directory.GetFiles(instance, "test.jar.backup-*").Single();
            using (var archive = ZipFile.OpenRead(backup))
            using (var reader = new StreamReader(archive.GetEntry("example.class")!.Open())) AssertEqual("original", reader.ReadToEnd());
            var descriptor = (await new MinecraftInstanceDiscovery().DiscoverAsync(root)).Single();
            AssertTrue(await MinecraftLaunchFileCompletion.HasVerifiedCorePatchAsync(descriptor, core, CancellationToken.None));
            await File.AppendAllTextAsync(core, "changed");
            AssertFalse(await MinecraftLaunchFileCompletion.HasVerifiedCorePatchAsync(descriptor, core, CancellationToken.None));
            await File.AppendAllTextAsync(source, "changed");
            AssertFalse((await service.ImportAsync(new(artifact, root, "test", LocalJarAction.CorePatch))).IsSuccess);
            artifact = await MinecraftLocalJarService.InspectAsync(source);
            await File.WriteAllTextAsync(Path.Combine(instance, "test.json"), "{\"id\":\"test\",\"inheritsFrom\":\"parent\"}");
            AssertFalse((await service.ImportAsync(new(artifact, root, "test", LocalJarAction.CorePatch))).IsSuccess);
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask LocalJarInstallerUsesContentIdentity()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-loader-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "misleading-1.99.jar");
            WriteLocalJar(source,
                ("install_profile.json", "{\"minecraft\":\"1.20.1\",\"path\":\"net.minecraftforge:forge:1.20.1-47.2.0\",\"json\":\"/version.json\"}"),
                ("version.json", "{\"inheritsFrom\":\"1.20.1\"}"));
            var artifact = await MinecraftLocalJarService.InspectAsync(source);
            AssertEqual("1.20.1", artifact.Game);
            AssertEqual("47.2.0", artifact.Build);
            AssertTrue(artifact.Loader == InstallLoader.Forge);
            var loader = new FixtureLoaderInstaller();
            using var fixture = new InstallFixture(new FakeMetadata { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, loaderInstaller: loader);
            var service = new MinecraftLocalJarService(fixture.Tasks, fixture.Store, fixture.Install);
            string root = Path.Combine(temporary, "game");
            Directory.CreateDirectory(root);
            var result = await service.ImportAsync(new(artifact, root, "", LocalJarAction.Loader));
            AssertTrue(result.IsSuccess, result.Error?.Message ?? "loader import");
            AssertTrue(loader.CalledLoader == InstallLoader.Forge);
            AssertEqual(artifact, loader.LocalArtifact!);
            string bad = Path.Combine(temporary, "bad.jar");
            WriteLocalJar(bad, ("../outside.class", "escape"));
            try { await MinecraftLocalJarService.InspectAsync(bad); throw new InvalidOperationException("Traversal accepted."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static void WriteLocalJar(string path, params (string Name, string Text)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open());
            writer.Write(entry.Text);
        }
    }

    private static async ValueTask LocalInstallerDoesNotDownloadReplacement()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-local-installer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "versions", "1.20.1"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "versions", "1.20.1", "1.20.1.jar"), "client");
            string source = Path.Combine(root, "input.jar");
            WriteLocalJar(source,
                ("install_profile.json", "{\"minecraft\":\"1.20.1\",\"path\":\"net.minecraftforge:forge:1.20.1-47.2.0\",\"json\":\"/version.json\"}"),
                ("version.json", "{\"id\":\"built\",\"mainClass\":\"bootstrap.Main\",\"inheritsFrom\":\"1.20.1\"}"));
            var artifact = await MinecraftLocalJarService.InspectAsync(source);
            var builder = new Nexa.Xsr.State.XsrStateStoreBuilder();
            Nexa.Services.Downloads.DownloadService.DeclareState(builder);
            using var http = new HttpClient(new RejectLocalInstallerNetwork());
            int executions = 0;
            var installer = new ForgeInstallService(new(builder.Build()), http,
                _ => throw new InvalidOperationException("Unexpected installer download."),
                (_, _) => Task.FromResult("java"), async (start, token) =>
                {
                    executions++;
                    string directory = Path.Combine(start.WorkingDirectory, "versions", "built");
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(Path.Combine(directory, "built.json"), "{\"id\":\"built\",\"mainClass\":\"bootstrap.Main\",\"inheritsFrom\":\"1.20.1\"}", token);
                });
            var request = new MinecraftLoaderInstallRequest(root, "1.20.1", "test", InstallLoader.Forge, "47.2.0", new()) { LocalInstaller = artifact };
            await installer.InstallAsync(request, null, CancellationToken.None);
            AssertEqual(1, executions);
            await File.AppendAllTextAsync(source, "changed");
            try { await installer.InstallAsync(request, null, CancellationToken.None); throw new InvalidOperationException("Changed file executed."); }
            catch (InvalidDataException) { }
            AssertEqual(1, executions);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RejectLocalInstallerNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A local installer must not fetch its remote replacement.");
    }
}
