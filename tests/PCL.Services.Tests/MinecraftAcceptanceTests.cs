using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Java;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.Process;
using PCL.Xsr;

namespace PCL.Services.Tests;

// XSR-606: Wave 6 acceptance hardening — Java range conflicts, full launch token coverage,
// client JAR on the classpath, ARM64 native classification, Mojang rule semantics, natives
// extraction, process state in the host store, and manifest-controlled path containment.
internal static partial class Program
{
    internal static void ConflictingJavaRangesAreRejected()
    {
        JavaVersionRange java21 = JavaVersionRange.ForMajor(21);
        JavaVersionRange java8 = JavaVersionRange.ForMajor(8);

        AssertFalse(java21.TryIntersect(java8, out JavaVersionRange conflict));
        AssertEqual(JavaVersionRange.Any, conflict);

        JavaRequirementResolution resolution = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasCleanroom = true,
            CleanroomVersion = "1.0.0",
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(8, 0),
            HasForge = true,
            ForgeVersion = "14.23.5.2859",
        });
        AssertFalse(resolution.Success);
        AssertEqual(JavaRequirementFailureReason.ConflictingRequirements, resolution.FailureReason);
    }

    internal static void OverlappingJavaRangesNarrowCorrectly()
    {
        JavaVersionRange any = JavaVersionRange.Any;
        AssertTrue(any.TryIntersect(JavaVersionRange.ForMajor(21), out JavaVersionRange narrowed));
        AssertEqual(JavaVersionRange.ForMajor(21), narrowed);

        JavaVersionRange legacy = new(new Version(1, 8), new Version(1, 8, 0, 512));
        AssertTrue(legacy.TryIntersect(JavaVersionRange.ForMajor(8), out JavaVersionRange eight));
        AssertEqual(new Version(1, 8), eight.Minimum);
        AssertEqual(new Version(1, 8, 0, 512), eight.Maximum);
    }

    internal static async ValueTask ModernJvmTokensAllResolve()
    {
        string directory = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(directory, "instance");
            Directory.CreateDirectory(instance);
            string natives = Path.Combine(instance, "natives");
            Directory.CreateDirectory(natives);
            string clientJar = Path.Combine(instance, "1.20.1.jar");
            File.WriteAllBytes(clientJar, [0xCA, 0xFE]);

            JsonObject manifest = new()
            {
                ["mainClass"] = "net.minecraft.client.main.Main",
                ["arguments"] = new JsonObject
                {
                    ["jvm"] = new JsonArray(
                        "-Djava.library.path=${natives_directory}",
                        "-Djna.nosys=true",
                        "${classpath_separator}"),
                    ["game"] = new JsonArray(
                        "--username ${auth_player_name}",
                        "--gameDir ${game_directory}",
                        "--assetsDir ${assets_root}",
                        "--assetIndex ${assets_index_name}",
                        "--uuid ${auth_uuid}",
                        "--accessToken ${auth_access_token}",
                        "--userType ${user_type}",
                        "--versionType ${version_type}",
                        "--width ${resolution_width}",
                        "--height ${resolution_height}"),
                },
            };
            MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
            {
                VersionJson = manifest,
                VersionId = "1.20.1",
                InstanceDirectory = instance,
                MinecraftRootDirectory = directory,
                PlayerName = "Steve",
                PlayerUuid = "uuid-1",
                NativesDirectory = natives,
                IsolatedGameDirectory = true,
            });

            foreach (string argument in plan.Arguments)
            {
                AssertFalse(argument.Contains("${", StringComparison.Ordinal));
                AssertFalse(argument.Contains("PCL-UNRESOLVED-TOKEN", StringComparison.Ordinal));
            }

            AssertTrue(plan.Arguments.Contains("-Djava.library.path=" + natives));
            AssertTrue(plan.Arguments.Contains("--gameDir " + instance, StringComparer.Ordinal));

            // The derived client JAR is automatically the classpath head.
            int cpIndex = plan.Arguments.ToList().IndexOf("-cp");
            AssertTrue(plan.Arguments[cpIndex + 1].StartsWith(clientJar, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    internal static async ValueTask UnresolvedLaunchTokenFailsPlanning()
    {
        string directory = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(directory, "instance");
            Directory.CreateDirectory(instance);
            File.WriteAllBytes(Path.Combine(instance, "unknown-1.0.jar"), [0x01]);
            JsonObject manifest = new()
            {
                ["mainClass"] = "net.minecraft.client.main.Main",
                ["arguments"] = new JsonObject
                {
                    ["game"] = new JsonArray("--futureToken ${some_future_token}"),
                },
            };

            bool failed = false;
            try
            {
                _ = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
                {
                    VersionJson = manifest,
                    VersionId = "unknown-1.0",
                    InstanceDirectory = instance,
                    MinecraftRootDirectory = directory,
                    PlayerName = "Steve",
                    PlayerUuid = "uuid-1",
                    ClientJarPath = Path.Combine(instance, "unknown-1.0.jar"),
                });
            }
            catch (InvalidDataException failure)
            {
                failed = failure.Message.Contains("some_future_token", StringComparison.Ordinal);
            }

            AssertTrue(failed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    internal static async ValueTask VanillaLaunchContainsClientJar()
    {
        string directory = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(directory, "instance");
            Directory.CreateDirectory(instance);
            byte[] clientJar = [0x50, 0x4B, 0x03, 0x04];
            string clientJarPath = Path.Combine(instance, "1.20.1.jar");
            File.WriteAllBytes(clientJarPath, clientJar);

            JsonObject manifest = new()
            {
                ["mainClass"] = "net.minecraft.client.main.Main",
                ["arguments"] = new JsonObject { ["game"] = new JsonArray("--username ${auth_player_name}") },
                ["libraries"] = new JsonArray(),
            };
            MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
            {
                VersionJson = manifest,
                VersionId = "1.20.1",
                InstanceDirectory = instance,
                MinecraftRootDirectory = directory,
                PlayerName = "Steve",
                PlayerUuid = "uuid-1",
            });

            int cpIndex = plan.Arguments.ToList().IndexOf("-cp");
            AssertTrue(cpIndex >= 0);
            string classpath = plan.Arguments[cpIndex + 1];
            AssertTrue(classpath.Split(Path.PathSeparator).First(static entry => entry.EndsWith("1.20.1.jar", StringComparison.Ordinal))
                == clientJarPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    internal static async ValueTask InheritedLaunchUsesBaseClientJarWhenProvided()
    {
        string directory = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(directory, "instance");
            Directory.CreateDirectory(instance);
            string baseJar = Path.Combine(directory, "versions", "1.20.1", "1.20.1.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(baseJar)!);
            File.WriteAllBytes(baseJar, [0x01]);

            JsonObject inherited = new()
            {
                ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
                ["inheritsFrom"] = "1.20.1",
                ["arguments"] = new JsonObject { ["game"] = new JsonArray("--username ${auth_player_name}") },
                ["libraries"] = new JsonArray(),
            };
            MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
            {
                VersionJson = inherited,
                VersionId = "fabric-1.20.1",
                InstanceDirectory = instance,
                MinecraftRootDirectory = directory,
                PlayerName = "Steve",
                PlayerUuid = "uuid-1",
                ClientJarPath = baseJar,
            });

            int cpIndex = plan.Arguments.ToList().IndexOf("-cp");
            AssertTrue(plan.Arguments[cpIndex + 1].StartsWith(baseJar, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    internal static async ValueTask Arm64NativeIsNotOnClasspath()
    {
        string directory = CreateTempDirectory();
        try
        {
            string minecraftRoot = Path.Combine(directory, "root");
            const string lwjgl3 = "org.lwjgl:lwjgl:3.3.2";
            JsonObject manifest = new()
            {
                ["mainClass"] = "net.minecraft.client.main.Main",
                ["libraries"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = lwjgl3,
                        ["natives"] = new JsonObject { ["linux"] = "natives-linux" },
                        ["downloads"] = new JsonObject
                        {
                            ["artifact"] = new JsonObject { ["path"] = "org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2.jar", ["sha1"] = "aa", ["size"] = 2 },
                            ["classifiers"] = new JsonObject
                            {
                                ["natives-linux"] = new JsonObject { ["path"] = "org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2-natives-linux.jar", ["sha1"] = "bb", ["size"] = 3 },
                            },
                        },
                    }),
            };

            IReadOnlyList<MinecraftLibraryToken> libraries = MinecraftLibraryResolver.Resolve(new MinecraftLibraryResolutionRequest
            {
                VersionJson = manifest,
                MinecraftRootDirectory = minecraftRoot,
                OperatingSystem = MinecraftLibraryOperatingSystem.Linux,
                Is64BitArchitecture = true,
                IsArm64Architecture = true,
            });

            MinecraftLibraryToken? arm64 = libraries.FirstOrDefault(static token => token.OriginalName is not null && token.OriginalName.Contains("natives-linux-arm64", StringComparison.Ordinal));
            AssertTrue(arm64 is not null);
            MinecraftLibraryToken arm64Token = arm64!;
            AssertTrue(arm64Token.IsNatives);

            // Classpath planner excludes natives.
            MinecraftClasspathPlan classpath = MinecraftClasspathPlanner.CreatePlan(new MinecraftClasspathPlanRequest
            {
                Libraries = libraries,
                HasCleanroom = false,
            });
            AssertFalse(classpath.Entries.Any(static entry => entry.Contains("natives", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    internal static async ValueTask NativesAreExtractedBeforeLaunch()
    {
        string directory = CreateTempDirectory();
        try
        {
            string jarPath = Path.Combine(directory, "natives.jar");
            using (FileStream stream = File.Create(jarPath))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry dll = archive.CreateEntry("lwjgl.dll");
                using (var writer = dll.Open()) writer.Write([0x01, 0x02]);
                ZipArchiveEntry so = archive.CreateEntry("liblwjgl.so");
                using (var writer = so.Open()) writer.Write([0x03]);
                ZipArchiveEntry meta = archive.CreateEntry("META-INF/MANIFEST.MF");
                using (var writer = meta.Open()) writer.Write([0xFF, 0xFF]);
                ZipArchiveEntry evil = archive.CreateEntry("../../evil.bin");
                using (var writer = evil.Open()) writer.Write([0xEE]);
            }

            string natives = Path.Combine(directory, "natives");

            // The traversal entry is a hard failure before anything else is staged.
            bool traversalRefused = false;
            try
            {
                await MinecraftNativesExtractor.ExtractAsync([jarPath], natives);
            }
            catch (InvalidDataException failure)
            {
                traversalRefused = failure.Message.Contains("evil.bin", StringComparison.Ordinal);
            }

            AssertTrue(traversalRefused);
            AssertFalse(File.Exists(Path.Combine(directory, "evil.bin")));

            // A clean archive extracts everything except META-INF.
            string cleanJar = Path.Combine(directory, "clean.jar");
            using (FileStream stream = File.Create(cleanJar))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry dll = archive.CreateEntry("lwjgl.dll");
                using (var writer = dll.Open()) writer.Write([0x01, 0x02]);
                ZipArchiveEntry so = archive.CreateEntry("liblwjgl.so");
                using (var writer = so.Open()) writer.Write([0x03]);
                ZipArchiveEntry meta = archive.CreateEntry("META-INF/MANIFEST.MF");
                using (var writer = meta.Open()) writer.Write([0xFF, 0xFF]);
            }

            await MinecraftNativesExtractor.ExtractAsync([cleanJar], natives);
            AssertTrue(File.Exists(Path.Combine(natives, "lwjgl.dll")));
            AssertTrue(File.Exists(Path.Combine(natives, "liblwjgl.so")));
            AssertFalse(Directory.EnumerateFileSystemEntries(natives, "*", SearchOption.AllDirectories)
                .Any(path => path.Replace('\\', '/').Contains("META-INF", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    internal static void MojangRuleOrderMatchesManifestSemantics()
    {
        // Ordered rules: the last matching rule decides; no match excludes the value.
        JsonObject manifest = new()
        {
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray(
                    new JsonObject
                    {
                        ["rules"] = new JsonArray(
                            new JsonObject { ["action"] = "allow", ["os"] = new JsonObject { ["name"] = "windows" } },
                            new JsonObject { ["action"] = "disallow", ["os"] = new JsonObject { ["name"] = "windows" } }),
                        ["value"] = "--ordered-disallow",
                    },
                    new JsonObject
                    {
                        ["rules"] = new JsonArray(
                            new JsonObject { ["action"] = "disallow", ["os"] = new JsonObject { ["name"] = "linux" } },
                            new JsonObject { ["action"] = "allow", ["os"] = new JsonObject { ["name"] = "windows" } }),
                        ["value"] = "--ordered-allow",
                    },
                    new JsonObject
                    {
                        ["rules"] = new JsonArray(new JsonObject { ["action"] = "allow", ["os"] = new JsonObject { ["name"] = "linux" } }),
                        ["value"] = "--linux-only",
                    }),
            },
        };

        MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
        {
            VersionJson = manifest,
            VersionId = "rules-1",
            InstanceDirectory = Path.Combine(CreateTempDirectory(), "instance"),
            MinecraftRootDirectory = Path.GetTempPath(),
            PlayerName = "Steve",
            PlayerUuid = "uuid",
            OperatingSystem = MinecraftLibraryOperatingSystem.Win32,
            OperatingSystemVersion = "10.0",
            ClientJarPath = Path.GetTempFileName(),
        });

        // Ordered: allow windows then disallow windows → the last match excludes the value.
        AssertFalse(plan.Arguments.Contains("--ordered-disallow", StringComparer.Ordinal));
        // allow-then-allow keeps it, and the non-matching linux-only never appears.
        AssertTrue(plan.Arguments.Contains("--ordered-allow", StringComparer.Ordinal));
        AssertFalse(plan.Arguments.Contains("--linux-only", StringComparer.Ordinal));
    }

    internal static void OsVersionUsesRegex()
    {
        JsonObject manifest = new()
        {
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray(
                    new JsonObject
                    {
                        ["rules"] = new JsonArray(
                            new JsonObject { ["action"] = "allow", ["os"] = new JsonObject { ["name"] = "windows", ["version"] = "^10\\." } }),
                        ["value"] = "--win10",
                    },
                    new JsonObject
                    {
                        ["rules"] = new JsonArray(
                            new JsonObject { ["action"] = "allow", ["os"] = new JsonObject { ["name"] = "windows", ["version"] = "^11\\." } }),
                        ["value"] = "--win11",
                    }),
            },
        };

        MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
        {
            VersionJson = manifest,
            VersionId = "rules-2",
            InstanceDirectory = Path.Combine(CreateTempDirectory(), "instance"),
            MinecraftRootDirectory = Path.GetTempPath(),
            PlayerName = "Steve",
            PlayerUuid = "uuid",
            OperatingSystem = MinecraftLibraryOperatingSystem.Win32,
            OperatingSystemVersion = "10.0.22631",
            ClientJarPath = Path.GetTempFileName(),
        });

        AssertTrue(plan.Arguments.Contains("--win10", StringComparer.Ordinal));
        AssertFalse(plan.Arguments.Contains("--win11", StringComparer.Ordinal));
    }

    internal static void AbsentFalseFeatureMatches()
    {
        JsonObject manifest = new()
        {
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray(
                    new JsonObject
                    {
                        ["rules"] = new JsonArray(
                            new JsonObject
                            {
                                ["action"] = "allow",
                                ["features"] = new JsonObject { ["has_custom_resolution"] = false },
                            }),
                        ["value"] = "--no-custom-resolution",
                    }),
            },
        };

        MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
        {
            VersionJson = manifest,
            VersionId = "rules-3",
            InstanceDirectory = Path.Combine(CreateTempDirectory(), "instance"),
            MinecraftRootDirectory = Path.GetTempPath(),
            PlayerName = "Steve",
            PlayerUuid = "uuid",
            ClientJarPath = Path.GetTempFileName(),
        });

        AssertTrue(plan.Arguments.Contains("--no-custom-resolution", StringComparer.Ordinal));
    }
}
