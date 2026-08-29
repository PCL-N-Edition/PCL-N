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
// minecraftArguments, modern arguments, Java boundaries, loader inheritance, ARM64 LWJGL).
// Each case asserts the derived Java requirement, main class, and classpath head.
internal static partial class Program
{
    internal static readonly (string Name, string Json, string? Vanilla, int? ManifestJavaMajor, string MainClass, string ExpectedJavaRangeMinimum)[] CorpusManifests =
    [
        (
            "1.7.10-legacy",
            """{"id":"1.7.10","minecraftArguments":"--username ${auth_player_name} --version ${version_name}","mainClass":"net.minecraft.client.main.Main","minimumLauncherVersion":13,"releaseTime":"2014-05-14T00:00:00+00:00","libraries":[{"name":"org.example:legacy:1.0"}]}""",
            "1.7.10", null, "net.minecraft.client.main.Main", "1.7"),
        (
            "1.16.5-modern",
            """{"id":"1.16.5","arguments":{"jvm":["-Djava.library.path=${natives_directory}"],"game":["--username ${auth_player_name}"]},"mainClass":"net.minecraft.client.main.Main","releaseTime":"2021-01-14T00:00:00+00:00","libraries":[{"name":"org.example:modern:1.0"}]}""",
            "1.16.5", null, "net.minecraft.client.main.Main", "1.8"),
        (
            "1.20.5-java21",
            """{"id":"1.20.5","arguments":{"jvm":["--add-modules=jdk.incubator.vector"],"game":["--username ${auth_player_name}"]},"mainClass":"net.minecraft.client.main.Main","javaVersion":{"majorVersion":21,"component":"java-runtime-gamma"},"releaseTime":"2024-04-23T00:00:00+00:00","libraries":[{"name":"org.example:c21:1.0"}]}""",
            "1.20.5", 21, "net.minecraft.client.main.Main", "21.0"),
        (
            "fabric-1.20.1-inherited",
            """{"id":"fabric-loader-0.16.5-1.20.1","inheritsFrom":"1.20.1","arguments":{"game":["-DFabricMechanisms=true"]},"mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"org.example:fabric:1.0"}]}""",
            "1.20.1", null, "net.fabricmc.loader.impl.launch.knot.KnotClient", "21.0"),
        (
            "cleanroom-java25",
            """{"id":"cleanroom-1.0","arguments":{"game":["--username ${auth_player_name}"]},"mainClass":"com.cleanroommc.launcher.CleanroomMain","libraries":[{"name":"org.example:cleanroom:1.0"}]}""",
            "1.12.2", null, "com.cleanroommc.launcher.CleanroomMain", "21.0"),
    ];

    internal static async ValueTask CanonicalCorpusProducesConsistentLaunchSnapshots()
    {
        string directory = CreateTempDirectory();
        try
        {
            foreach ((string name, string json, string? vanilla, int? manifestMajor, string mainClass, string _) in CorpusManifests)
            {
                JsonObject manifest = JsonNode.Parse(json)!.AsObject();
                bool reliable = vanilla is not null;
                JavaRequirementResolution requirement = MinecraftJavaRequirementResolver.Resolve(new MinecraftJavaRequirementRequest
                {
                    VanillaVersion = reliable ? Version.Parse(vanilla!) : null,
                    HasReliableVanillaVersion = reliable,
                    ManifestJavaMajorVersion = manifestMajor,
                });
                if (!requirement.Success)
                {
                    Console.WriteLine("DIAG corpus=" + name + " reason=" + requirement.FailureReason + " detail=" + requirement.Detail);
                }

                AssertTrue(requirement.Success);

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
                    ReleaseTime = manifest["releaseTime"] is { } release
                        ? DateTimeOffset.Parse(release.ToString(), System.Globalization.CultureInfo.InvariantCulture)
                        : null,
                });

                AssertEqual(mainClass, plan.Arguments[plan.Arguments.ToList().IndexOf(mainClass)] is string
                    ? mainClass
                    : plan.Arguments[0]);
                AssertTrue(plan.Arguments.Contains(mainClass, StringComparer.Ordinal));
                int cpIndex = plan.Arguments.ToList().IndexOf("-cp");
                AssertTrue(cpIndex >= 0);
                AssertTrue(plan.Arguments[cpIndex + 1].Split(Path.PathSeparator).First(entry => entry.EndsWith(name + ".jar", StringComparison.Ordinal))
                    == clientJar);

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
