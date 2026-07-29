// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Launching;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftProcessLaunchServiceTests
{
    [TestMethod]
    public void ParseCommandLine_PreservesQuotedAndEmptyArguments()
    {
        IReadOnlyList<string> parsed = MinecraftProcessLaunchService.ParseCommandLine(
            "-Dpath=\"C:\\Game Files\\data\" -cp \"C:\\Game Files\\client.jar\" Main --name \"Offline User\" \"\"");

        CollectionAssert.AreEqual(
            new[]
            {
                "-Dpath=C:\\Game Files\\data",
                "-cp",
                "C:\\Game Files\\client.jar",
                "Main",
                "--name",
                "Offline User",
                string.Empty
            },
            parsed.ToArray());
    }

    [TestMethod]
    public void NormalizeJvmHostVmArguments_CanonicalizesNeoForgeModuleOptions()
    {
        IReadOnlyList<string> normalized = MinecraftProcessLaunchService.NormalizeJvmHostVmArguments(
            [
                "-Xmx3379m",
                "-cp", "client.jar;libraries.jar",
                "-p", "bootstrap.jar;securejarhandler.jar",
                "--add-modules", "ALL-MODULE-PATH",
                "--add-opens", "java.base/java.util.jar=cpw.mods.securejarhandler",
                "--add-exports", "java.base/sun.security.util=cpw.mods.securejarhandler",
                "-Djava.net.preferIPv4Stack=true"
            ]);

        CollectionAssert.AreEqual(
            new[]
            {
                "-Xmx3379m",
                "--module-path=bootstrap.jar;securejarhandler.jar",
                "--add-modules=ALL-MODULE-PATH",
                "--add-opens=java.base/java.util.jar=cpw.mods.securejarhandler",
                "--add-exports=java.base/sun.security.util=cpw.mods.securejarhandler",
                "-Djava.net.preferIPv4Stack=true"
            },
            normalized.ToArray());
    }

    [TestMethod]
    public void NormalizeJvmHostVmArguments_RejectsMissingPairedOptionValue()
    {
        AssertFormatException(["--add-modules"]);
        AssertFormatException(["--add-opens", "--add-exports", "x=y"]);
        AssertFormatException(["-cp"]);
    }

    [TestMethod]
    public void AddExecutableBits_PreservesAccessAndEnablesReadableScopes()
    {
        UnixFileMode original =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead;

        UnixFileMode executable = MinecraftProcessLaunchService.AddExecutableBits(original);

        Assert.IsTrue(executable.HasFlag(UnixFileMode.UserExecute));
        Assert.IsTrue(executable.HasFlag(UnixFileMode.GroupExecute));
        Assert.IsTrue(executable.HasFlag(UnixFileMode.OtherExecute));
        Assert.IsTrue(executable.HasFlag(UnixFileMode.UserWrite));
    }

    private static void AssertFormatException(IReadOnlyList<string> arguments)
    {
        try
        {
            MinecraftProcessLaunchService.NormalizeJvmHostVmArguments(arguments);
            Assert.Fail("Expected FormatException.");
        }
        catch (FormatException)
        {
        }
    }

    [TestMethod]
    public async Task CreatePlanAsync_ProducesStructuredJvmHostRequest()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-jvm-host-plan-" + Guid.NewGuid().ToString("N"));
        string instanceDirectory = Path.Combine(root, "versions", "Host Test");
        string versionJsonPath = Path.Combine(instanceDirectory, "Host Test.json");
        string versionJarPath = Path.Combine(instanceDirectory, "Host Test.jar");
        string classpathHead = Path.Combine(root, "custom libraries", "head.jar");

        try
        {
            Directory.CreateDirectory(instanceDirectory);
            await File.WriteAllTextAsync(
                versionJsonPath,
                """
                {
                  "mainClass": "net.minecraft.client.main.Main",
                  "arguments": {
                    "jvm": ["-Dhost.test=true", "-cp", "${classpath}"],
                    "game": ["--username", "${auth_player_name}", "--gameDir", "${game_directory}"]
                  }
                }
                """);
            await File.WriteAllTextAsync(versionJarPath, string.Empty);

            MinecraftProcessLaunchPlan plan = await MinecraftProcessLaunchService.CreatePlanAsync(
                new MinecraftProcessLaunchRequest
                {
                    VersionId = "Host Test",
                    VersionJsonPath = versionJsonPath,
                    InstanceDirectory = instanceDirectory,
                    MinecraftRootDirectory = root,
                    PlayerName = "Offline User",
                    PlayerUuid = "01234567-89ab-cdef-0123-456789abcdef",
                    JavaExecutablePath = Path.Combine(root, "runtime", "bin", "java"),
                    JavaMajorVersion = 17,
                    ClasspathHeadEntries = [classpathHead],
                    UseExperimentalJvmHost = true,
                    JvmHostIdentityMode = MinecraftJvmHostIdentityMode.Offline,
                    OfflineSkinSource = Path.Combine(root, "skin.png"),
                    OfflineSkinSlim = true
                });

            Assert.IsNotNull(plan.JvmHostRequest);
            MinecraftJvmHostRequest host = plan.JvmHostRequest;
            Assert.AreEqual("net.minecraft.client.main.Main", host.MainClass);
            Assert.AreEqual(MinecraftJvmHostIdentityMode.Offline, host.IdentityMode);
            Assert.AreEqual("0123456789abcdef0123456789abcdef", host.PlayerUuid);
            Assert.IsTrue(host.OfflineSkinSlim);
            CollectionAssert.Contains(host.VmArguments, "-Dhost.test=true");
            CollectionAssert.DoesNotContain(host.VmArguments, "-cp");
            CollectionAssert.AreEqual(new[] { classpathHead, versionJarPath }, host.ClasspathEntries);
            CollectionAssert.Contains(host.GameArguments, "Offline User");
            Assert.IsFalse(plan.StartInfo.Arguments.Contains("-javaagent:", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreatePlanAsync_AppliesInstanceLaunchOverrides()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-launch-plan-" + Guid.NewGuid().ToString("N"));
        string instanceDirectory = Path.Combine(root, "versions", "CustomPack");
        string versionJsonPath = Path.Combine(instanceDirectory, "CustomPack.json");
        string versionJarPath = Path.Combine(instanceDirectory, "CustomPack.jar");
        string classpathHead = Path.Combine(root, "custom-head.jar");

        try
        {
            Directory.CreateDirectory(instanceDirectory);
            await File.WriteAllTextAsync(
                versionJsonPath,
                """
                {
                  "mainClass": "net.minecraft.client.main.Main",
                  "arguments": {
                    "jvm": [
                      "-cp",
                      "${classpath}"
                    ],
                    "game": [
                      "--username",
                      "${auth_player_name}",
                      "--gameDir",
                      "${game_directory}"
                    ]
                  }
                }
                """);
            await File.WriteAllTextAsync(versionJarPath, string.Empty);

            MinecraftProcessLaunchPlan plan = await MinecraftProcessLaunchService.CreatePlanAsync(
                new MinecraftProcessLaunchRequest
                {
                    VersionId = "CustomPack",
                    VersionJsonPath = versionJsonPath,
                    InstanceDirectory = instanceDirectory,
                    MinecraftRootDirectory = root,
                    PlayerName = "Steve",
                    PlayerUuid = "00000000000000000000000000000000",
                    JavaExecutablePath = "java",
                    MemoryMegabytes = 3072,
                    IsolatedGameDirectory = true,
                    CustomJvmArguments = "-XX:+UseZGC",
                    CustomGameArguments = "--demo",
                    ClasspathHeadEntries = [classpathHead],
                    AuthlibInjectorPath = Path.Combine(root, "authlib-injector.jar"),
                    AuthlibServer = "https://example.com/api/yggdrasil",
                    AuthlibPrefetchedMetadata = "{}",
                    Server = "play.example.com",
                    ReleaseTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
                });

            Assert.AreEqual(instanceDirectory, plan.StartInfo.WorkingDirectory);
            StringAssert.Contains(plan.StartInfo.Arguments, "-Xmx3072m");
            StringAssert.Contains(plan.StartInfo.Arguments, "-XX:+UseZGC");
            StringAssert.Contains(plan.StartInfo.Arguments, "-javaagent:");
            StringAssert.Contains(plan.StartInfo.Arguments, "authlib-injector.jar=https://example.com/api/yggdrasil");
            StringAssert.Contains(plan.StartInfo.Arguments, "-Dauthlibinjector.yggdrasil.prefetched=e30=");
            StringAssert.Contains(plan.StartInfo.Arguments, "--demo");
            StringAssert.Contains(plan.StartInfo.Arguments, "--quickPlayMultiplayer \"play.example.com\"");
            CollectionAssert.AreEqual(new[] { classpathHead, versionJarPath }, plan.ClasspathEntries.ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreatePlanAsync_LoadsInheritedVersionJsonForLoaderInstances()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-launch-inherits-" + Guid.NewGuid().ToString("N"));
        string baseDirectory = Path.Combine(root, "versions", "1.20.1");
        string loaderDirectory = Path.Combine(root, "versions", "fabric-loader-0.16.14-1.20.1");
        string baseJsonPath = Path.Combine(baseDirectory, "1.20.1.json");
        string baseJarPath = Path.Combine(baseDirectory, "1.20.1.jar");
        string loaderJsonPath = Path.Combine(loaderDirectory, "fabric-loader-0.16.14-1.20.1.json");

        try
        {
            Directory.CreateDirectory(baseDirectory);
            Directory.CreateDirectory(loaderDirectory);
            await File.WriteAllTextAsync(baseJsonPath,
                """
                {
                  "id": "1.20.1",
                  "mainClass": "net.minecraft.client.main.Main",
                  "arguments": {
                    "jvm": [
                      "-cp",
                      "${classpath}"
                    ],
                    "game": [
                      "--username",
                      "${auth_player_name}",
                      "--gameDir",
                      "${game_directory}",
                      "--versionType",
                      "${version_type}",
                      "--launcherName",
                      "${launcher_name}"
                    ]
                  },
                  "assetIndex": {
                    "id": "empty"
                  },
                  "libraries": [
                    {
                      "name": "com.mojang:brigadier:1.0.18",
                      "url": "https://libraries.minecraft.net/"
                    }
                  ]
                }
                """);
            await File.WriteAllTextAsync(baseJarPath, string.Empty);
            await File.WriteAllTextAsync(loaderJsonPath,
                """
                {
                  "id": "fabric-loader-0.16.14-1.20.1",
                  "inheritsFrom": "1.20.1",
                  "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
                  "libraries": [
                    {
                      "name": "net.fabricmc:fabric-loader:0.16.14",
                      "url": "https://maven.fabricmc.net/"
                    }
                  ]
                }
                """);

            MinecraftProcessLaunchPlan plan = await MinecraftProcessLaunchService.CreatePlanAsync(
                new MinecraftProcessLaunchRequest
                {
                    VersionId = "fabric-loader-0.16.14-1.20.1",
                    VersionJsonPath = loaderJsonPath,
                    InstanceDirectory = loaderDirectory,
                    MinecraftRootDirectory = root,
                    PlayerName = "Steve",
                    PlayerUuid = "00000000000000000000000000000000",
                    LauncherName = "Custom Launcher",
                    VersionType = "Custom Type",
                    JavaExecutablePath = "java"
                });

            StringAssert.Contains(plan.StartInfo.Arguments, "net.fabricmc.loader.impl.launch.knot.KnotClient");
            StringAssert.Contains(plan.StartInfo.Arguments, "--username Steve");
            StringAssert.Contains(plan.StartInfo.Arguments, "--versionType \"Custom Type\"");
            StringAssert.Contains(plan.StartInfo.Arguments, "--launcherName \"Custom Launcher\"");
            CollectionAssert.Contains(plan.ClasspathEntries.ToArray(), baseJarPath);
            Assert.IsTrue(plan.ClasspathEntries.Any(path => path.EndsWith(
                Path.Combine("net", "fabricmc", "fabric-loader", "0.16.14", "fabric-loader-0.16.14.jar"),
                StringComparison.Ordinal)));
            Assert.IsTrue(plan.ClasspathEntries.Any(path => path.EndsWith(
                Path.Combine("com", "mojang", "brigadier", "1.0.18", "brigadier-1.0.18.jar"),
                StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreatePlanAsync_ResolvesJarAndMultiLevelInheritanceByJsonIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-launch-json-identity-" + Guid.NewGuid().ToString("N"));
        string instanceDirectory = Path.Combine(root, "versions", "Renamed Pack");
        string loaderDirectory = Path.Combine(root, "versions", "Loader Directory");
        string vanillaDirectory = Path.Combine(root, "versions", "Vanilla Directory");
        string instanceJsonPath = Path.Combine(instanceDirectory, "launcher-profile.json");
        string loaderJsonPath = Path.Combine(loaderDirectory, "loader-data.json");
        string vanillaJsonPath = Path.Combine(vanillaDirectory, "client-manifest.json");
        string vanillaJarPath = Path.Combine(vanillaDirectory, "client-manifest.jar");

        try
        {
            Directory.CreateDirectory(instanceDirectory);
            Directory.CreateDirectory(loaderDirectory);
            Directory.CreateDirectory(vanillaDirectory);
            await File.WriteAllTextAsync(
                instanceJsonPath,
                """
                {
                  "id": "profile-id",
                  "inheritsFrom": "quilt-profile-id",
                  "jar": "minecraft-client-id",
                  "mainClass": "org.quiltmc.loader.impl.launch.knot.KnotClient",
                  "libraries": [
                    { "name": "org.quiltmc:quilt-loader:0.28.1" }
                  ]
                }
                """);
            await File.WriteAllTextAsync(
                loaderJsonPath,
                """
                {
                  "id": "quilt-profile-id",
                  "inheritsFrom": "minecraft-client-id",
                  "libraries": [
                    { "name": "org.ow2.asm:asm:9.7.1" }
                  ]
                }
                """);
            await File.WriteAllTextAsync(
                vanillaJsonPath,
                """
                {
                  "id": "minecraft-client-id",
                  "arguments": {
                    "jvm": ["-cp", "${classpath}"],
                    "game": ["--username", "${auth_player_name}"]
                  },
                  "libraries": [
                    { "name": "com.mojang:brigadier:1.0.18" }
                  ]
                }
                """);
            await File.WriteAllTextAsync(vanillaJarPath, string.Empty);

            MinecraftProcessLaunchPlan plan = await MinecraftProcessLaunchService.CreatePlanAsync(
                new MinecraftProcessLaunchRequest
                {
                    VersionId = "Renamed Pack",
                    VersionJsonPath = instanceJsonPath,
                    InstanceDirectory = instanceDirectory,
                    MinecraftRootDirectory = root,
                    PlayerName = "Steve",
                    PlayerUuid = "00000000000000000000000000000000",
                    JavaExecutablePath = "java"
                });

            StringAssert.Contains(plan.StartInfo.Arguments, "org.quiltmc.loader.impl.launch.knot.KnotClient");
            StringAssert.Contains(plan.StartInfo.Arguments, "--username Steve");
            CollectionAssert.Contains(plan.ClasspathEntries.ToArray(), vanillaJarPath);
            Assert.IsTrue(plan.ClasspathEntries.Any(path => path.EndsWith(
                Path.Combine("org", "quiltmc", "quilt-loader", "0.28.1", "quilt-loader-0.28.1.jar"),
                StringComparison.Ordinal)));
            Assert.IsTrue(plan.ClasspathEntries.Any(path => path.EndsWith(
                Path.Combine("org", "ow2", "asm", "asm", "9.7.1", "asm-9.7.1.jar"),
                StringComparison.Ordinal)));
            Assert.IsTrue(plan.ClasspathEntries.Any(path => path.EndsWith(
                Path.Combine("com", "mojang", "brigadier", "1.0.18", "brigadier-1.0.18.jar"),
                StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreatePlanAsync_UsesJarAsLegacyLiteLoaderInheritance()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-launch-legacy-lite-" + Guid.NewGuid().ToString("N"));
        string instanceDirectory = Path.Combine(root, "versions", "Legacy Pack");
        string vanillaDirectory = Path.Combine(root, "versions", "Vanilla Alias");
        string instanceJsonPath = Path.Combine(instanceDirectory, "legacy-profile.json");
        string vanillaJsonPath = Path.Combine(vanillaDirectory, "vanilla-manifest.json");
        string vanillaJarPath = Path.Combine(vanillaDirectory, "vanilla-manifest.jar");

        try
        {
            Directory.CreateDirectory(instanceDirectory);
            Directory.CreateDirectory(vanillaDirectory);
            await File.WriteAllTextAsync(
                instanceJsonPath,
                """
                {
                  "id": "legacy-liteloader-profile",
                  "jar": "1.5.2",
                  "mainClass": "net.minecraft.launchwrapper.Launch",
                  "arguments": {
                    "game": ["--tweakClass", "com.mumfrey.liteloader.launch.LiteLoaderTweaker"]
                  },
                  "libraries": [
                    { "name": "com.mumfrey:liteloader:1.5.2" }
                  ]
                }
                """);
            await File.WriteAllTextAsync(
                vanillaJsonPath,
                """
                {
                  "id": "1.5.2",
                  "arguments": {
                    "jvm": ["-cp", "${classpath}"]
                  },
                  "libraries": [
                    { "name": "net.minecraft:launchwrapper:1.8" }
                  ]
                }
                """);
            await File.WriteAllTextAsync(vanillaJarPath, string.Empty);

            MinecraftProcessLaunchPlan plan = await MinecraftProcessLaunchService.CreatePlanAsync(
                new MinecraftProcessLaunchRequest
                {
                    VersionId = "Legacy Pack",
                    VersionJsonPath = instanceJsonPath,
                    InstanceDirectory = instanceDirectory,
                    MinecraftRootDirectory = root,
                    PlayerName = "Steve",
                    PlayerUuid = "00000000000000000000000000000000",
                    JavaExecutablePath = "java"
                });

            StringAssert.Contains(plan.StartInfo.Arguments, "net.minecraft.launchwrapper.Launch");
            StringAssert.Contains(plan.StartInfo.Arguments, "com.mumfrey.liteloader.launch.LiteLoaderTweaker");
            CollectionAssert.Contains(plan.ClasspathEntries.ToArray(), vanillaJarPath);
            Assert.IsTrue(plan.ClasspathEntries.Any(path => path.EndsWith(
                Path.Combine("com", "mumfrey", "liteloader", "1.5.2", "liteloader-1.5.2.jar"),
                StringComparison.Ordinal)));
            Assert.IsTrue(plan.ClasspathEntries.Any(path => path.EndsWith(
                Path.Combine("net", "minecraft", "launchwrapper", "1.8", "launchwrapper-1.8.jar"),
                StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreatePlanAsync_FiltersJdk23OnlyArgumentsUsingSelectedJavaVersion()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-launch-java-version-" + Guid.NewGuid().ToString("N"));
        string instanceDirectory = Path.Combine(root, "versions", "JavaVersionTest");
        string versionJsonPath = Path.Combine(instanceDirectory, "JavaVersionTest.json");
        string versionJarPath = Path.Combine(instanceDirectory, "JavaVersionTest.jar");

        try
        {
            Directory.CreateDirectory(instanceDirectory);
            await File.WriteAllTextAsync(
                versionJsonPath,
                """
                {
                  "mainClass": "net.minecraft.client.main.Main",
                  "arguments": {
                    "jvm": [
                      {
                        "rules": [{ "action": "allow" }],
                        "value": [" --sun-misc-unsafe-memory-access=allow ", "-cp", "${classpath}"]
                      }
                    ],
                    "game": []
                  }
                }
                """);
            await File.WriteAllTextAsync(versionJarPath, string.Empty);

            MinecraftProcessLaunchRequest request = new()
            {
                VersionId = "JavaVersionTest",
                VersionJsonPath = versionJsonPath,
                InstanceDirectory = instanceDirectory,
                MinecraftRootDirectory = root,
                PlayerName = "Steve",
                PlayerUuid = "00000000000000000000000000000000",
                JavaExecutablePath = "java",
                JavaMajorVersion = 21
            };

            MinecraftProcessLaunchPlan java21 = await MinecraftProcessLaunchService.CreatePlanAsync(request);
            MinecraftProcessLaunchPlan java23 = await MinecraftProcessLaunchService.CreatePlanAsync(
                request with { JavaMajorVersion = 23 });

            Assert.IsFalse(java21.StartInfo.Arguments.Contains(
                "--sun-misc-unsafe-memory-access=allow",
                StringComparison.Ordinal));
            StringAssert.Contains(java23.StartInfo.Arguments, "--sun-misc-unsafe-memory-access=allow");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
