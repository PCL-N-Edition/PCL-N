using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Services.Composition;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Assets;
using PCL.Services.Minecraft.Crash;
using PCL.Services.Minecraft.Downloads;
using PCL.Services.Minecraft.Java;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.ModLoaders;
using PCL.Xsr;
using PCL.Xsr.Runtime;

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
            new MinecraftJavaRequirementRequest { HasReliableVanillaVersion = true, VanillaVersion = new Version(1, 20, 5) });
        if (!result.Success)
        {
            Console.WriteLine("DIAG select reason=" + result.FailureReason + " detail=" + result.Detail);
        }

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
        AssertEqual(1, MinecraftAssetDownloadPlanner.CreatePlan(new MinecraftAssetDownloadPlanRequest { Assets = [asset] }).Files.Count);

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
            ClientJarPath = Path.GetTempFileName(),
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

    internal static async ValueTask MinecraftRuntimeCompositionRegistersRoutes()
    {
        string root = CreateTempDirectory();
        try
        {
            string versionDirectory = Path.Combine(root, "versions", "1.20.1");
            Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(Path.Combine(versionDirectory, "1.20.1.json"), "{\"id\":\"1.20.1\",\"type\":\"release\"}");
            MinecraftRuntime runtime = MinecraftRuntimeComposer.Compose();
            AssertEqual(2, runtime.Commands.Count);
            AssertEqual(3, runtime.Queries.Count);
            AssertTrue(runtime.Queries.TryResolve(MinecraftRouteIds.VersionsRead, out XsrQueryId versionsId));
            XsrResult<IReadOnlyList<MinecraftVersionDescriptor>> versions = await runtime.Queries.QueryAsync<MinecraftVersionsQuery, IReadOnlyList<MinecraftVersionDescriptor>>(versionsId, new MinecraftVersionsQuery(root));
            AssertTrue(versions.IsSuccess);
            AssertEqual(1, versions.Value.Count);
            AssertEqual("1.20.1", versions.Value[0].Id);
            AssertTrue(runtime.Queries.TryResolve(MinecraftRouteIds.InstancesRead, out XsrQueryId instancesId));
            XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>> instances = await runtime.Queries.QueryAsync<MinecraftInstancesQuery, IReadOnlyList<MinecraftInstanceDescriptor>>(instancesId, new MinecraftInstancesQuery(root));
            AssertTrue(instances.IsSuccess);
            AssertEqual(1, instances.Value.Count);
            AssertTrue(runtime.Queries.TryResolve(MinecraftRouteIds.CrashAnalyze, out XsrQueryId crashId));
            XsrResult<MinecraftLaunchFaultReport> report = await runtime.Queries.QueryAsync<MinecraftCrashAnalyzeQuery, MinecraftLaunchFaultReport>(crashId, new MinecraftCrashAnalyzeQuery(["OutOfMemoryError: Java heap space"]));
            AssertTrue(report.IsSuccess);
            AssertEqual(MinecraftLaunchFaultCode.OutOfMemory, report.Value.Code);
            AssertTrue(runtime.Commands.TryResolve(MinecraftRouteIds.ProcessCancel, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async ValueTask MinecraftJavaRuntimePackagePlannerValidatesManifest()
    {
        JavaRuntimePlatform platform = new(JavaRuntimeOperatingSystem.Win32, JavaRuntimeArchitecture.X64);
        AssertEqual("windows-x64", platform.ToMojangKey());
        JavaRuntimePackageDescriptor packageDescriptor = JavaRuntimePackagePlanner.SelectPackage("""
            { "windows-x64": { "java-runtime-gamma": [{ "version": { "name": "21.0.2" }, "manifest": { "url": "https://example.invalid/runtime.json" } }] } }
            """, platform, "java-runtime-gamma");
        AssertEqual("21.0.2", packageDescriptor.VersionName);
        JavaRuntimeDownloadPlan plan = JavaRuntimePackagePlanner.CreateDownloadPlan(packageDescriptor, """
            { "files": { "bin/java": { "executable": true, "downloads": { "raw": { "url": "https://example.invalid/java", "sha1": "0123456789abcdef0123456789abcdef01234567", "size": 1234 } } } } }
            """, Path.Combine(Path.GetTempPath(), "minecraft-runtime"));
        AssertEqual(1, plan.Files.Count);
        AssertTrue(plan.Files[0].Executable);
        AssertTrue(plan.Files[0].TargetPath.EndsWith(Path.Combine("java-runtime-gamma", "bin", "java"), StringComparison.Ordinal));
        bool rejected = false;
        try
        {
            JavaRuntimePackagePlanner.CreateDownloadPlan(packageDescriptor, "{\"files\":{\"../escape\":{\"downloads\":{\"raw\":{\"url\":\"x\",\"sha1\":\"x\",\"size\":1}}}}}", Path.Combine(Path.GetTempPath(), "minecraft-runtime"));
        }
        catch (InvalidOperationException) { rejected = true; }
        AssertTrue(rejected);
        _ = await new JavaRuntimeDownloadPlanService(new FakeJavaRuntimeMetadataProvider()).CreatePlanAsync("java-runtime-gamma", platform, Path.Combine(Path.GetTempPath(), "minecraft-runtime"));
    }

    internal static void MinecraftDownloadPlannersRespectExistingFiles()
    {
        MinecraftAssetToken asset = new() { LocalPath = Path.Combine("root", "assets", "objects", "ab", "abcdef"), SourcePath = "foo", Hash = "abcdef", Size = 42 };
        PCL.Services.Minecraft.Downloads.MinecraftAssetDownloadPlan skipped = MinecraftAssetDownloadPlanner.CreatePlan(new MinecraftAssetDownloadPlanRequest
        {
            Assets = [asset],
            ExistingFiles = new Dictionary<string, MinecraftAssetFileState>(StringComparer.Ordinal) { [asset.LocalPath] = new MinecraftAssetFileState(true, 42) },
        });
        AssertEqual(0, skipped.Files.Count);
        PCL.Services.Minecraft.Downloads.MinecraftAssetDownloadPlan forced = MinecraftAssetDownloadPlanner.CreatePlan(new MinecraftAssetDownloadPlanRequest { Assets = [asset], CheckHash = true });
        AssertEqual(1, forced.Files.Count);
        MinecraftClientJarDownloadPlan client = MinecraftClientDownloadPlanner.CreateClientJarPlan(new MinecraftClientJarDownloadPlanRequest
        {
            VersionJson = JsonNode.Parse("{\"downloads\":{\"client\":{\"url\":\"https://example.invalid/client.jar\",\"size\":2048,\"sha1\":\"abc\"}}}")!.AsObject(),
            InstanceDirectory = Path.Combine(Path.GetTempPath(), "instance"),
            VersionName = "1.20.1",
        });
        AssertEqual(MinecraftClientDownloadFailureReason.None, client.FailureReason);
        AssertEqual(2048L, client.File!.ActualSize);
        AssertTrue(MinecraftDownloadSourcePlanner.GetAssetSources("http://resources.download.minecraft.net/ab/abcdef", true)[0].StartsWith("https://", StringComparison.Ordinal));
    }

    internal static void MinecraftJavaPreferenceParserPreservesLegacySemantics()
    {
        AssertTrue(JavaPreferenceParser.Parse("") is AutoSelectJavaPreference);
        AssertTrue(JavaPreferenceParser.Parse(JavaPreferenceParser.LegacyUseGlobalText) is UseGlobalJavaPreference);
        string absolute = OperatingSystem.IsWindows() ? @"C:\Java\bin\java.exe" : "/opt/java/bin/java";
        AssertEqual(absolute, ((ExistingJavaPreference)JavaPreferenceParser.Parse(absolute)).JavaExecutablePath);
        AssertTrue(JavaPreferenceParser.Parse(@"jre\bin\java.exe") is UseGlobalJavaPreference);
        string escapedAbsolute = absolute.Replace("\\", "\\\\", StringComparison.Ordinal);
        AssertEqual(absolute, ((ExistingJavaPreference)JavaPreferenceParser.Parse("{\"kind\":\"exist\",\"JavaExePath\":\"" + escapedAbsolute + "\"}")).JavaExecutablePath);
        string baseDirectory = CreateTempDirectory();
        try
        {
            AssertTrue(JavaPreferenceParser.Parse("{\"kind\":\"relative\",\"RelativePath\":\"jre/bin/java\"}", baseDirectory) is UseRelativeJavaPreference);
            AssertTrue(JavaPreferenceParser.Parse("{\"kind\":\"relative\",\"RelativePath\":\"../outside/java\"}", baseDirectory) is UseGlobalJavaPreference);
            AssertTrue(JavaPreferenceParser.Parse("{not-json") is UseGlobalJavaPreference);
        }
        finally { Directory.Delete(baseDirectory, recursive: true); }
    }

    internal static void MinecraftLibrariesUseArm64CompatibilityArtifacts()
    {
        string root = Path.Combine(Path.GetTempPath(), "minecraft-arm64-root");
        JsonObject manifest = JsonNode.Parse("""
            {
              "libraries": [
                { "name": "org.lwjgl:lwjgl:3.2.2", "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2.jar", "sha1": "old", "size": 1 } } },
                { "name": "org.lwjgl:lwjgl:3.2.2", "natives": { "linux": "natives-linux" }, "downloads": { "classifiers": { "natives-linux": { "path": "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2-natives-linux.jar", "sha1": "old-native", "size": 2 } } } },
                { "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209", "natives": { "linux": "natives-linux" } }
              ]
            }
            """)!.AsObject();
        IReadOnlyList<MinecraftLibraryToken> libraries = MinecraftLibraryResolver.Resolve(new MinecraftLibraryResolutionRequest
        {
            VersionJson = manifest,
            MinecraftRootDirectory = root,
            OperatingSystem = MinecraftLibraryOperatingSystem.Linux,
            IsArm64Architecture = true,
            Is64BitArchitecture = true,
        });
        AssertTrue(libraries.Any(token => token.OriginalName == "org.lwjgl:lwjgl:3.3.2" && token.Sha1 == "4421d94af68e35dcaa31737a6fc59136a1e61b94"));
        AssertTrue(libraries.Any(token => token.OriginalName == "org.lwjgl:lwjgl:3.3.2:natives-linux-arm64" && token.Sha1 == "8bd89332c90a90e6bc4aa997a25c05b7db02c90a"));
        AssertTrue(libraries.Any(token => token.OriginalName == "org.glavo.hmcl:lwjgl2-natives:2.9.3-linux-arm64"));
    }

    internal static void MinecraftLaunchPlanMergesInheritedAndModernArguments()
    {
        JsonObject inherited = JsonNode.Parse("""
            { "mainClass": "net.minecraft.client.main.Main", "arguments": { "jvm": ["-Dparent=true"], "game": ["--versionType", "${version_type}"] }, "libraries": [{ "name": "org.example:parent:1.0" }] }
            """)!.AsObject();
        JsonObject current = JsonNode.Parse("""
            { "id": "loader-1", "arguments": { "jvm": ["--sun-misc-unsafe-memory-access=allow", "-cp", "${classpath}"], "game": [{ "rules": [{ "action": "allow", "os": { "name": "windows" } }], "value": ["--username", "${auth_player_name}"] }] }, "libraries": [{ "name": "org.example:current:1.0" }] }
            """)!.AsObject();
        MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
        {
            VersionJson = current,
            InheritedVersionJsons = [inherited],
            VersionId = "loader-1",
            InstanceDirectory = Path.Combine(Path.GetTempPath(), "minecraft-launch-instance"),
            MinecraftRootDirectory = Path.Combine(Path.GetTempPath(), "minecraft-launch-root"),
            ClientJarPath = Path.GetTempFileName(),
            PlayerName = "Steve",
            PlayerUuid = "uuid",
            JavaMajorVersion = 23,
            OperatingSystem = MinecraftLibraryOperatingSystem.Win32,
        });
        AssertTrue(plan.Arguments.Contains("-Dparent=true"));
        AssertTrue(plan.Arguments.Contains("--sun-misc-unsafe-memory-access=allow"));
        AssertTrue(plan.Arguments.Contains("Steve"));
        AssertTrue(plan.Arguments.Contains("${version_type}") is false);
        AssertEqual(2, plan.Libraries.Count);
    }

    internal static void MinecraftDownloadSourcePlannerCoversOfficialAndUnlistedMirrors()
    {
        string[] thirdParty = MinecraftDownloadSourcePlanner.GetLibrarySources("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1/forge.jar", true);
        AssertEqual(2, thirdParty.Length);
        AssertTrue(thirdParty.All(source => source.StartsWith("https://bmclapi2.bangbang93.com/", StringComparison.Ordinal)));
        string[] unlisted = MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources("https://zkitefly.github.io/unlisted-versions-of-minecraft/1.0.json", true);
        AssertTrue(unlisted.Any(source => source.StartsWith("https://alist.8mi.tech/", StringComparison.Ordinal)));
    }

    private sealed class SynchronousProgress<T>(Action<T> sink) : IProgress<T>
    {
        public void Report(T value) => sink(value);
    }

    internal static async ValueTask MinecraftJavaRuntimeInstallerVerifiesAndInstalls()
    {
        string root = CreateTempDirectory();
        try
        {
            string relative = OperatingSystem.IsWindows() ? "bin/java.exe" : "bin/java";
            const string sha1 = "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d";
            string platformKey = JavaRuntimeInstaller.DetectPlatform().ToMojangKey();
            FakeInstallerMetadataProvider metadata = new(platformKey, relative, sha1);
            using HttpClient client = new(new StaticHttpMessageHandler("hello"));
            using JavaRuntimeInstaller installer = new(new JavaRuntimeDownloadPlanService(metadata), client);
            List<JavaRuntimeInstallProgress> progress = [];
            var synchronousProgress = new SynchronousProgress<JavaRuntimeInstallProgress>(progress.Add);
            string executable = await installer.InstallAsync("java-runtime-test", root, synchronousProgress);
            AssertTrue(File.Exists(executable));
            AssertTrue(progress.Any(item => item.Progress >= 1d));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FakeJavaRuntimeMetadataProvider : IJavaRuntimeMetadataProvider
    {
        public ValueTask<string> GetRuntimeIndexAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult("{\"windows-x64\":{\"java-runtime-gamma\":[{\"version\":{\"name\":\"21.0.2\"},\"manifest\":{\"url\":\"https://example.invalid/runtime.json\"}}]}}");
        public ValueTask<string> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default) => ValueTask.FromResult("{\"files\":{\"bin/java\":{\"executable\":true,\"downloads\":{\"raw\":{\"url\":\"https://example.invalid/java\",\"sha1\":\"0123456789abcdef0123456789abcdef01234567\",\"size\":1234}}}}}");
    }

    private sealed class FakeInstallerMetadataProvider(string platformKey, string relativePath, string sha1) : IJavaRuntimeMetadataProvider
    {
        public ValueTask<string> GetRuntimeIndexAsync(CancellationToken cancellationToken = default)
        {
            JsonObject root = new();
            JsonArray versions = JsonNode.Parse("[{\"version\":{\"name\":\"21.0.2\"},\"manifest\":{\"url\":\"https://example.invalid/java.json\"}}]")!.AsArray();
            root[platformKey] = new JsonObject
            {
                ["java-runtime-test"] = versions,
            };
            return ValueTask.FromResult(root.ToJsonString());
        }

        public ValueTask<string> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
        {
            JsonObject root = new()
            {
                ["files"] = new JsonObject
                {
                    [relativePath] = new JsonObject
                    {
                        ["executable"] = true,
                        ["downloads"] = new JsonObject
                        {
                            ["raw"] = new JsonObject { ["url"] = "https://example.invalid/java", ["sha1"] = sha1, ["size"] = 5 },
                        },
                    },
                },
            };
            return ValueTask.FromResult(root.ToJsonString());
        }
    }

    private sealed class StaticHttpMessageHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(payload) });
    }

    private static JavaRuntimeCandidate Candidate(string home, Version version, JavaBrand brand, bool isJre) =>
        new(new JavaInstallation(home, Path.Combine(home, "bin", "java"), null, version, brand, JavaArchitecture.X64, true, isJre));

    private sealed class InMemoryJavaLocator(IReadOnlyList<JavaRuntimeCandidate> candidates) : IJavaRuntimeLocator
    {
        public ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(candidates);
    }

}
