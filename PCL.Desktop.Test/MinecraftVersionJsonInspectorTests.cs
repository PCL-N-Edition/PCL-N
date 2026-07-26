// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Instances;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Shared;
using PCL.Domain.Minecraft.Launch;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MinecraftVersionJsonInspectorTests
{
    [TestMethod]
    public void Discovery_FindsVersionJsonWhenDirectoryFileAndIdDiffer()
    {
        string root = CreateTemporaryRoot("pcl-discovery-mismatched-json-");
        string versionDirectory = Path.Combine(root, "versions", "Imported Loader");
        string jsonPath = Path.Combine(versionDirectory, "third-party-profile.json");

        try
        {
            Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(
                jsonPath,
                """
                {
                  "id": "external-loader-id",
                  "inheritsFrom": "1.20.1",
                  "libraries": [{ "name": "net.fabricmc:fabric-loader:0.16.14" }]
                }
                """);

            LaunchInstanceInfo instance = LaunchInstanceDiscovery.Discover([root]).Single();

            Assert.AreEqual("Imported Loader", instance.Name);
            Assert.AreEqual(jsonPath, instance.VersionJsonPath);
            Assert.AreEqual(versionDirectory, instance.InstanceDirectory);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [TestMethod]
    public void Inspector_ResolvesMultiLevelInheritanceByJsonId()
    {
        string root = CreateTemporaryRoot("pcl-inspector-json-id-");
        string instanceDirectory = Path.Combine(root, "versions", "Renamed Pack");
        string loaderDirectory = Path.Combine(root, "versions", "Loader Alias");
        string vanillaDirectory = Path.Combine(root, "versions", "Vanilla Alias");
        string instanceJsonPath = Path.Combine(instanceDirectory, "pack-profile.json");

        try
        {
            Directory.CreateDirectory(instanceDirectory);
            Directory.CreateDirectory(loaderDirectory);
            Directory.CreateDirectory(vanillaDirectory);
            File.WriteAllText(
                instanceJsonPath,
                """
                {
                  "id": "pack-json-id",
                  "inheritsFrom": "loader-json-id",
                  "libraries": []
                }
                """);
            File.WriteAllText(
                Path.Combine(loaderDirectory, "loader-profile.json"),
                """
                {
                  "id": "loader-json-id",
                  "inheritsFrom": "vanilla-json-id",
                  "libraries": [{ "name": "net.neoforged:neoforge:21.1.204" }]
                }
                """);
            File.WriteAllText(
                Path.Combine(vanillaDirectory, "client-profile.json"),
                """
                {
                  "id": "vanilla-json-id",
                  "clientVersion": "1.21.1",
                  "libraries": [{ "name": "com.mojang:brigadier:1.0.18" }]
                }
                """);
            LaunchInstanceInfo instance = new("Renamed Pack", instanceJsonPath, instanceDirectory);

            MinecraftVersionJsonInfo info = MinecraftVersionJsonInspector.Read(instance);

            Assert.AreEqual("1.21.1", info.MinecraftVersionId);
            Assert.AreEqual("loader-json-id", info.InheritsFrom);
            Assert.IsTrue(info.Libraries.Any(static entry =>
                entry.Contains("net.neoforged:neoforge:21.1.204", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(info.Libraries.Any(static entry =>
                entry.Contains("com.mojang:brigadier:1.0.18", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(MinecraftLaunchCoordinator.BuildLaunchProfile(instance).HasForge);
            StringAssert.EndsWith(
                InstanceDisplayHelper.ResolveLogo(instance, new InstanceMetadata()),
                "NeoForge.png");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [TestMethod]
    public void Inspector_RecognizesSupportedLoaderProfiles()
    {
        string root = CreateTemporaryRoot("pcl-inspector-loaders-");
        (string Name, string Json, string Needle, string Icon)[] cases =
        [
            ("Forge", """{"id":"forge-profile","clientVersion":"1.20.1","libraries":[{"name":"net.minecraftforge:forge:1.20.1-47.3.0"}]}""", "net.minecraftforge:forge", "Anvil.png"),
            ("NeoForge", """{"id":"neoforge-profile","clientVersion":"1.21.1","libraries":[{"name":"net.neoforged:neoforge:21.1.204"}]}""", "net.neoforged:neoforge", "NeoForge.png"),
            ("Fabric", """{"id":"fabric-profile","clientVersion":"1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.14"}]}""", "fabric-loader", "Fabric.png"),
            ("LegacyFabric", """{"id":"legacy-fabric-profile","clientVersion":"1.12.2","libraries":[{"name":"net.legacyfabric:intermediary:1.12.2"},{"name":"net.fabricmc:fabric-loader:0.16.14"}]}""", "legacyfabric", "Fabric.png"),
            ("Quilt", """{"id":"quilt-profile","clientVersion":"1.20.1","libraries":[{"name":"org.quiltmc:quilt-loader:0.28.1"}]}""", "quilt-loader", "Quilt.png"),
            ("LabyMod", """{"id":"custom-profile","clientVersion":"1.20.1","labymod_data":{"version":"4.2.0"}}""", "labymod", "LabyMod.png"),
            ("OptiFine", """{"id":"custom-profile","clientVersion":"1.20.1","arguments":{"game":["--tweakClass","optifine.OptiFineTweaker"]}}""", "optifine", "GrassPath.png"),
            ("LiteLoader", """{"id":"custom-profile","jar":"1.5.2","mainClass":"net.minecraft.launchwrapper.Launch","arguments":{"game":["--tweakClass","com.mumfrey.liteloader.launch.LiteLoaderTweaker"]},"libraries":[{"name":"com.mumfrey:liteloader:1.5.2"}]}""", "liteloader", "Egg.png")
        ];

        try
        {
            foreach ((string name, string json, string needle, string icon) in cases)
            {
                string versionDirectory = Path.Combine(root, "versions", name);
                string jsonPath = Path.Combine(versionDirectory, "profile.json");
                Directory.CreateDirectory(versionDirectory);
                File.WriteAllText(jsonPath, json);
                LaunchInstanceInfo instance = new(name, jsonPath, versionDirectory);

                MinecraftVersionJsonInfo info = MinecraftVersionJsonInspector.Read(instance);
                MinecraftLaunchProfile profile = MinecraftLaunchCoordinator.BuildLaunchProfile(instance);

                Assert.IsTrue(
                    info.LoaderEntries.Any(entry => entry.Contains(needle, StringComparison.OrdinalIgnoreCase)),
                    name);
                Assert.IsTrue(InstanceDisplayHelper.IsModable(instance), name);
                StringAssert.EndsWith(
                    InstanceDisplayHelper.ResolveLogo(instance, new InstanceMetadata()),
                    icon,
                    name);
                AssertLoaderProfile(name, profile);
            }
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static void AssertLoaderProfile(string name, MinecraftLaunchProfile profile)
    {
        switch (name)
        {
            case "Forge":
            case "NeoForge":
                Assert.IsTrue(profile.HasForge, name);
                break;
            case "Fabric":
            case "LegacyFabric":
            case "Quilt":
                Assert.IsTrue(profile.HasFabric, name);
                break;
            case "LabyMod":
                Assert.IsTrue(profile.HasLabyMod, name);
                break;
            case "OptiFine":
                Assert.IsTrue(profile.HasOptiFine, name);
                break;
            case "LiteLoader":
                Assert.IsTrue(profile.HasLiteLoader, name);
                Assert.AreEqual(new Version(5, 0, 2, 0), profile.VanillaVersion);
                break;
        }
    }

    private static string CreateTemporaryRoot(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
