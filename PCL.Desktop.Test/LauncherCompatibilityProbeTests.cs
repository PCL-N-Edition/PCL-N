// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Diagnostics;
using PCL.Desktop.Views.FirstRun;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class LauncherCompatibilityProbeTests
{
    [TestMethod]
    public void Report_CanRun_IsFalse_WhenAnyItemIsFatal()
    {
        CompatibilityReport report = new(
            DateTimeOffset.UtcNow,
            [
                new CompatibilityCheckItem("a", "A", "ok", CompatibilityStatus.Ok, true),
                new CompatibilityCheckItem("b", "B", "bad", CompatibilityStatus.Fatal, true)
            ]);

        Assert.IsTrue(report.HasFatal);
        Assert.IsFalse(report.CanRun);
        Assert.AreEqual(1, report.OkCount);
        Assert.AreEqual(1, report.IssueCount);
    }

    [TestMethod]
    public void Report_CanRun_IsTrue_WhenOnlyOptionalUnavailable()
    {
        CompatibilityReport report = new(
            DateTimeOffset.UtcNow,
            [
                new CompatibilityCheckItem("a", "A", "ok", CompatibilityStatus.Ok, true),
                new CompatibilityCheckItem("b", "B", "skip", CompatibilityStatus.Unavailable, false)
            ]);

        Assert.IsFalse(report.HasFatal);
        Assert.IsTrue(report.CanRun);
    }

    [TestMethod]
    public void OobeDefaultFlows_IncludeCompatibilitySelfCheck()
    {
        CollectionAssert.Contains(OobeManifest.DefaultFullSteps.ToArray(), OobeStepId.Compatibility);
        CollectionAssert.Contains(OobeConfiguration.DefaultResumeSteps.ToArray(), OobeStepId.Compatibility);

        Assert.AreEqual(
            1,
            Array.IndexOf(OobeManifest.DefaultFullSteps.ToArray(), OobeStepId.Compatibility),
            "Compatibility should follow Welcome in full OOBE.");
        Assert.IsTrue(
            Array.IndexOf(OobeManifest.DefaultFullSteps.ToArray(), OobeStepId.Compatibility) <
            Array.IndexOf(OobeManifest.DefaultFullSteps.ToArray(), OobeStepId.Terms));

        // Version upgrade: short 3-page flow only.
        CollectionAssert.AreEqual(
            new[] { OobeStepId.Welcome, OobeStepId.Compatibility, OobeStepId.Finish },
            OobeManifest.DefaultUpdateSteps.ToArray());
    }
}
