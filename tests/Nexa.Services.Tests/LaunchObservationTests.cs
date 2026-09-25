using Nexa.Services.Capabilities;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Telemetry;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void RunSamplingRetainsWholeRunAndPrivateSettings()
    {
        RunResourceHistogram histogram = new();
        for (int i = 0; i < 10000; i++) histogram.Add(4096L * 1048576);
        for (int i = 0; i < 4096; i++) histogram.Add(256L * 1048576);
        AssertEqual(4096L * 1048576, histogram.P95());
        var settings = JvmRunSettings.Parse("renderDistance:16\nsimulationDistance:12\nmaxFps:120\nfullscreen:true\nlastServer:private-address\nenableVsync:false");
        AssertEqual(16, settings.RenderDistance); AssertEqual(0, settings.Vsync);
        AssertEqual(-1, JvmRunSettings.Parse("renderDistance:private\nmaxFps:9999999").MaxFps);
        JvmRunWindow window = new(); window.Add(1048576, 2097152, null, 3); window.Add(3145728, 4194304, 0, 5);
        var sample = window.Finish(Guid.NewGuid(), 0, 30000, 30000, 0, settings, 21, "Fabric", 42, 2048, false, null);
        AssertEqual(2d, sample.WorkingMeanMiB); AssertEqual(0d, sample.CpuMeanPercent);
        var next = window.Finish(sample.SessionId, 1, 30001, 1, 1, new(), 21, "Fabric", 42, 2048, true, 0);
        AssertEqual(-1d, next.WorkingMeanMiB); AssertEqual(0L, next.SampleCount);
        var props = new Dictionary<string, string>(RunDiagnosticTelemetry.Sample(sample))
        { ["version"] = "2.0.0.alpha.5", ["os"] = "windows", ["arch"] = "x64", ["result"] = "ok" };
        AssertTrue(CloudflareTelemetryTransport.IsAllowed(new("diagnostic.run", DateTimeOffset.UtcNow, props)));
        props["path"] = "private";
        AssertFalse(CloudflareTelemetryTransport.IsAllowed(new("diagnostic.run", DateTimeOffset.UtcNow, props)));
    }
    private static void ModInventoryUsesMetadataAndMarksPartialEvidence()
    {
        var mods = LaunchModInventoryReader.Parse("""{"id":"example","version":"1.2.3","name":"private-name","depends":{"minecraft":">=1.20","fabricloader":">=0.15"}}""", "fabric.mod.json", true);
        AssertEqual("example", mods.Single().Id); AssertEqual(2, mods.Single().Dependencies.Count);
        AssertTrue(mods.Single().DependenciesComplete);
        var forge = LaunchModInventoryReader.Parse("[[mods]]\nmodId=\"example\"\nversion=\"${file.jarVersion}\"\n[[dependencies.example]]\nmodId=\"minecraft\"", "META-INF/mods.toml", true);
        AssertEqual("unknown", forge.Single().Version); AssertFalse(forge.Single().DependenciesComplete);
        var context = new JvmRunContext(Guid.NewGuid(), "Fabric", "0.16.0", new(mods, 0, true));
        var properties = new Dictionary<string, string>(RunDiagnosticTelemetry.Inventory(context).Single())
        { ["version"] = "2.0.0.alpha.5", ["os"] = "windows", ["arch"] = "x64", ["result"] = "ok" };
        AssertTrue(CloudflareTelemetryTransport.IsAllowed(new("diagnostic.mods", DateTimeOffset.UtcNow, properties)));
        AssertFalse(properties["mods"].Contains("private-name", StringComparison.Ordinal));
    }
    private static async ValueTask PreflightGateRejectsStaleAndBlockedContinuation()
    {
        XsrStateStoreBuilder builder = new(); LaunchPreflightGate.DeclareState(builder); var store = builder.Build();
        var issue = new CapabilityPreflightIssue("JAVA_MISSING", PreflightSeverity.Blocked, "java", PreflightCertainty.Verified, true, ["java.derived.missing"]);
        CapabilityPreflightReport report = new([issue], [issue], PreflightSeverity.Blocked);
        LaunchPreflightGate gate = new(store, (_, _, _, _) => Task.FromResult(report));
        MinecraftLaunchPlan plan = new("java", "root", [], [], [], new(MinecraftModLoaderKind.Vanilla, null, null, []));
        var blocked = gate.CheckAsync("root", "instance", plan, CancellationToken.None);
        var prompt = (LaunchPreflightPrompt)store.ReadAppliedValue(store.Resolve(LaunchPreflightGate.StateKey))!;
        AssertFalse(gate.Decide(new(prompt.Attempt, true))); AssertFalse(gate.Decide(new(Guid.NewGuid(), false)));
        AssertTrue(gate.Decide(new(prompt.Attempt, false))); AssertFalse(await blocked);
        issue = new("MEM_HEAP_LAUNCH_LOW", PreflightSeverity.Critical, "memory", PreflightCertainty.Estimated, false, ["estimate.heap.launch"], canBypass: true);
        report = new([issue], [issue], PreflightSeverity.Critical);
        using var cancel = new CancellationTokenSource(); var cancelled = gate.CheckAsync("root", "instance", plan, cancel.Token);
        cancel.Cancel();
        try { await cancelled; throw new InvalidOperationException("Cancellation did not end preflight."); } catch (OperationCanceledException) { }
        var allowed = gate.CheckAsync("root", "instance", plan, CancellationToken.None);
        var current = (LaunchPreflightPrompt)store.ReadAppliedValue(store.Resolve(LaunchPreflightGate.StateKey))!;
        AssertFalse(gate.Decide(new(prompt.Attempt, true))); AssertTrue(gate.Decide(new(current.Attempt, true)));
        AssertTrue(await allowed);
    }
}
