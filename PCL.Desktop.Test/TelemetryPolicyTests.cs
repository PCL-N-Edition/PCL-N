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
    public void SentryClientsDisableDiskCacheWithoutInvalidZeroLimit()
    {
        const string dsn = "https://0123456789abcdef0123456789abcdef@example.com/1";

        Sentry.SentryOptions essential = LauncherTelemetry.CreateEssentialSentryOptions(dsn);
        Sentry.SentryOptions experience = LauncherTelemetry.CreateExperienceSentryOptions(dsn);

        Assert.IsNull(essential.CacheDirectoryPath);
        Assert.IsNull(experience.CacheDirectoryPath);
        Assert.IsGreaterThanOrEqualTo(1, essential.MaxCacheItems);
        Assert.IsGreaterThanOrEqualTo(1, experience.MaxCacheItems);
    }

    [TestMethod]
    public void InvalidEssentialDsnDoesNotBreakLauncherInitialization()
    {
        const string variable = "PCL_SENTRY_ESSENTIAL_DSN";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "not-a-valid-dsn");
            LauncherTelemetry.Initialize(new LauncherSettings());
        }
        finally
        {
            LauncherTelemetry.Shutdown();
            Environment.SetEnvironmentVariable(variable, previous);
        }
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
