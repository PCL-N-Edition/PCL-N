using System.Text.Json.Nodes;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask LoaderInstallsAreIndependentUnlessOptedIn()
    {
        var definition = SettingsPolicySchema.ByKey["install.inherit-vanilla"];
        AssertEqual("false", definition.DefaultValue);
        AssertFalse(definition.InstanceOverride);
        AssertEqual(SettingsApplyTiming.NextTask, definition.Timing);
        AssertEqual(SettingsCapabilityAvailability.Available, SettingsCatalog.Entries.Single(row => row.SettingKey == definition.Key).Availability);
        foreach (bool attached in new[] { false, true })
        {
            string root = CreateTempDirectory();
            try
            {
                bool policy = attached;
                using var fixture = new InstallFixture(new()
                {
                    VanillaJson = VanillaJson(),
                    AssetIndexJson = AssetIndexJson(),
                    LoaderJson = new JsonObject { ["mainClass"] = "loader.Main", ["arguments"] = new JsonObject { ["game"] = new JsonArray("--loader-argument") } },
                }, inheritVanilla: () => { bool snapshot = policy; policy = !policy; return snapshot; });
                var result = await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Fabric, "0.16.9", InstanceName: "managed"));
                AssertTrue(result.IsSuccess, fixture.Entry().ErrorMessage ?? "install failed");
                string directory = result.Value!.InstanceDirectory;
                string jsonPath = Path.Combine(directory, "managed.json");
                var json = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath))!;
                AssertEqual(attached, json["inheritsFrom"] is not null);
                AssertEqual("loader.Main", json["mainClass"]!.ToString());
                if (attached)
                {
                    AssertEqual("1.20.1", json["inheritsFrom"]!.ToString());
                    AssertTrue(File.Exists(Path.Combine(root, "versions", "1.20.1", "1.20.1.json")));
                    continue;
                }
                AssertTrue(json["jar"] is null);
                AssertTrue(json["downloads"]?["client"] is not null);
                AssertTrue(json["libraries"]!.AsArray().Count > 0);
                AssertEqual("JARCONTENT", await File.ReadAllTextAsync(Path.Combine(directory, "managed.jar")));
                Directory.Delete(Path.Combine(root, "versions", "1.20.1"), true);
                var instance = new MinecraftInstanceDescriptor("managed", directory, "managed",
                    new MinecraftVersionDescriptor("managed", directory, jsonPath, Path.Combine(directory, "managed.jar"), null,
                        "loader.Main", null, new MinecraftVersionClassification("1.20.1", "release", MinecraftVersionCategory.Release, null)), new());
                var resolved = await MinecraftVersionJsonReader.ResolveAsync(instance, root);
                AssertEqual(0, resolved.Inherited.Count);
                var edit = await MinecraftInstallEditService.ReadAsync(new(root, "managed"));
                AssertEqual("1.20.1", edit.GameVersion);
                AssertEqual(InstallLoader.Fabric, edit.Selection.Single().Loader);
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
