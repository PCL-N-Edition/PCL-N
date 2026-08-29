using System.Globalization;
using System.Text.Json;
using PCL.Services.Minecraft;

namespace PCL.Services.Tests;

internal static partial class Program
{
    internal static void MinecraftVersionClassifierMatchesCanonicalAliases()
    {
        MinecraftVersionClassification release = MinecraftVersionClassifier.Classify(
            new MinecraftVersionManifestEntry("1.20.5", "snapshot", "https://example.invalid/1.20.5.json", DateTimeOffset.Parse("2024-04-23T00:00:00Z", CultureInfo.InvariantCulture)));
        MinecraftVersionClassification fool = MinecraftVersionClassifier.Classify(
            new MinecraftVersionManifestEntry("20w14infinite", "snapshot", "https://example.invalid/fool.json", DateTimeOffset.Parse("2020-04-01T14:00:00Z", CultureInfo.InvariantCulture)));

        AssertEqual(MinecraftVersionCategory.Release, release.Category);
        AssertEqual("release", release.Type);
        AssertEqual(MinecraftVersionCategory.AprilFools, fool.Category);
        AssertEqual("20w14∞", fool.Id);
        AssertEqual("Classic_0.30", MinecraftVersionClassifier.FormatVersion("c0.30_01c"));
        AssertEqual("Beta_1.6_Test_Build_3", MinecraftVersionClassifier.FormatVersion("b1.6-tb3"));
    }

    internal static void MinecraftVersionDiscoveryUsesStableSafeResolution()
    {
        string root = CreateTempDirectory();
        try
        {
            string versions = Path.Combine(root, "versions");
            string primary = Path.Combine(versions, "1.20.1");
            string inherited = Path.Combine(versions, "loader");
            Directory.CreateDirectory(primary);
            Directory.CreateDirectory(inherited);
            File.WriteAllText(Path.Combine(primary, "1.20.1.json"), "{\"id\":\"1.20.1\",\"type\":\"release\",\"mainClass\":\"net.minecraft.client.main.Main\"}");
            File.WriteAllBytes(Path.Combine(primary, "1.20.1.jar"), [1]);
            File.WriteAllText(Path.Combine(inherited, "loader.json"), "{\"id\":\"1.20.1-loader\",\"inheritsFrom\":\"1.20.1\",\"mainClass\":\"loader.Main\"}");

            AssertNull(MinecraftVersionPaths.ResolveJsonPath(root, null, "../1.20.1"));
            AssertEqual(Path.Combine(primary, "1.20.1.json"), MinecraftVersionPaths.ResolveJsonPath(root, null, "1.20.1"));
            AssertEqual(Path.Combine(primary, "1.20.1.jar"), MinecraftVersionPaths.ResolveJarPath(root, null, "1.20.1"));
            IReadOnlyList<MinecraftVersionDescriptor> discovered = new MinecraftVersionDiscovery().Discover(root);
            AssertEqual(2, discovered.Count);
            AssertEqual("1.20.1", discovered[0].Id);
            AssertEqual("1.20.1-loader", discovered[1].Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async ValueTask MinecraftInstanceMetadataRoundTripsAtomically()
    {
        string root = CreateTempDirectory();
        try
        {
            MinecraftInstanceMetadataStore store = new();
            await store.SaveAsync(root, new MinecraftInstanceMetadata { Description = "Survival", LaunchCount = 2, IsStarred = true });
            MinecraftInstanceMetadata loaded = await store.LoadAsync(root);
            AssertEqual("Survival", loaded.Description);
            AssertEqual(2, loaded.LaunchCount);
            AssertTrue(loaded.IsStarred);

            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.UpdateAsync(
                root,
                metadata => metadata with { LaunchCount = metadata.LaunchCount + 1 })));
            AssertEqual(22, (await store.LoadAsync(root)).LaunchCount);
            AssertTrue(File.Exists(store.GetMetadataPath(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
