using Nexa.Services.Capabilities;

namespace Nexa.Services.Tests;

internal static partial class Program
{
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
        AssertEqual("remediation.java.download", RemediationCatalog.For(java)[0].Id);
        AssertEqual(17, RemediationCatalog.All.Count);

        ResourceObservationHistory history = new();
        for (int index = 0; index < 12; index++)
            history.Record(new("fixture", "Vanilla", 21, 0, 0, 1536 + index, 512, 2304, 2560, 256, 1000, now));
        ResourceEstimator calibrated = new(history: history);
        ResourceEstimateSnapshot calibratedResult = calibrated.Estimate(new MachineCapabilitySnapshot(3, now, []));
        AssertTrue(calibratedResult.HeapRuntime.HistoricalWeight > 0);

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
