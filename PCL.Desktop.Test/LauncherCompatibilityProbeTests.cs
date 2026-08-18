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
    public void OobeDefaultFlows_OmitCompatibilitySelfCheck()
    {
        CollectionAssert.DoesNotContain(OobeManifest.DefaultFullSteps.ToArray(), OobeStepId.Compatibility);
        CollectionAssert.DoesNotContain(OobeConfiguration.DefaultResumeSteps.ToArray(), OobeStepId.Compatibility);
        CollectionAssert.DoesNotContain(OobeManifest.DefaultUpdateSteps.ToArray(), OobeStepId.Compatibility);

        Assert.AreEqual(
            1,
            Array.IndexOf(OobeManifest.DefaultFullSteps.ToArray(), OobeStepId.Terms),
            "Terms should follow Welcome in full OOBE after Compatibility removal.");

        // Version upgrade: short welcome → finish flow only.
        CollectionAssert.AreEqual(
            new[] { OobeStepId.Welcome, OobeStepId.Finish },
            OobeManifest.DefaultUpdateSteps.ToArray());
    }
}
