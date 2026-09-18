using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask EditingPreservesConfigurationAndLocksMinecraft()
    {
        string root = CreateTempDirectory();
        try
        {
            var metadata = new FakeMetadata { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
            using var fixture = new InstallFixture(metadata, loaderInstaller: new FixtureLoaderInstaller());
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1"))).IsSuccess);
            string instance = Path.Combine(root, "versions", "1.20.1");
            Directory.CreateDirectory(Path.Combine(instance, "config"));
            Directory.CreateDirectory(Path.Combine(instance, "saves", "world"));
            await File.WriteAllTextAsync(Path.Combine(instance, "config", "options.cfg"), "user settings");
            await File.WriteAllTextAsync(Path.Combine(instance, "saves", "world", "level.dat"), "world");
            var original = await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"));
            var changedGame = await fixture.Install.InstallAsync(new(root, "1.21.1", InstanceName: "1.20.1", EditFingerprint: original.Fingerprint));
            AssertFalse(changedGame.IsSuccess);
            var replaced = await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Forge, "47.4.20", [], "1.20.1", original.Fingerprint));
            AssertTrue(replaced.IsSuccess, string.Join(" | ", fixture.Store.ReadCollection<Nexa.Services.Tasks.TaskCenterEntry>(fixture.Store.Resolve(Nexa.Services.Tasks.TaskCenterStateContract.EntriesKey)).Items.Select(item => item.ErrorMessage)));
            var updated = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(instance, "1.20.1.json")))!;
            AssertTrue(updated["inheritsFrom"] is null);
            AssertEqual("bootstrap.Main", updated["mainClass"]!.ToString());
            AssertEqual("user settings", await File.ReadAllTextAsync(Path.Combine(instance, "config", "options.cfg")));
            AssertEqual("world", await File.ReadAllTextAsync(Path.Combine(instance, "saves", "world", "level.dat")));
            var next = await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"));
            AssertEqual(InstallLoader.Forge, next.Selection[0].Loader);
            AssertFalse((await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "1.20.1", EditFingerprint: original.Fingerprint))).IsSuccess);
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "1.20.1", EditFingerprint: next.Fingerprint))).IsSuccess);
            AssertEqual(0, (await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"))).Selection.Count);
            AssertEqual("world", await File.ReadAllTextAsync(Path.Combine(instance, "saves", "world", "level.dat")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask EditingRemovesOnlyUnchangedManagedMods()
    {
        foreach (bool userChangedMod in new[] { false, true })
        {
            string root = CreateTempDirectory();
            try
            {
                using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, new FakeCatalog("https://example.invalid/fabricapi.jar"));
                AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Fabric, "0.16.9", [new(InstallLoader.FabricApi, "1.0.0")], "custom"))).IsSuccess);
                string managed = Path.Combine(root, "mods", "FabricApi.jar");
                if (userChangedMod) await File.WriteAllTextAsync(managed, "user replacement");
                string unmanaged = Path.Combine(root, "mods", "user-mod.jar");
                await File.WriteAllTextAsync(unmanaged, "user mod");
                var before = await MinecraftInstallEditService.ReadAsync(new(root, "custom"));
                AssertEqual(1, before.ManagedMods.Count);
                AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "custom", EditFingerprint: before.Fingerprint))).IsSuccess);
                AssertEqual(userChangedMod, File.Exists(managed));
                if (userChangedMod) AssertEqual("user replacement", await File.ReadAllTextAsync(managed));
                AssertEqual("user mod", await File.ReadAllTextAsync(unmanaged));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static async ValueTask ComponentEditsDoNotReinstallTheBase()
    {
        string root = CreateTempDirectory();
        try
        {
            var metadata = new FakeMetadata { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
            using var fixture = new InstallFixture(metadata, new FakeCatalog("https://example.invalid/fabricapi.jar"));
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Fabric, "0.16.9", [], "custom"))).IsSuccess);
            var original = await MinecraftInstallEditService.ReadAsync(new(root, "custom"));
            var same = MinecraftInstallEditPlanner.Evaluate(new(original, original.Selection));
            AssertEqual(MinecraftInstallEditKind.Unchanged, same.Kind);
            int reads = metadata.VanillaReads;
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Fabric, "0.16.9", [], "custom", original.Fingerprint))).IsSuccess);
            AssertEqual(reads, metadata.VanillaReads);
            var added = MinecraftInstallEditPlanner.Evaluate(new(original, [.. original.Selection, new(InstallLoader.FabricApi, "1.0.0")]));
            AssertEqual(MinecraftInstallEditKind.ComponentsOnly, added.Kind);
            AssertEqual("附加安装", added.ActionLabel);
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Fabric, "0.16.9", [new(InstallLoader.FabricApi, "1.0.0")], "custom", original.Fingerprint))).IsSuccess);
            AssertEqual(reads, metadata.VanillaReads);
            AssertTrue(File.Exists(Path.Combine(root, "mods", "FabricApi.jar")));
            var current = await MinecraftInstallEditService.ReadAsync(new(root, "custom"));
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Fabric, "0.16.9", [], "custom", current.Fingerprint))).IsSuccess);
            AssertFalse(File.Exists(Path.Combine(root, "mods", "FabricApi.jar")));
            AssertEqual(reads, metadata.VanillaReads);
            AssertEqual(MinecraftInstallEditKind.Reinstall, MinecraftInstallEditPlanner.Evaluate(new(original, [new(InstallLoader.Fabric, "0.17.0")])).Kind);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask EditDefaultsRecognizeModernForgeAndNeoForgeArguments()
    {
        string root = CreateTempDirectory();
        try
        {
            foreach (var loader in new[] { InstallLoader.Forge, InstallLoader.NeoForge })
            {
                string id = loader.ToString(); string directory = Path.Combine(root, "versions", id); Directory.CreateDirectory(directory);
                string key = loader == InstallLoader.Forge ? "--fml.forgeVersion" : "--fml.neoForgeVersion";
                var json = new JsonObject { ["id"] = id, ["arguments"] = new JsonObject { ["game"] = new JsonArray(key, "selected-build", "--fml.mcVersion", "1.20.1") } };
                await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), json.ToJsonString());
                var result = await MinecraftInstallEditService.ReadAsync(new(root, id));
                AssertEqual("1.20.1", result.GameVersion);
                AssertEqual(loader, result.Selection[0].Loader);
                AssertEqual("selected-build", result.Selection[0].Version);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class BrokenEditInstaller : IMinecraftLoaderInstaller
    {
        public Task<JsonObject> InstallAsync(MinecraftLoaderInstallRequest request, IProgress<string>? progress, CancellationToken token) => throw new IOException("fixture processor failure");
    }

    private static async ValueTask FailedEditKeepsOriginalManifest()
    {
        string root = CreateTempDirectory();
        try
        {
            using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, loaderInstaller: new BrokenEditInstaller());
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "custom"))).IsSuccess);
            var before = await MinecraftInstallEditService.ReadAsync(new(root, "custom"));
            string path = Path.Combine(root, "versions", "custom", "custom.json");
            string text = await File.ReadAllTextAsync(path);
            AssertFalse((await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Forge, "47.4.20", [], "custom", before.Fingerprint))).IsSuccess);
            AssertEqual(text, await File.ReadAllTextAsync(path));
            AssertEqual(0, Directory.GetDirectories(Path.Combine(root, ".nexa-modify")).Length);
        }
        finally { Directory.Delete(root, true); }
    }
}
