using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Services.Minecraft.Assets;
using PCL.Services.Minecraft.Java;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.ModLoaders;
using PCL.Services.Minecraft.Crash;
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

    internal static async ValueTask MinecraftJavaSelectionHonorsManifestAndAvailability()
    {
        JavaRuntimeCandidate[] candidates =
        [
            Candidate("jdk-17", new Version(17, 0, 10), JavaBrand.EclipseTemurin, false),
            Candidate("jdk-21-disabled", new Version(21, 0, 1), JavaBrand.EclipseTemurin, false) with { IsEnabled = false },
            Candidate("jdk-21", new Version(21, 0, 2), JavaBrand.Microsoft, false),
        ];
        JavaSelectionResult result = await new JavaSelectionService(new InMemoryJavaLocator(candidates)).SelectAsync(
            new MinecraftJavaRequirementRequest { HasReliableVanillaVersion = true, VanillaVersion = new Version(20, 0, 5) });
        AssertTrue(result.Success);
        AssertEqual(Path.GetFullPath("jdk-21"), result.SelectedJava!.Installation.JavaHome);

        JavaRequirementResolution future = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
        {
            ReleaseTime = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            ManifestJavaMajorVersion = 25,
            ManifestJavaComponent = "java-runtime-delta",
        });
        AssertTrue(future.Success);
        AssertEqual("java-runtime-delta", future.RecommendedComponent);
        AssertTrue(future.Range.Contains(new Version(25, 0)));
        AssertFalse(future.Range.Contains(new Version(21, 0)));
    }

    internal static void MinecraftAssetsResolveCanonicalObjectPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "minecraft-assets-root");
        JsonObject index = JsonNode.Parse("""
            { "objects": { "minecraft/sounds/menu.ogg": { "hash": "abcdef1234", "size": 42 } } }
            """)!.AsObject();
        MinecraftAssetToken asset = MinecraftAssetListResolver.GetAssetList(new MinecraftAssetListRequest
        {
            IndexJson = index,
            MinecraftRootDirectory = root,
            InstanceDirectory = Path.Combine(root, "versions", "1.20.1"),
        })[0];
        AssertEqual(Path.Combine(root, "assets", "objects", "ab", "abcdef1234"), asset.LocalPath);
        AssertEqual("https://resources.download.minecraft.net/ab/abcdef1234", MinecraftAssetListResolver.GetObjectUrl(asset.Hash));
        AssertEqual(1, MinecraftAssetListResolver.CreateDownloadPlan([asset]).Files.Count);

        bool rejected = false;
        try
        {
            MinecraftAssetListResolver.GetAssetList(new MinecraftAssetListRequest
            {
                IndexJson = JsonNode.Parse("{\"objects\":{\"../escape\":{\"hash\":\"abcdef\",\"size\":1}}}")!.AsObject(),
                MinecraftRootDirectory = root,
                InstanceDirectory = Path.Combine(root, "instance"),
            });
        }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
    }

    internal static void MinecraftLibrariesAndClasspathHonorRules()
    {
        string root = Path.Combine(Path.GetTempPath(), "minecraft-library-root");
        JsonObject manifest = JsonNode.Parse("""
            {
              "libraries": [
                { "name": "org.example:client:1.0", "downloads": { "artifact": { "path": "org/example/client/1.0/client-1.0.jar", "url": "https://repo.example/client.jar", "sha1": "abcdef", "size": 12 } } },
                { "name": "org.example:linux:1.0", "rules": [{ "action": "allow", "os": { "name": "linux" } }] },
                { "name": "org.example:windows:1.0", "rules": [{ "action": "allow", "os": { "name": "windows" } }] },
                { "name": "org.example:native:1.0", "natives": { "linux": "natives-linux" }, "downloads": { "classifiers": { "natives-linux": { "path": "org/example/native/1.0/native-1.0-natives-linux.jar", "url": "https://repo.example/native.jar" } } } }
              ]
            }
            """)!.AsObject();
        IReadOnlyList<MinecraftLibraryToken> libraries = MinecraftLibraryResolver.Resolve(new MinecraftLibraryResolutionRequest
        {
            VersionJson = manifest,
            MinecraftRootDirectory = root,
            OperatingSystem = MinecraftLibraryOperatingSystem.Linux,
            Is64BitArchitecture = true,
        });
        AssertEqual(3, libraries.Count);
        AssertTrue(libraries.Any(token => token.OriginalName == "org.example:linux:1.0"));
        AssertFalse(libraries.Any(token => token.OriginalName == "org.example:windows:1.0"));
        MinecraftLibraryToken native = libraries.Single(token => token.IsNatives);
        AssertTrue(native.LocalPath.EndsWith("native-1.0-natives-linux.jar", StringComparison.Ordinal));
        AssertEqual("org/example/client/1.0/client-1.0.jar", Path.GetRelativePath(Path.Combine(root, "libraries"), libraries[0].LocalPath).Replace('\\', '/'));

        MinecraftClasspathPlan plan = MinecraftClasspathPlanner.CreatePlan(new MinecraftClasspathPlanRequest
        {
            Libraries = [native, new MinecraftLibraryToken { OriginalName = "optifine:OptiFine:HD_U_I7", NameWithoutVersion = "optifine:OptiFine", LocalPath = "optifine.jar" }, new MinecraftLibraryToken { OriginalName = "org.example:client:1.0", LocalPath = "client.jar" }],
            ClasspathHeadEntries = ["head.jar"],
        });
        AssertFalse(plan.Entries.Contains(native.LocalPath));
        AssertTrue(plan.Entries.Contains("head.jar"));
        AssertTrue(plan.Entries.Contains("optifine.jar"));
    }

    internal static void MinecraftModLoaderAndLaunchPlanAreDeterministic()
    {
        JsonObject manifest = JsonNode.Parse("""
            {
              "id": "fabric-loader-0.16.5",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "arguments": { "game": ["--username", "${auth_player_name}", "--gameDir", "${game_directory}", "--assetsDir", "${assets_root}", "--version", "${version_name}"] },
              "libraries": [{ "name": "org.example:client:1.0" }]
            }
            """)!.AsObject();
        MinecraftModLoaderDescriptor loader = MinecraftModLoaderDetector.Detect(manifest);
        AssertEqual(MinecraftModLoaderKind.Fabric, loader.Kind);
        AssertEqual("0.16.5", loader.Version);
        MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
        {
            VersionJson = manifest,
            VersionId = "fabric-loader-0.16.5",
            InstanceDirectory = Path.Combine(Path.GetTempPath(), "minecraft-instance"),
            MinecraftRootDirectory = Path.Combine(Path.GetTempPath(), "minecraft-root"),
            PlayerName = "Steve",
            PlayerUuid = "uuid",
            JavaExecutablePath = "java",
            MemoryMegabytes = 4096,
            JavaMajorVersion = 21,
            OperatingSystem = MinecraftLibraryOperatingSystem.Linux,
        });
        AssertEqual("net.fabricmc.loader.impl.launch.knot.KnotClient", plan.Arguments.First(argument => argument.Contains("KnotClient", StringComparison.Ordinal)));
        AssertTrue(plan.Arguments.Contains("Steve"));
        AssertTrue(plan.Arguments.Contains("-Xmx4096m"));
        AssertTrue(plan.Arguments.Contains("-Dfile.encoding=COMPAT"));
        AssertEqual(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "minecraft-instance")), plan.WorkingDirectory);
        AssertTrue(plan.ToStartInfo().ArgumentList.Contains("-Xmx4096m"));
    }

    internal static void MinecraftCrashAnalysisAndDependencyParsingAreStructured()
    {
        MinecraftLaunchFaultReport java = MinecraftLaunchFaultAnalyzer.Analyze(new DllNotFoundException("Unable to load jvm.dll"), "JvmStarting");
        AssertEqual(MinecraftLaunchFaultCode.JavaRuntimeMissing, java.Code);
        AssertEqual("JVM", java.Subsystem);
        AssertTrue(java.AllowedActions.Contains(MinecraftRepairActionKind.SelectCompatibleJava));
        MinecraftLaunchFaultReport graphics = MinecraftLaunchFaultAnalyzer.AnalyzeText(["GLFW error: failed to create window"], "MinecraftClient", "org.lwjgl.glfw.GLFW");
        AssertEqual(MinecraftLaunchFaultCode.GraphicsInitializationFailed, graphics.Code);
        AssertEqual("Graphics", graphics.Subsystem);

        IReadOnlyList<MinecraftMissingDependency> missing = MinecraftMissingDependencyParser.Parse([
            "Mod sodium requires mod 'Fabric API' (fabric-api) version 0.100.0 or later, which is missing!",
            "Mod example requires cloth-config any version, which is missing!",
            "Mod farmersdelight requires version [1.2,) of bookshelf",
        ]);
        AssertEqual(3, missing.Count);
        AssertEqual("fabric-api", missing[0].ModId);
        AssertEqual("0.100.0", missing[0].RequiredVersion);
        AssertEqual("cloth-config", missing[1].ModId);
        AssertNull(missing[1].RequiredVersion);
        AssertEqual("bookshelf", missing[2].ModId);
    }

    private static JavaRuntimeCandidate Candidate(string home, Version version, JavaBrand brand, bool isJre) =>
        new(new JavaInstallation(home, Path.Combine(home, "bin", "java"), null, version, brand, JavaArchitecture.X64, true, isJre));

    private sealed class InMemoryJavaLocator(IReadOnlyList<JavaRuntimeCandidate> candidates) : IJavaRuntimeLocator
    {
        public ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(candidates);
    }

}
