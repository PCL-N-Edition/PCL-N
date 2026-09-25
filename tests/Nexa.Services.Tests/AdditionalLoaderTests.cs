using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ProfileLoadersCommitWithoutDuplicatingFullManifests()
    {
        foreach (var loader in new[] { InstallLoader.LiteLoader, InstallLoader.LabyMod })
        {
            string root = CreateTempDirectory();
            try
            {
                var profile = loader == InstallLoader.LabyMod ? VanillaJson() : new JsonObject { ["inheritsFrom"] = "1.20.1" };
                profile["mainClass"] = "net.minecraft.launchwrapper.Launch";
                using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), LoaderJson = profile, AssetIndexJson = AssetIndexJson() });
                var result = await fixture.Install.InstallAsync(new(root, "1.20.1", loader, "selected", [], "renamed"));
                AssertTrue(result.IsSuccess, fixture.Entry().ErrorMessage ?? "profile install failed");
                var installed = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "versions", "renamed", "renamed.json")))!;
                AssertEqual("renamed", installed["id"]!.ToString());
                AssertTrue(installed["inheritsFrom"] is null);
                AssertTrue(installed["jar"] is null);
                AssertEqual(VanillaJson()["libraries"]!.AsArray().Count, installed["libraries"]!.AsArray().Count);
                AssertTrue(File.Exists(Path.Combine(root, "versions", "renamed", "renamed.jar")));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static void LiteLoaderPreservesLegacyBaseArguments()
    {
        var catalog = JsonNode.Parse("""{"versions":{"1.12.2":{"snapshots":{"com.mumfrey:liteloader":{"latest":{"version":"1.12.2-SNAPSHOT","libraries":[{"name":"net.minecraft:launchwrapper:1.12"},{"name":"org.ow2.asm:asm-all:5.2"}]}}}}}}""")!.AsObject();
        var profile = AdditionalLoaderProfiles.LiteLoader(catalog, "1.12.2", "1.12.2-SNAPSHOT");
        AssertTrue(profile["libraries"]![1]!["url"]!.ToString().Contains("maven.apache.org", StringComparison.Ordinal));
        bool rejected = false;
        try { AdditionalLoaderProfiles.LiteLoader(catalog, "1.12.2", "stale"); } catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
        string jar = Path.GetTempFileName();
        try
        {
            var plan = MinecraftLaunchPlanner.CreatePlan(new()
            {
                VersionJson = profile,
                InheritedVersionJsons = [JsonNode.Parse("""{"id":"1.12.2","minecraftArguments":"--username ${auth_player_name} --version ${version_name}"}""")!.AsObject()],
                VersionId = "Lite",
                InstanceDirectory = Path.GetTempPath(),
                MinecraftRootDirectory = Path.GetTempPath(),
                ClientJarPath = jar,
                PlayerName = "Steve",
                PlayerUuid = "uuid",
                JavaMajorVersion = 8,
            });
            AssertEqual(1, plan.Arguments.Count(a => a == "--username"));
            AssertTrue(plan.Arguments.Contains("Steve"));
            AssertTrue(plan.Arguments.Contains("com.mumfrey.liteloader.launch.LiteLoaderTweaker"));
        }
        finally { File.Delete(jar); }
    }

    private static void LabyModIncludesBootstrapAndPinnedArtifact()
    {
        var profile = new JsonObject { ["libraries"] = new JsonArray() };
        var manifest = JsonNode.Parse("""{"commitReference":"abcdef12","labyModVersion":"4.6","sha1":"0123456789012345678901234567890123456789","size":123,"assets":{"shader":"assetHash"}}""")!.AsObject();
        var catalog = JsonNode.Parse("""{"libraries":[{"name":"net.minecraft:launchwrapper:4.1.3","minecraftVersion":"all","url":"https://example.invalid/bootstrap.jar","sha1":"bootstrap","size":3},{"name":"wrong:game:1","minecraftVersion":"1.8","url":"https://example.invalid/wrong.jar"}]}""")!.AsObject();
        var result = AdditionalLoaderProfiles.LabyMod(profile, manifest, catalog, "1.20.1", "production");
        AssertEqual(2, result["libraries"]!.AsArray().Count);
        AssertTrue(result["libraries"]![1]!["downloads"]!["artifact"]!["url"]!.ToString().EndsWith("/abcdef12.jar", StringComparison.Ordinal));
        AssertEqual("0123456789012345678901234567890123456789", result["libraries"]![1]!["downloads"]!["artifact"]!["sha1"]!.ToString());
        AssertTrue(result["_nexaLabyAssets"]!["shader"]!.ToString().Contains("abcdef12/shader/assetHash.jar", StringComparison.Ordinal));
    }

    private static async ValueTask AdditionalInstallersUseStagingAndPropagateFailure()
    {
        foreach (var loader in new[] { InstallLoader.Cleanroom, InstallLoader.OptiFine })
            foreach (bool local in new[] { false, true })
                foreach (bool fail in new[] { false, true })
                {
                    string root = CreateTempDirectory();
                    try
                    {
                        Directory.CreateDirectory(Path.Combine(root, "versions", "1.12.2"));
                        await File.WriteAllTextAsync(Path.Combine(root, "versions", "1.12.2", "1.12.2.jar"), "client");
                        var manifest = new JsonObject { ["id"] = "generated", ["mainClass"] = "bootstrap.Main", ["inheritsFrom"] = "1.12.2" };
                        byte[] archive = InstallerFixtureArchive(manifest);
                        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive));
                        using var http = new HttpClient(new StaticHttpMessageHandler("{\"assets\":[{\"name\":\"cleanroom-0.6-installer.jar\",\"digest\":\"sha256:" + sha + "\"}]}"));
                        XsrStateStoreBuilder builder = new(); DownloadService.DeclareState(builder);
                        bool ran = false;
                        var service = new ForgeInstallService(new DownloadService(builder.Build()), http,
                            _ => local ? throw new InvalidOperationException("Local installer must not be downloaded.") : new ServingConnection(archive), (_, _) => Task.FromResult("java"), async (start, token) =>
                        {
                            ran = true; AssertTrue(start.CreateNoWindow); AssertFalse(start.UseShellExecute);
                            AssertEqual(start.WorkingDirectory, start.ArgumentList[^1]);
                            if (loader == InstallLoader.OptiFine)
                            {
                                AssertTrue(start.ArgumentList[^2].EndsWith("NexaOptiFineInstall.java", StringComparison.Ordinal));
                                AssertTrue((await File.ReadAllTextAsync(start.ArgumentList[^2], token)).Contains("doInstall", StringComparison.Ordinal));
                            }
                            if (fail) throw new IOException("processor failed");
                            string target = Path.Combine(start.WorkingDirectory, "versions", "generated"); Directory.CreateDirectory(target);
                            await File.WriteAllTextAsync(Path.Combine(target, "generated.json"), manifest.ToJsonString(), token);
                        });
                        string build = loader == InstallLoader.Cleanroom ? "0.6" : "1.12.2_HD_U_G5";
                        string localPath = Path.Combine(root, "local.jar");
                        if (local) await File.WriteAllBytesAsync(localPath, archive);
                        var request = new MinecraftLoaderInstallRequest(root, "1.12.2", "custom", loader, build, new())
                        { LocalInstaller = local ? new(localPath, sha, "1.12.2", loader, build) : null };
                        bool rejected = false;
                        try { await service.InstallAsync(request, null, CancellationToken.None); }
                        catch (IOException) { rejected = true; }
                        AssertEqual(fail, rejected); AssertTrue(ran);
                        AssertFalse(Directory.Exists(Path.Combine(root, "versions", "custom")));
                        AssertEqual(0, Directory.GetDirectories(Path.Combine(root, ".nexa-install")).Length);
                    }
                    finally { Directory.Delete(root, true); }
                }
    }
}
