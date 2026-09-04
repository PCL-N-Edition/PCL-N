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
using PCL.Xsr.State;

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

    internal static void MinecraftJavaGatesUseNormalizedCoordinates()
    {
        JavaRequirementResolution modern = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(1, 20, 5),
        });
        AssertTrue(modern.Success);
        AssertEqual(JavaVersionRange.ForMajor(21), modern.Range);

        JavaRequirementResolution preModern = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(1, 20, 4),
        });
        AssertTrue(preModern.Success);
        AssertEqual(JavaVersionRange.ForMajor(17), preModern.Range);

        JavaRequirementResolution shorthand = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(20, 5),
        });
        AssertTrue(shorthand.Success);
        AssertEqual(JavaVersionRange.ForMajor(21), shorthand.Range);

        JavaRequirementResolution cleanroom25 = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasCleanroom = true,
            CleanroomVersion = "0.5.1-beta",
        });
        AssertTrue(cleanroom25.Success);
        AssertEqual(JavaVersionRange.ForMajor(25), cleanroom25.Range);

        JavaRequirementResolution cleanroomWithLegacyJava8 = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasCleanroom = true,
            CleanroomVersion = "0.5.1-beta",
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(1, 8, 9),
        });
        AssertFalse(cleanroomWithLegacyJava8.Success);
        AssertEqual(JavaRequirementFailureReason.ConflictingRequirements, cleanroomWithLegacyJava8.FailureReason);
    }

    internal static void MinecraftJavaEraMatrixMatchesReleaseLine()
    {
        (string Version, int JavaMajor)[] matrix =
        [
            ("1.7.10", 8),
            ("1.12.2", 8),
            ("1.16.5", 8),
            ("1.17.1", 16),
            ("1.18.2", 17),
            ("1.20.1", 17),
            ("1.20.4", 17),
            ("1.20.5", 21),
            ("1.21.1", 21),
            ("26.1", 25),
            ("26.2", 25),
        ];

        foreach ((string version, int javaMajor) in matrix)
        {
            JavaRequirementResolution resolution = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
            {
                HasReliableVanillaVersion = true,
                VanillaVersion = Version.Parse(version),
            });
            AssertTrue(resolution.Success);
            AssertEqual(JavaVersionRange.ForMajor(javaMajor), resolution.Range);
        }
    }

    internal static void MinecraftCalendarVersionsRetainTheirScheme()
    {
        MinecraftGameVersion calendar = MinecraftGameVersion.FromVersion(new Version(26, 1));
        AssertEqual(MinecraftVersionScheme.Calendar, calendar.Scheme);
        AssertTrue(calendar.IsCalendar);
        AssertEqual(26, calendar.Major);
        AssertEqual(1, calendar.Minor);
        AssertEqual(0, calendar.Patch);
        AssertEqual(new Version(26, 1, 0), calendar.ToVersion());
        AssertEqual(new Version(26, 1, 0), MinecraftJavaRequirementResolver.NormalizeVanilla(new Version(26, 1)));

        MinecraftGameVersion shorthand = MinecraftGameVersion.FromVersion(new Version(20, 5));
        AssertEqual(MinecraftVersionScheme.Legacy, shorthand.Scheme);
        AssertEqual(new MinecraftGameVersion(MinecraftVersionScheme.Legacy, 1, 20, 5), shorthand);
        AssertTrue(calendar > shorthand);
    }

    internal static void ManifestJavaMetadataIsAuthoritative()
    {
        // Calendar 26.1 uses Java 25. The manifest contract must remain exact even when the
        // fallback table is changed or when a caller supplies the legacy Version compatibility input.
        JavaRequirementResolution calendar = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(26, 1),
            ManifestJavaMajorVersion = 25,
            ManifestJavaComponent = "java-runtime-delta",
        });
        AssertTrue(calendar.Success);
        AssertEqual(JavaVersionRange.ForMajor(25), calendar.Range);
        AssertEqual("java-runtime-delta", calendar.RecommendedComponent);

        // A modern 1.20.1 manifest explicitly declaring Java 17 must not inherit an old Any
        // fallback.
        JavaRequirementResolution modern = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(1, 20, 1),
            ManifestJavaMajorVersion = 17,
        });
        AssertTrue(modern.Success);
        AssertEqual(JavaVersionRange.ForMajor(17), modern.Range);

        // Even an intentionally different manifest major wins over inference; only loader
        // constraints are intersected with this authoritative value.
        JavaRequirementResolution overridden = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(1, 20, 1),
            ManifestJavaMajorVersion = 25,
        });
        AssertTrue(overridden.Success);
        AssertEqual(JavaVersionRange.ForMajor(25), overridden.Range);

        JavaRequirementResolution typedResolution = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            MinecraftVersion = MinecraftGameVersion.FromVersion(new Version(26, 1)),
            ManifestJavaMajorVersion = 25,
        });
        AssertTrue(typedResolution.Success);
        AssertEqual(JavaVersionRange.ForMajor(25), typedResolution.Range);
    }

    internal static async ValueTask Minecraft1165NeverSelectsJava7()
    {
        JavaRuntimeCandidate[] candidates =
        [
            Candidate("jdk-7", new Version(1, 7, 0, 321), JavaBrand.EclipseTemurin, false),
            Candidate("jdk-8", new Version(1, 8, 0, 392), JavaBrand.EclipseTemurin, false),
        ];
        JavaSelectionResult result = await new JavaSelectionService(new InMemoryJavaLocator(candidates)).SelectAsync(new MinecraftJavaRequirementRequest
        {
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(1, 16, 5),
        });
        AssertTrue(result.Success);
        AssertEqual(8, result.SelectedJava!.Installation.MajorVersion);
        AssertFalse(result.Requirement.Range.Contains(new Version(1, 7, 0, 321)));
    }

    internal static async ValueTask Minecraft1201NeverSelectsJava8()
    {
        JavaRuntimeCandidate[] candidates =
        [
            Candidate("jdk-8", new Version(1, 8, 0, 392), JavaBrand.EclipseTemurin, false),
            Candidate("jdk-17", new Version(17, 0, 10), JavaBrand.EclipseTemurin, false),
            Candidate("jdk-21", new Version(21, 0, 2), JavaBrand.Microsoft, false),
        ];
        JavaSelectionResult result = await new JavaSelectionService(new InMemoryJavaLocator(candidates)).SelectAsync(new MinecraftJavaRequirementRequest
        {
            HasReliableVanillaVersion = true,
            VanillaVersion = new Version(1, 20, 1),
        });
        AssertTrue(result.Success);
        AssertEqual(17, result.SelectedJava!.Installation.MajorVersion);
        AssertFalse(result.Requirement.Range.Contains(new Version(1, 8, 0, 392)));
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
                        "-Dlauncher=${launcher_name}/${launcher_version}",
                        "-Dlibrary=${library_directory}",
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
            AssertTrue(plan.Arguments.Contains("-Dlauncher=PCL-N/2.0.0"));
            AssertTrue(plan.Arguments.Any(argument => argument.StartsWith("-Dlibrary=", StringComparison.Ordinal)));
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

    internal static async ValueTask UnknownJvmTokenFailsPlanning()
    {
        string directory = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(directory, "instance");
            Directory.CreateDirectory(instance);
            string client = Path.Combine(instance, "unknown-jvm.jar");
            File.WriteAllBytes(client, [0x01]);
            bool failed = false;
            try
            {
                _ = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
                {
                    VersionJson = new JsonObject
                    {
                        ["mainClass"] = "net.minecraft.client.main.Main",
                        ["arguments"] = new JsonObject { ["jvm"] = new JsonArray("-Dfuture=${future_jvm_token}") },
                    },
                    VersionId = "unknown-jvm",
                    InstanceDirectory = instance,
                    MinecraftRootDirectory = directory,
                    PlayerName = "Steve",
                    PlayerUuid = "uuid-1",
                    ClientJarPath = client,
                });
            }
            catch (InvalidDataException failure)
            {
                failed = failure.Message.Contains("future_jvm_token", StringComparison.Ordinal);
            }

            AssertTrue(failed);
        }
        finally { Directory.Delete(directory, recursive: true); }

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

            // Legacy loader manifests use a `jar` alias instead of inheritsFrom. The alias must
            // win even when the loader has its own auxiliary JAR in the instance directory.
            string loaderJar = Path.Combine(instance, "fabric-loader-alias.jar");
            File.WriteAllBytes(loaderJar, [0x02]);
            MinecraftLaunchPlan aliasPlan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
            {
                VersionJson = new JsonObject
                {
                    ["id"] = "fabric-loader-alias",
                    ["jar"] = "1.20.1",
                    ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
                },
                VersionId = "fabric-loader-alias",
                InstanceDirectory = instance,
                MinecraftRootDirectory = directory,
                PlayerName = "Steve",
                PlayerUuid = "uuid-1",
            });
            AssertTrue(aliasPlan.IsInheritedClientJar);
            AssertEqual(Path.GetFullPath(baseJar), aliasPlan.ClientJarPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    internal static async ValueTask InheritedLaunchResolvesBaseClientJarAutomatically()
    {
        string directory = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(directory, "instance");
            string baseDirectory = Path.Combine(directory, "versions", "1.20.1");
            Directory.CreateDirectory(instance);
            Directory.CreateDirectory(baseDirectory);
            string baseJar = Path.Combine(baseDirectory, "1.20.1.jar");
            File.WriteAllBytes(baseJar, [0x01]);

            MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
            {
                VersionJson = new JsonObject
                {
                    ["id"] = "fabric-loader-0.16.5-1.20.1",
                    ["inheritsFrom"] = "1.20.1",
                    ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
                    ["libraries"] = new JsonArray(),
                },
                VersionId = "fabric-loader-0.16.5-1.20.1",
                InstanceDirectory = instance,
                MinecraftRootDirectory = directory,
                PlayerName = "Steve",
                PlayerUuid = "uuid-1",
            });

            AssertTrue(plan.IsInheritedClientJar);
            AssertEqual(Path.GetFullPath(baseJar), plan.ClientJarPath);
            AssertEqual(Path.GetFullPath(baseJar), plan.ClasspathEntries[0]);
        }
        finally { Directory.Delete(directory, recursive: true); }

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
                    },
                    new JsonObject
                    {
                        // Some third-party manifests put the native classifier in the coordinate
                        // instead of using the separate `natives` map.
                        ["name"] = "org.lwjgl:lwjgl:3.2.2:natives-linux",
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
            AssertTrue(libraries.Any(static token => token.OriginalName == "org.lwjgl:lwjgl:3.3.2:natives-linux-arm64" && token.IsNatives));

            MinecraftLibraryToken artifact = libraries.Single(static token => !token.IsNatives && token.LocalPath.EndsWith("lwjgl-3.3.2.jar", StringComparison.Ordinal));

            // Classpath planner excludes natives.
            MinecraftClasspathPlan classpath = MinecraftClasspathPlanner.CreatePlan(new MinecraftClasspathPlanRequest
            {
                Libraries = libraries,
                HasCleanroom = false,
            });
            AssertFalse(classpath.Entries.Any(static entry => entry.Contains("natives", StringComparison.OrdinalIgnoreCase)));
            AssertTrue(classpath.Entries.Contains(artifact.LocalPath, StringComparer.Ordinal));
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

    internal static void MinecraftLibraryRulesUseSharedEvaluator()
    {
        string directory = CreateTempDirectory();
        try
        {
            JsonObject manifest = new()
            {
                ["libraries"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "org.example:ordered:1.0",
                        ["rules"] = new JsonArray(
                            new JsonObject { ["action"] = "disallow", ["os"] = new JsonObject { ["name"] = "windows" } },
                            new JsonObject { ["action"] = "allow", ["os"] = new JsonObject { ["name"] = "windows" } }),
                    },
                    new JsonObject
                    {
                        ["name"] = "org.example:regex:1.0",
                        ["rules"] = new JsonArray(new JsonObject { ["action"] = "allow", ["os"] = new JsonObject { ["name"] = "windows", ["version"] = "^10\\." } }),
                    },
                    new JsonObject
                    {
                        ["name"] = "org.example:feature:1.0",
                        ["rules"] = new JsonArray(new JsonObject { ["action"] = "allow", ["features"] = new JsonObject { ["has_custom_resolution"] = false } }),
                    }),
            };

            IReadOnlyList<MinecraftLibraryToken> resolved = MinecraftLibraryResolver.Resolve(new MinecraftLibraryResolutionRequest
            {
                VersionJson = manifest,
                MinecraftRootDirectory = CreateTempDirectory(),
                OperatingSystem = MinecraftLibraryOperatingSystem.Win32,
                OperatingSystemVersion = "10.0.22631",
            });
            AssertTrue(resolved.Any(token => token.OriginalName == "org.example:ordered:1.0"));
            AssertTrue(resolved.Any(token => token.OriginalName == "org.example:regex:1.0"));
            AssertTrue(resolved.Any(token => token.OriginalName == "org.example:feature:1.0"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static async ValueTask MinecraftLaunchRouteStagesNativesBeforeProcessStart()
    {
        string directory = CreateTempDirectory();
        MinecraftProcessService? processes = null;
        try
        {
            string root = Path.Combine(directory, "root");
            string instance = Path.Combine(directory, "instance");
            string natives = Path.Combine(instance, "natives");
            Directory.CreateDirectory(instance);
            string client = Path.Combine(instance, "1.20.1.jar");
            File.WriteAllBytes(client, [0x01]);

            string nativeJar = Path.Combine(root, "libraries", "org", "example", "native", "1.0", "native-1.0-natives-linux.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(nativeJar)!);
            using (FileStream stream = File.Create(nativeJar))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("libnative.so");
                using Stream output = entry.Open();
                output.Write([0x42]);
            }

            JsonObject manifest = new()
            {
                ["id"] = "1.20.1",
                ["mainClass"] = "net.minecraft.client.main.Main",
                ["arguments"] = new JsonObject
                {
                    ["jvm"] = new JsonArray("-Djava.library.path=${natives_directory}"),
                    ["game"] = new JsonArray("--username", "${auth_player_name}"),
                },
                ["libraries"] = new JsonArray(new JsonObject
                {
                    ["name"] = "org.example:native:1.0",
                    ["natives"] = new JsonObject { ["linux"] = "natives-linux" },
                    ["downloads"] = new JsonObject
                    {
                        ["classifiers"] = new JsonObject
                        {
                            ["natives-linux"] = new JsonObject { ["path"] = "org/example/native/1.0/native-1.0-natives-linux.jar" },
                        },
                    },
                }),
            };

            XsrStateStoreBuilder builder = new();
            MinecraftProcessStateComposition.DeclareState(builder);
            PCL.Services.Logging.LogService.DeclareState(builder);
            XsrStateStore store = builder.Build();
            PCL.Services.Logging.LogService log = new(store);
            processes = new MinecraftProcessService(new ExtractionCheckingProcessPort(Path.Combine(natives, "libnative.so")), store, log);
            MinecraftLaunchExecutor executor = new(processes, log);
            XsrCommandHandler<MinecraftLaunchCommand> launch = MinecraftCommands.CreateLaunchHandler(executor);
            XsrResult result = await launch(new MinecraftLaunchCommand(new MinecraftLaunchRequest
            {
                VersionJson = manifest,
                VersionId = "1.20.1",
                InstanceDirectory = instance,
                MinecraftRootDirectory = root,
                PlayerName = "Steve",
                PlayerUuid = "uuid-1",
                OperatingSystem = MinecraftLibraryOperatingSystem.Linux,
                NativesDirectory = natives,
            }), CancellationToken.None);

            AssertTrue(result.IsSuccess);
            AssertTrue(File.Exists(Path.Combine(natives, "libnative.so")));
            MinecraftProcessSnapshot session = processes.ListSessions().Single();
            XsrCollectionSnapshot<MinecraftProcessSnapshot> state = store.ReadCollection<MinecraftProcessSnapshot>(
                store.Resolve(MinecraftProcessStateComposition.SessionsKey));
            AssertTrue(state.Items.Any(item => item.SessionId == session.SessionId));

            XsrResult cancelled = await MinecraftCommands.CreateCancelProcessHandler(processes)(
                new MinecraftCancelProcessCommand(session.SessionId), CancellationToken.None);
            AssertTrue(cancelled.IsSuccess);
            string diagnostic = DiagnosticText(log);
            int extractionIndex = diagnostic.IndexOf("stage=extract_natives", StringComparison.Ordinal);
            int processIndex = diagnostic.IndexOf("stage=os_start", StringComparison.Ordinal);
            AssertTrue(extractionIndex >= 0 && processIndex > extractionIndex);
            AssertTrue(diagnostic.Contains("state=Created", StringComparison.Ordinal));
            AssertTrue(diagnostic.Contains("state=Running", StringComparison.Ordinal));
            AssertTrue(diagnostic.Contains($"Process cancellation requested session={session.SessionId}", StringComparison.Ordinal));
            AssertTrue(diagnostic.Contains("state=Cancelled", StringComparison.Ordinal));
        }
        finally
        {
            if (processes is not null) await processes.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void MinecraftDownloadPathsRejectManifestTraversal()
    {
        bool unsafeClientRejected = false;
        try
        {
            _ = PCL.Services.Minecraft.Downloads.MinecraftClientDownloadPlanner.CreateClientJarPlan(new PCL.Services.Minecraft.Downloads.MinecraftClientJarDownloadPlanRequest
            {
                VersionJson = new JsonObject
                {
                    ["downloads"] = new JsonObject
                    {
                        ["client"] = new JsonObject { ["url"] = "https://example.invalid/client.jar" },
                    },
                },
                InstanceDirectory = CreateTempDirectory(),
                VersionName = "../../outside",
            });
        }
        catch (InvalidDataException) { unsafeClientRejected = true; }
        AssertTrue(unsafeClientRejected);

        bool unsafeIndexRejected = false;
        try
        {
            _ = PCL.Services.Minecraft.Downloads.MinecraftClientDownloadPlanner.CreateAssetIndexPlan(new PCL.Services.Minecraft.Downloads.MinecraftAssetIndexDownloadPlanRequest
            {
                VersionJson = new JsonObject
                {
                    ["assetIndex"] = new JsonObject
                    {
                        ["id"] = "../../outside",
                        ["url"] = "https://example.invalid/assets.json",
                    },
                },
                MinecraftRootDirectory = CreateTempDirectory(),
            });
        }
        catch (InvalidDataException) { unsafeIndexRejected = true; }
        AssertTrue(unsafeIndexRejected);
    }

    private sealed class ExtractionCheckingProcessPort(string requiredNativePath) : IMinecraftProcessPort
    {
        public ValueTask<System.Diagnostics.Process> StartAsync(System.Diagnostics.ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(requiredNativePath)) throw new InvalidOperationException("Native extraction did not complete before process start.");
            return ValueTask.FromResult(CreateSleepProcess(2));
        }
    }
}
