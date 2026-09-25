using Nexa.Services.Capabilities;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void ResourceEstimatesRemainAdvisoryAndCountHeapOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var floor = new CapabilityDefinition<long>("estimate.heap.hard_minimum", "floor", "fixture", "fixture");
        var report = CapabilityPreflightEngine.Evaluate(new(1, now,
            [floor.Observe(2048, now, "fixture", CapabilityConfidence.High),
             LaunchPolicyCatalog.MinecraftMemory.Observe(512, now, "fixture")]));
        var issue = report.Issues.Single(item => item.Code == "MEM_HEAP_BELOW_HARD_MINIMUM");
        AssertEqual(PreflightCertainty.Estimated, issue.Certainty);
        AssertFalse(issue.HardConstraint);
        AssertTrue(issue.CanBypass);
        AssertTrue(issue.Severity < PreflightSeverity.Blocked);

        var resource = new CapabilityDefinition<long>("resource.derived.steady_memory", "resource", "fixture", "fixture");
        var projection = new ResourceEstimatorProjection();
        Dictionary<string, long> Estimate(long bytes) => projection.Project(new Dictionary<string, ICapability>
        { [resource.Id] = resource.Observe(bytes, now, "fixture") }, now)
            .OfType<Capability<long>>().ToDictionary(item => item.Id, item => item.Value);
        var before = Estimate(0);
        var after = Estimate(1024L * 1024 * 1024);
        foreach (var values in new[] { before, after })
        {
            AssertEqual(values["estimate.heap.runtime"] + values["estimate.native.runtime"]
                + values["estimate.graphics.shared_system"] + values["estimate.physical.system_reserve"],
                values["estimate.physical.runtime"]);
            AssertEqual(values["estimate.native.runtime"], values["estimate.commit.nonheap.runtime"]);
        }
        AssertEqual(after["estimate.heap.runtime"] - before["estimate.heap.runtime"]
            + after["estimate.native.runtime"] - before["estimate.native.runtime"],
            after["estimate.commit.runtime"] - before["estimate.commit.runtime"]);
    }

    private static void ResourceEstimateExcludesDisabledMods()
    {
        var now = DateTimeOffset.UtcNow;
        var projection = new ResourceEstimatorProjection();
        Dictionary<string, ICapability> facts = new()
        {
            [ModCatalog.ModCount.Id] = ModCatalog.ModCount.Observe(200, now, "fixture"),
            [ModCatalog.ModEnabled.Id] = ModCatalog.ModEnabled.Observe(3, now, "fixture"),
        };
        long Read(string id) => ((Capability<long>)projection.Project(facts, now).Single(value => value.Id == id)).Value;
        long enabledEstimate = Read("estimate.heap.mods");
        AssertEqual(3L, Read("estimate.heap.mod_count"));
        facts[ModCatalog.ModCount.Id] = ModCatalog.ModCount.Observe(1000, now, "fixture");
        AssertEqual(enabledEstimate, Read("estimate.heap.mods"));
        facts[ModCatalog.ModEnabled.Id] = ModCatalog.ModEnabled.Observe(0, now, "fixture");
        AssertEqual(0L, Read("estimate.heap.mod_count"));
        AssertEqual(0L, Read("estimate.heap.mods"));
        facts.Remove(ModCatalog.ModEnabled.Id);
        AssertEqual(1000L, Read("estimate.heap.mod_count"));
    }

    private static void InputUsageIsSessionLocalAndExplicit()
    {
        InputUsageTracker tracker = new();
        tracker.Record(InputUsageKind.Touch);
        InputUsageSnapshot snapshot = tracker.Read();
        AssertEqual(InputUsageKind.Touch, snapshot.Primary);
        AssertTrue(snapshot.Touch);
        AssertFalse(snapshot.Controller);
    }

    private static void EstimatorPreflightAndRemediationRemainLayered()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ResourceEstimator estimator = new();
        ResourceEstimateSnapshot estimate = estimator.Estimate(new MachineCapabilitySnapshot(1, now, []));
        AssertEqual(ResourceEstimateStatus.Completed, estimate.Status);
        AssertEqual(CapabilityConfidence.Low, estimate.Confidence);
        AssertEqual("1.1.0", estimate.HeapLaunch.ProfileVersion);
        AssertEqual("resource-1", estimate.HeapLaunch.ModelVersion);

        CapabilityDefinition<long> launchTime = (CapabilityDefinition<long>)ResourceEstimateCatalog.Definitions()
            .Single(static definition => definition.Id == "estimate.history.launch_time.p95");
        CapabilityDefinition<long> sampleCount = (CapabilityDefinition<long>)ResourceEstimateCatalog.Definitions()
            .Single(static definition => definition.Id == "estimate.history.sample_count");
        AssertEqual("ms", launchTime.Unit);
        AssertEqual("count", sampleCount.Unit);
        AssertFalse(ResourceEstimateCatalog.Definitions().Any(static definition => definition.Label == definition.Id));
        AssertFalse(PreflightCatalog.Definitions().Any(static definition => definition.Label == definition.Id));
        AssertTrue(InputCatalog.Definitions().Any(static definition => definition.Id == "input.gyroscope.devices"));
        AssertTrue(InputCatalog.Definitions().Any(static definition => definition.Id == "input.haptics.devices"));
        AssertEqual("file/First.zip", LoaderVersionProjections.DescribeResourcePacks(
            "[\"vanilla\",\"file/First.zip\",\"file/Second.zip\"]")[0]);

        ResourceEstimatorProjection constrainedProjection = new();
        Dictionary<string, ICapability> constrained = new(StringComparer.Ordinal)
        {
            [MachineCapabilityCatalog.PhysicalAvailable.Id] = MachineCapabilityCatalog.PhysicalAvailable.Observe(
                2L * 1024 * 1024 * 1024, now, "fixture"),
        };
        Capability<long> safeMaximum = (Capability<long>)constrainedProjection.Project(constrained, now)
            .Single(static value => value.Id == "estimate.heap.safe_maximum");
        Capability<long> launchHeap = (Capability<long>)constrainedProjection.Project(constrained, now)
            .Single(static value => value.Id == "estimate.heap.launch");
        AssertTrue(safeMaximum.Value < launchHeap.Value);

        CapabilityDefinition<bool> missing = MachineDerivedRules.JavaDerivedMissing;
        MachineCapabilitySnapshot snapshot = new(2, now,
        [
            missing.Observe(true, now, "fixture"),
            ResourceEstimateCatalog.PhysicalLaunch.Observe(4096, now, "fixture", CapabilityConfidence.Low),
            ResourceEstimateCatalog.HeapLaunch.Observe(4096, now, "fixture", CapabilityConfidence.Low),
            ResourceEstimateCatalog.HeapRuntime.Observe(3072, now, "fixture", CapabilityConfidence.Low),
            LaunchPolicyCatalog.MinecraftMemory.Observe(1024, now, "fixture"),
            MachineCapabilityCatalog.PhysicalAvailable.Observe(1024L * 1024 * 1024, now, "fixture"),
        ]);
        CapabilityPreflightReport report = CapabilityPreflightEngine.Evaluate(snapshot);
        AssertEqual(PreflightSeverity.Blocked, report.OverallSeverity);
        AssertTrue(report.Issues.Any(static issue => issue.Code == "MEM_HEAP_LAUNCH_LOW"
            && issue.Severity == PreflightSeverity.Critical && issue.Certainty == PreflightCertainty.Estimated));
        CapabilityPreflightIssue java = report.Issues.Single(static issue => issue.Code == "JAVA_MISSING");
        AssertEqual("缺少可用 Java", java.Title);
        AssertFalse(string.IsNullOrWhiteSpace(java.Description));
        AssertEqual("remediation.java.download", RemediationCatalog.For(java)[0].Id);
        AssertEqual(17, RemediationCatalog.All.Count);

        ResourceObservationHistory history = new();
        string instanceDirectory = Path.Combine(Path.GetTempPath(), "nexa-history-fixture", "versions", "fixture");
        for (int index = 0; index < 12; index++)
            history.Record(new(instanceDirectory, "Vanilla", 21, 0, 0, 1536 + index, 512, 2304, 2560, 256, 1000, now) { SettingsFingerprint = "options-fixture", ModFingerprint = "fixture" });
        ResourceEstimator calibrated = new(history: history);
        ResourceEstimateSnapshot calibratedResult = calibrated.Estimate(new MachineCapabilitySnapshot(3, now, [ModCatalog.ModMetadataFingerprint.Observe("fixture", now, "fixture"), ResourceHistoryCatalog.SettingsFingerprint.Observe("options-fixture", now, "fixture")]),
            new MachineCapabilityQuery(instanceDirectory));
        AssertTrue(calibratedResult.HeapRuntime.HistoricalWeight > 0);
        AssertEqual(0d, calibrated.Estimate(new MachineCapabilitySnapshot(3, now, [])).HeapRuntime.HistoricalWeight);

        ResourceEstimatorProjection projection = new(history: history);
        IReadOnlyList<ICapability> projected = projection.Project(new Dictionary<string, ICapability>(), now);
        AssertTrue(projected.Any(static item => item.Id == "estimate.graphics.total"));
        AssertTrue(projected.Any(static item => item.Id == "estimate.history.heap.p95"));
        AssertTrue(projected.Any(static item => item.Id == "estimate.confidence.score"));

        CapabilityDefinition<bool> unavailablePath = new("instance.path.exists", "path", "fixture", "fixture");
        CapabilityPreflightReport unavailableReport = CapabilityPreflightEngine.Evaluate(new(4, now,
            [unavailablePath.Unavailable(CapabilityAvailability.DependencyMissing, now, "missing")]));
        AssertFalse(unavailableReport.Issues.Any(static issue => issue.Code == "INSTANCE_PATH_UNAVAILABLE"));
    }

    private static async ValueTask RemediationActionsRequireExactHandlers()
    {
        RemediationService empty = new();
        RemediationResult unavailable = await empty.ExecuteAsync(new("remediation.java.select"));
        AssertEqual("handler_unavailable", unavailable.Code);
        RecordingRemediationHandler handler = new("remediation.memory.adjust_heap");
        RemediationService service = new([handler]);
        RemediationResult confirmation = await service.ExecuteAsync(new(handler.Id));
        AssertEqual("confirmation_required", confirmation.Code);
        RemediationResult completed = await service.ExecuteAsync(new(handler.Id, Confirmed: true));
        AssertTrue(completed.Succeeded);
        AssertEqual(1, handler.Calls);
        var (_, settings) = PolicyFixture();
        var scoped = new RemediationService(CoreRemediationHandlers.Create(settings));
        AssertFalse((await scoped.ExecuteAsync(new("remediation.memory.adjust_heap",
            new Dictionary<string, string> { ["memoryMiB"] = "4096" }, true))).Succeeded);
        string instance = Path.Combine(Path.GetTempPath(), "nexa-remediation", "versions", "test");
        string? previousGlobal = Effective(settings, "game.memory").Value.Value;
        AssertTrue((await scoped.ExecuteAsync(new("remediation.memory.adjust_heap",
            new Dictionary<string, string> { ["memoryMiB"] = "4096", ["instanceDirectory"] = instance }, true))).Succeeded);
        AssertEqual(previousGlobal, Effective(settings, "game.memory").Value.Value);
        AssertEqual("4096", Effective(settings, "game.memory", instance).Value.Value);
    }

    private sealed class RecordingRemediationHandler(string id) : IRemediationHandler
    {
        public string Id { get; } = id;
        public int Calls { get; private set; }
        public ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(new RemediationResult(Id, true, "ok", "done"));
        }
    }

}
