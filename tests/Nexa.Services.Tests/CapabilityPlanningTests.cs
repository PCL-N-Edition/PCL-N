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
        AssertEqual("1.0.0", estimate.HeapLaunch.ProfileVersion);

        CapabilityDefinition<bool> missing = MachineDerivedRules.JavaDerivedMissing;
        MachineCapabilitySnapshot snapshot = new(2, now,
        [
            missing.Observe(true, now, "fixture"),
            ResourceEstimateCatalog.PhysicalLaunch.Observe(4096, now, "fixture", CapabilityConfidence.Low),
            MachineCapabilityCatalog.PhysicalAvailable.Observe(1024L * 1024 * 1024, now, "fixture"),
        ]);
        CapabilityPreflightReport report = CapabilityPreflightEngine.Evaluate(snapshot);
        AssertEqual(PreflightSeverity.Blocked, report.OverallSeverity);
        AssertTrue(report.Issues.Any(static issue => issue.Code == "MEM_HEAP_LAUNCH_LOW"
            && issue.Severity == PreflightSeverity.Critical && issue.Certainty == PreflightCertainty.Estimated));
        CapabilityPreflightIssue java = report.Issues.Single(static issue => issue.Code == "JAVA_MISSING");
        AssertEqual("remediation.java.download", RemediationCatalog.For(java)[0].Id);
    }

}
