using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class FixtureLoaderInstaller : IMinecraftLoaderInstaller
    {
        public InstallLoader? CalledLoader;
        public LocalJarArtifact? LocalArtifact;
        public Task<JsonObject> InstallAsync(MinecraftLoaderInstallRequest request, IProgress<string>? progress, CancellationToken token)
        {
            AssertTrue(File.Exists(Path.Combine(request.Root, "versions", request.Game, request.Game + ".jar")));
            AssertFalse(File.Exists(Path.Combine(request.Root, "versions", request.InstanceId, request.InstanceId + ".json")));
            CalledLoader = request.Loader;
            LocalArtifact = request.LocalInstaller;
            return Task.FromResult(new JsonObject { ["mainClass"] = "bootstrap.Main", ["inheritsFrom"] = request.Game });
        }
    }

    private static async ValueTask ForgeAndNeoForgeCommitInstallerManifestLast()
    {
        foreach (var loader in new[] { InstallLoader.Forge, InstallLoader.NeoForge, InstallLoader.Cleanroom, InstallLoader.OptiFine })
        {
            string root = Path.Combine(Path.GetTempPath(), "nexa-install-tests", Guid.NewGuid().ToString("N"));
            try
            {
                var runner = new FixtureLoaderInstaller();
                using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, loaderInstaller: runner);
                var result = await fixture.Install.InstallAsync(new MinecraftInstallCommand(root, "1.20.1", loader, "fixture", [], "custom-instance"));
                AssertTrue(result.IsSuccess, fixture.Entry().ErrorMessage ?? "install failed");
                AssertEqual(loader, runner.CalledLoader!.Value);
                var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "versions", "custom-instance", "custom-instance.json")))!;
                AssertEqual("bootstrap.Main", json["mainClass"]!.ToString());
                AssertEqual("custom-instance", json["id"]!.ToString());
                AssertEqual(1, fixture.InstalledRoots.Count);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    private static async ValueTask ForgeInstallStagesAndValidatesOutputs()
    {
        foreach (var kind in new[] { InstallLoader.Forge, InstallLoader.NeoForge })
        {
            string root = CreateTempDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "versions", "1.20.1"));
                await File.WriteAllTextAsync(Path.Combine(root, "versions", "1.20.1", "1.20.1.jar"), "client");
                await File.WriteAllTextAsync(Path.Combine(root, "launcher_profiles.json"), "user profiles");
                var manifest = JsonNode.Parse("""{"id":"loader-built","inheritsFrom":"1.20.1","mainClass":"bootstrap.Main","libraries":[{"name":"test:patched:1","downloads":{"artifact":{"path":"test/patched/1/patched-1.jar","url":""}}}]}""")!.AsObject();
                byte[] archive = InstallerFixtureArchive(manifest);
                using var http = new HttpClient(new StaticHttpMessageHandler(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(archive))));
                XsrStateStoreBuilder builder = new(); DownloadService.DeclareState(builder); var store = builder.Build();
                bool ran = false;
                var service = new ForgeInstallService(new DownloadService(store), http,
                    _ => new ServingConnection(archive), (_, _) => Task.FromResult("java with spaces"),
                    async (start, token) =>
                    {
                        ran = true;
                        AssertTrue(start.CreateNoWindow); AssertFalse(start.UseShellExecute);
                        AssertEqual("--installClient", start.ArgumentList[^2]);
                        AssertEqual(start.WorkingDirectory, start.ArgumentList[^1]);
                        AssertFalse(start.WorkingDirectory == root);
                        AssertTrue(File.Exists(Path.Combine(start.WorkingDirectory, "versions", "1.20.1", "1.20.1.jar")));
                        string folder = Path.Combine(start.WorkingDirectory, "versions", "loader-built"); Directory.CreateDirectory(folder);
                        await File.WriteAllTextAsync(Path.Combine(folder, "loader-built.json"), manifest.ToJsonString(), token);
                        string library = Path.Combine(start.WorkingDirectory, "libraries", "test", "patched", "1"); Directory.CreateDirectory(library);
                        await File.WriteAllTextAsync(Path.Combine(library, "patched-1.jar"), "patched", token);
                    });
                var result = await service.InstallAsync(new(root, "1.20.1", "my-instance", kind, "47.2.0", new()), null, CancellationToken.None);
                AssertTrue(ran); AssertEqual("bootstrap.Main", result["mainClass"]!.ToString());
                AssertEqual("user profiles", await File.ReadAllTextAsync(Path.Combine(root, "launcher_profiles.json")));
                AssertTrue(File.Exists(Path.Combine(root, "libraries", "test", "patched", "1", "patched-1.jar")));
                AssertEqual(0, Directory.GetDirectories(Path.Combine(root, ".nexa-install")).Length);
            }
            finally { Directory.Delete(root, true); }
        }
        AssertTrue(ForgeInstallService.InstallerUrl(InstallLoader.NeoForge, "1.21.1", "21.1.235").Contains("net/neoforged/neoforge/21.1.235/neoforge-21.1.235-installer.jar", StringComparison.Ordinal));
    }

    private static async ValueTask ForgeInstallRejectsMissingProcessorOutput()
    {
        string root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "versions", "1.20.1"));
            await File.WriteAllTextAsync(Path.Combine(root, "versions", "1.20.1", "1.20.1.jar"), "client");
            var manifest = JsonNode.Parse("""{"id":"loader-built","mainClass":"bootstrap.Main","libraries":[{"name":"test:missing:1"}]}""")!.AsObject();
            byte[] archive = InstallerFixtureArchive(manifest);
            using var http = new HttpClient(new StaticHttpMessageHandler(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(archive))));
            XsrStateStoreBuilder builder = new(); DownloadService.DeclareState(builder);
            var service = new ForgeInstallService(new DownloadService(builder.Build()), http, _ => new ServingConnection(archive),
                (_, _) => Task.FromResult("java"), async (start, token) =>
                {
                    string folder = Path.Combine(start.WorkingDirectory, "versions", "loader-built"); Directory.CreateDirectory(folder);
                    await File.WriteAllTextAsync(Path.Combine(folder, "loader-built.json"), manifest.ToJsonString(), token);
                });
            bool rejected = false;
            try { await service.InstallAsync(new(root, "1.20.1", "my-instance", InstallLoader.Forge, "47.2.0", new()), null, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected); AssertFalse(Directory.Exists(Path.Combine(root, "versions", "my-instance")));
            AssertEqual(0, Directory.GetDirectories(Path.Combine(root, ".nexa-install")).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] InstallerFixtureArchive(JsonObject manifest)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("install_profile.json").Open())) writer.Write("{\"json\":\"/version.json\"}");
            using (var writer = new StreamWriter(zip.CreateEntry("version.json").Open())) writer.Write(manifest.ToJsonString());
        }
        return stream.ToArray();
    }
}
