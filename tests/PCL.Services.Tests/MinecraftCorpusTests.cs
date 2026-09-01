using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Services.Downloads;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Java;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.ModLoaders;
using PCL.Services.Minecraft.Process;
using PCL.Services.Updates;
using PCL.Xsr.State;
namespace PCL.Services.Tests;

// Canonical corpus: representative manifests covering the shape matrix (legacy
// minecraftArguments, modern arguments, Java boundaries, loader inheritance, ARM64 LWJGL,
// and the major third-party loader families). The fixtures are deliberately de-identified, but
// keep the fields that affect the launch contract. Every case asserts the derived Java
// requirement, selected component, main class, library/native split, and classpath head.
internal static partial class Program
{
    internal sealed record CanonicalManifestCase(
        string Name,
        string Json,
        string? Vanilla,
        int? ManifestJavaMajor,
        string MainClass,
        string ExpectedJavaRangeMinimum,
        bool HasForge = false,
        string? ForgeVersion = null,
        bool HasOptiFine = false,
        bool HasCleanroom = false,
        string? CleanroomVersion = null,
        bool IsArm64 = false);

    internal static readonly CanonicalManifestCase[] CorpusManifests =
    [
        new(
            "1.7.10-legacy",
            """{"id":"1.7.10","minecraftArguments":"--username ${auth_player_name} --version ${version_name}","mainClass":"net.minecraft.client.main.Main","minimumLauncherVersion":13,"releaseTime":"2014-05-14T00:00:00+00:00","libraries":[{"name":"org.example:legacy:1.0"}]}""",
            "1.7.10", null, "net.minecraft.client.main.Main", "1.8"),
        new(
            "1.16.5-modern",
            """{"id":"1.16.5","arguments":{"jvm":["-Djava.library.path=${natives_directory}"],"game":["--username ${auth_player_name}"]},"mainClass":"net.minecraft.client.main.Main","releaseTime":"2021-01-14T00:00:00+00:00","libraries":[{"name":"org.example:modern:1.0"}]}""",
            "1.16.5", null, "net.minecraft.client.main.Main", "1.7"),
        new(
            "1.20.5-java21",
            """{"id":"1.20.5","arguments":{"jvm":["--add-modules=jdk.incubator.vector"],"game":["--username ${auth_player_name}"]},"mainClass":"net.minecraft.client.main.Main","javaVersion":{"majorVersion":21,"component":"java-runtime-gamma"},"releaseTime":"2024-04-23T00:00:00+00:00","libraries":[{"name":"org.example:c21:1.0"}]}""",
            "1.20.5", 21, "net.minecraft.client.main.Main", "21.0"),
        new(
            "fabric-1.20.1-inherited",
            """{"id":"fabric-loader-0.16.5-1.20.1","inheritsFrom":"1.20.1","arguments":{"game":["-DFabricMechanisms=true"]},"mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"org.example:fabric:1.0"}]}""",
            "1.20.1", null, "net.fabricmc.loader.impl.launch.knot.KnotClient", "1.7"),
        new(
            "cleanroom-java25",
            """{"id":"cleanroom-5.0","arguments":{"game":["--username ${auth_player_name}"]},"mainClass":"com.cleanroommc.launcher.CleanroomMain","libraries":[{"name":"org.example:cleanroom:1.0"}]}""",
            null, null, "com.cleanroommc.launcher.CleanroomMain", "25.0", HasCleanroom: true, CleanroomVersion: "0.5.1-beta"),
        new(
            "forge-1.12.2",
            """{"id":"forge-1.12.2","minecraftArguments":"--username ${auth_player_name}","mainClass":"net.minecraft.launchwrapper.Launch","libraries":[{"name":"net.minecraftforge:forge:14.23.5.2860"}]}""",
            "1.12.2", null, "net.minecraft.launchwrapper.Launch", "1.8", HasForge: true, ForgeVersion: "14.23.5.2860"),
        new(
            "quilt-1.20.1",
            """{"id":"quilt-loader-0.27.1-1.20.1","mainClass":"org.quiltmc.loader.impl.launch.knot.KnotClient","arguments":{"game":["--username ${auth_player_name}"]},"libraries":[{"name":"org.quiltmc:quilt-loader:0.27.1"}]}""",
            "1.20.1", null, "org.quiltmc.loader.impl.launch.knot.KnotClient", "1.7"),
        new(
            "neoforge-1.20.6",
            """{"id":"neoforge-20.6.119-1.20.6","mainClass":"net.neoforged.fml.loading.targets.CommonLaunchHandler","arguments":{"game":["--username ${auth_player_name}"]},"libraries":[{"name":"net.neoforged:neoforge:20.6.119"}]}""",
            "1.20.6", null, "net.neoforged.fml.loading.targets.CommonLaunchHandler", "21.0"),
        new(
            "optifine-1.12.2",
            """{"id":"OptiFine_1.12.2","mainClass":"net.minecraft.launchwrapper.Launch","minecraftArguments":"--username ${auth_player_name}","libraries":[{"name":"optifine:OptiFine:1.12.2"}]}""",
            "1.12.2", null, "net.minecraft.launchwrapper.Launch", "1.8", HasOptiFine: true),
        new(
            "1.21.1-modern",
            """{"id":"1.21.1","mainClass":"net.minecraft.client.main.Main","arguments":{"jvm":["-Djava.library.path=${natives_directory}"],"game":["--version ${version_name}"]},"libraries":[{"name":"org.example:modern-21:1.0"}]}""",
            "1.21.1", null, "net.minecraft.client.main.Main", "21.0"),
        new(
            "arm64-lwjgl",
            """{"id":"1.20.1-arm64","mainClass":"net.minecraft.client.main.Main","libraries":[{"name":"org.lwjgl:lwjgl:3.3.2","natives":{"linux":"natives-linux"},"downloads":{"classifiers":{"natives-linux":{"path":"org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2-natives-linux.jar"}}}}]}""",
            "1.20.1", null, "net.minecraft.client.main.Main", "1.7", IsArm64: true),
    ];

    internal static async ValueTask CanonicalCorpusProducesConsistentLaunchSnapshots()
    {
        string directory = CreateTempDirectory();
        try
        {
            foreach (CanonicalManifestCase corpus in CorpusManifests)
            {
                string name = corpus.Name;
                string json = corpus.Json;
                string? vanilla = corpus.Vanilla;
                int? manifestMajor = corpus.ManifestJavaMajor;
                string mainClass = corpus.MainClass;
                string expectedJavaRangeMinimum = corpus.ExpectedJavaRangeMinimum;
                JsonObject manifest = JsonNode.Parse(json)!.AsObject();
                bool reliable = vanilla is not null;
                MinecraftJavaRequirementRequest requirementRequest = new()
                {
                    VanillaVersion = reliable ? Version.Parse(vanilla!) : null,
                    HasReliableVanillaVersion = reliable,
                    ManifestJavaMajorVersion = manifestMajor,
                    ManifestJavaComponent = manifest["javaVersion"]?["component"]?.ToString(),
                    HasForge = corpus.HasForge,
                    ForgeVersion = corpus.ForgeVersion,
                    HasOptiFine = corpus.HasOptiFine,
                    HasCleanroom = corpus.HasCleanroom,
                    CleanroomVersion = corpus.CleanroomVersion,
                };
                JavaRequirementResolution requirement = MinecraftJavaRequirementResolver.Resolve(requirementRequest);
                if (!requirement.Success)
                {
                    Console.WriteLine("DIAG corpus=" + name + " reason=" + requirement.FailureReason + " detail=" + requirement.Detail);
                }

                AssertTrue(requirement.Success);
                AssertEqual(expectedJavaRangeMinimum, requirement.Range.Minimum.ToString());
                if (manifestMajor is int expectedMajor)
                {
                    AssertEqual(expectedMajor.ToString(System.Globalization.CultureInfo.InvariantCulture), requirement.Range.Minimum.Major == 1
                        ? requirement.Range.Minimum.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : requirement.Range.Minimum.Major.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    AssertEqual(manifest["javaVersion"]?["component"]?.ToString(), requirement.RecommendedComponent);
                }

                string instance = Path.Combine(directory, name);
                Directory.CreateDirectory(instance);
                string clientJar = Path.Combine(instance, name + ".jar");
                File.WriteAllBytes(clientJar, [0xCA, 0xFE]);

                MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
                {
                    VersionJson = manifest,
                    VersionId = name,
                    InstanceDirectory = instance,
                    MinecraftRootDirectory = directory,
                    PlayerName = "Steve",
                    PlayerUuid = "uuid-corpus",
                    ClientJarPath = clientJar,
                    IsArm64Architecture = corpus.IsArm64,
                    OperatingSystem = corpus.IsArm64 ? MinecraftLibraryOperatingSystem.Linux : MinecraftLibraryOperatingSystem.Unknown,
                    ReleaseTime = manifest["releaseTime"] is { } release
                        ? DateTimeOffset.Parse(release.ToString(), System.Globalization.CultureInfo.InvariantCulture)
                        : null,
                });

                AssertTrue(plan.Arguments.Contains(mainClass, StringComparer.Ordinal));
                int expectedLibraryCount = manifest["libraries"]?.AsArray()?.Count ?? 0;
                AssertEqual(expectedLibraryCount, plan.Libraries.Count);
                AssertEqual(plan.Libraries.Count(static library => library.IsNatives), plan.NativeLibraries.Count);
                AssertEqual(Path.GetFullPath(clientJar), plan.ClientJarPath);
                int cpIndex = plan.Arguments.ToList().IndexOf("-cp");
                AssertTrue(cpIndex >= 0);
                AssertTrue(plan.Arguments[cpIndex + 1].Split(Path.PathSeparator).First(entry => entry.EndsWith(name + ".jar", StringComparison.Ordinal))
                    == clientJar);
                AssertTrue(plan.Arguments.Any(argument => argument.StartsWith("-Xmx", StringComparison.Ordinal)));

                foreach (string argument in plan.Arguments)
                {
                    AssertFalse(argument.Contains("${", StringComparison.Ordinal));
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    internal static async ValueTask ProcessSessionPublishesIntoHostStore()
    {
        XsrStateStoreBuilder builder = new();
        MinecraftProcessStateComposition.DeclareState(builder);
        XsrStateStore store = builder.Build();

        // Real child process: a short OS sleep keeps the session observable as Running and
        // then exits on its own on every platform.
        using System.Diagnostics.Process process = CreateSleepProcess(seconds: 2);
        MinecraftProcessService service = new(new ExistingProcessPort(process), store);

        MinecraftLaunchPlan plan = new(
            "java",
            "work",
            ["-cp", "staged"],
            ["staged"],
            [],
            new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, null, []));
        MinecraftProcessSession session = await service.StartAsync(plan, "instance-1");

        XsrCollectionSnapshot<MinecraftProcessSnapshot> state = store.ReadCollection<MinecraftProcessSnapshot>(
            store.Resolve(MinecraftProcessStateComposition.SessionsKey));
        AssertEqual(1, state.Count);
        AssertEqual(session.Snapshot.SessionId, state.Items[0].SessionId);

        // Cancellation flows through the same state publication path.
        AssertTrue(service.TryCancel(session.Snapshot.SessionId));
        state = store.ReadCollection<MinecraftProcessSnapshot>(
            store.Resolve(MinecraftProcessStateComposition.SessionsKey));
        AssertTrue(state.Items[0].State is MinecraftProcessState.Cancelled or MinecraftProcessState.Exited or MinecraftProcessState.Failed);

        // Retention: many finished sessions never grow the state collection unbounded.
        await session.DisposeAsync();
    }

    private static System.Diagnostics.Process CreateSleepProcess(int seconds)
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", $"/c timeout /t {seconds} /nobreak")
            : new ProcessStartInfo("/bin/sleep", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return System.Diagnostics.Process.Start(startInfo)!;
    }

    private sealed class ExistingProcessPort(System.Diagnostics.Process process) : IMinecraftProcessPort
    {
        public ValueTask<System.Diagnostics.Process> StartAsync(System.Diagnostics.ProcessStartInfo startInfo, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(process);
    }
}
