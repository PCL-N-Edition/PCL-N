// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Desktop.Telemetry;
using PCL.Desktop.Views.FirstRun;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class TelemetryPolicyTests
{
    [TestMethod]
    public void ExperienceProgramIsOptInByDefault()
    {
        Assert.IsFalse(LauncherSettingDefaults.GetBoolean(LauncherTelemetry.ExperienceSettingKey));
    }

    [TestMethod]
    public void FailureFingerprintDoesNotDependOnExceptionMessage()
    {
        string first = TelemetryDataPolicy.CreateFailureFingerprint(
            new InvalidOperationException("token=first-secret"),
            "game.launch");
        string second = TelemetryDataPolicy.CreateFailureFingerprint(
            new InvalidOperationException("C:\\Users\\someone\\private.txt"),
            "game.launch");

        Assert.AreEqual(first, second);
        Assert.AreEqual(24, first.Length);
    }

    [TestMethod]
    public void ProductPropertiesRejectIdentityAndPathFields()
    {
        IReadOnlyDictionary<string, string> sanitized = TelemetryDataPolicy.SanitizeProperties(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["feature"] = "instance.export",
                ["account_id"] = "123",
                ["target_path"] = "C:\\private",
                ["access_token"] = "secret"
            });

        Assert.AreEqual("instance.export", sanitized["feature"]);
        Assert.IsFalse(sanitized.ContainsKey("account_id"));
        Assert.IsFalse(sanitized.ContainsKey("target_path"));
        Assert.IsFalse(sanitized.ContainsKey("access_token"));
    }

    [TestMethod]
    public void UpdatedUsersSeeLegalAndTelemetryBeforeFinish()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                OobeStepId.Welcome,
                OobeStepId.Terms,
                OobeStepId.Privacy,
                OobeStepId.Telemetry,
                OobeStepId.Finish
            },
            OobeManifest.DefaultUpdateSteps.ToArray());
    }

    [TestMethod]
    public void ResumeFlowStillPresentsTelemetryAfterPathRestart()
    {
        CollectionAssert.Contains(OobeConfiguration.DefaultResumeSteps.ToArray(), OobeStepId.Telemetry);
        Assert.IsTrue(
            Array.IndexOf(OobeConfiguration.DefaultResumeSteps.ToArray(), OobeStepId.Telemetry) <
            Array.IndexOf(OobeConfiguration.DefaultResumeSteps.ToArray(), OobeStepId.Finish));
    }
}
