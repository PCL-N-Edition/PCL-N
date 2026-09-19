using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void JvmHostDescribesTheProcessBoundary()
    {
        MinecraftLaunchPlan plan = new("java", "root", ["-Xmx2g", "-cp", "a;b", "example.Main", "--demo"],
            ["a", "b"], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "example.Main", []))
        { NativesDirectory = "native" };
        JvmHostService host = new(new MinecraftProcessService());
        JvmHostEnvironment environment = host.Describe(plan);
        AssertEqual("example.Main", plan.Arguments[3]);
        AssertEqual(3, environment.JvmArguments.Count);
        AssertEqual("--demo", environment.GameArguments.Single());
        AssertEqual("native", environment.NativePath);
        IReadOnlyList<Nexa.Services.Capabilities.ICapability> capabilities = JvmHostService.DescribeCapabilities(plan);
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.process.spawn"));
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.environment.game_args"));
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.process.cpu_sets"));
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.metric.process_tree"));
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.crash.stdout_tail"));
        AssertEqual(37, capabilities.Count);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            capabilities.Single(static item => item.Id == "jvmhost.metric.gpu").Availability);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            capabilities.Single(static item => item.Id == "jvmhost.process.cpu_sets").Availability);

        JvmHostObservation unavailableMetrics = new(Guid.NewGuid(), "fixture", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, 1, 1024, 512, 1, 1, 0, 512, 512, 0, 0, 0, 0, null, null, 0, [], []);
        IReadOnlyList<Nexa.Services.Capabilities.ICapability> observations =
            ObservationCapabilityCatalog.Project(unavailableMetrics, DateTimeOffset.UtcNow);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            observations.Single(static item => item.Id == "observation.launch.heap_peak").Availability);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            observations.Single(static item => item.Id == "observation.runtime.gpu_p95").Availability);

        JvmHostObservation sampledMetrics = unavailableMetrics with
        {
            CpuPeakPercent = 84,
            RuntimePhysicalP95Bytes = 900,
            RuntimeCommitP95Bytes = 700,
            RuntimeCpuP95Percent = 62,
        };
        IReadOnlyList<Nexa.Services.Capabilities.ICapability> sampled =
            ObservationCapabilityCatalog.Project(sampledMetrics, DateTimeOffset.UtcNow);
        AssertEqual(900L, ((Nexa.Services.Capabilities.Capability<long>)sampled
            .Single(static item => item.Id == "observation.runtime.physical_p95")).Value);
        AssertEqual(62L, ((Nexa.Services.Capabilities.Capability<long>)sampled
            .Single(static item => item.Id == "observation.runtime.cpu_p95")).Value);
    }
}
