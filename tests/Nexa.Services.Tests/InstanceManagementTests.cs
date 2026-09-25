using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void InstanceContentIsBoundedAndCancellable()
    {
        string root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "folder"));
            File.WriteAllText(Path.Combine(root, "b.zip"), "abc");
            File.WriteAllText(Path.Combine(root, "a.zip"), "de");
            var page = new InstanceManagementPage("resourcepacks", "资源包", root);
            var result = InstanceManagementService.ReadContent(page, CancellationToken.None);
            AssertEqual(3, result.Entries.Count);
            AssertTrue(result.Complete);
            AssertTrue(result.Entries[0].IsDirectory);
            AssertEqual("a.zip", result.Entries[1].Name);
            AssertEqual(2L, result.Entries[1].Size!.Value);
            var bounded = InstanceManagementService.ReadContent(page, CancellationToken.None, 2);
            AssertFalse(bounded.Complete); AssertEqual(2, bounded.Entries.Count);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { InstanceManagementService.ReadContent(page, stop.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void InstanceManagementPagesFollowEnabledCapabilities()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-instance-pages");
        string[] Pages(InstallBuildSelection[] components, params LaunchModIdentity[] mods) =>
            InstanceManagementService.Pages(directory, components, new(mods, 0, true)).Select(page => page.Id).ToArray();
        LaunchModIdentity Mod(string id, bool enabled = true) => new(id, "1", "fabric.mod.json", enabled, new Dictionary<string, string>(), true);
        var vanilla = Pages([]);
        AssertFalse(vanilla.Contains("mods"));
        AssertFalse(vanilla.Contains("shaderpacks"));
        AssertFalse(vanilla.Contains("schematics"));
        foreach (string id in new[] { "overview", "game", "java", "components", "resourcepacks", "saves", "screenshots", "servers", "modpack" })
            AssertTrue(vanilla.Contains(id));
        InstallBuildSelection[] fabric = [new(InstallLoader.Fabric, "0.16.0")];
        AssertTrue(Pages(fabric).Contains("mods"));
        AssertTrue(Pages(fabric, Mod("iris"), Mod("litematica")).Contains("shaderpacks"));
        AssertTrue(Pages(fabric, Mod("iris"), Mod("litematica")).Contains("schematics"));
        AssertFalse(Pages(fabric, Mod("iris", false), Mod("optifabric"), Mod("iris-helper")).Contains("shaderpacks"));
        AssertFalse(Pages(fabric, Mod("litematica", false), Mod("litematica-helper")).Contains("schematics"));
        AssertFalse(Pages([], Mod("iris"), Mod("litematica")).Contains("shaderpacks"));
        AssertTrue(Pages([new(InstallLoader.OptiFine, "1.12.2_HD_U_G5")]).Contains("shaderpacks"));
        var all = InstanceManagementService.Pages(directory, fabric, new([Mod("iris"), Mod("litematica")], 0, true));
        AssertEqual(all.Count, all.Select(page => page.Id).Distinct().Count());
        AssertEqual(Path.Combine(directory, "shaderpacks"), all.Single(page => page.Id == "shaderpacks").Directory!);
    }

    private static async ValueTask InstanceManagementReadsExactInstance()
    {
        string root = CreateTempDirectory();
        try
        {
            string directory = CreateVersionDirectory(root, "fixture", new JsonObject
            {
                ["id"] = "fixture",
                ["mainClass"] = "example.Main",
                ["_minecraftVersion"] = "1.20.1",
                ["libraries"] = new JsonArray(new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.0" }),
            });
            var result = await InstanceManagementService.ReadAsync(new(directory));
            AssertEqual(directory, result.InstanceDirectory);
            AssertEqual(directory, result.GameDirectory);
            AssertEqual("1.20.1", result.GameVersion);
            AssertTrue(result.Pages.Any(page => page.Id == "mods"));
            await new Nexa.Services.Minecraft.MinecraftInstanceMetadataStore().SaveAsync(directory,
                new Nexa.Services.Minecraft.MinecraftInstanceMetadata { InstanceIsolation = false });
            var shared = await InstanceManagementService.ReadAsync(new(directory));
            AssertEqual(root, shared.GameDirectory);
            AssertEqual(Path.Combine(root, "mods"), shared.Pages.Single(page => page.Id == "mods").Directory!);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await InstanceManagementService.ReadAsync(new(directory), stop.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            try { await InstanceManagementService.ReadAsync(new("relative/instance")); throw new InvalidOperationException("Relative identity accepted."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }
}
