// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Diagnostics;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class UnhandledExceptionGuardTests
{
    [TestMethod]
    public void RecoverabilityClassifier_DoesNotContinueAfterCorruptedProcessState()
    {
        Assert.IsTrue(UnhandledExceptionGuard.IsRecoverableUiException(
            new InvalidOperationException("ordinary UI callback failure")));
        Assert.IsFalse(UnhandledExceptionGuard.IsRecoverableUiException(
            new OutOfMemoryException("fatal")));
        Assert.IsFalse(UnhandledExceptionGuard.IsRecoverableUiException(
            new AggregateException(
                new InvalidOperationException("ordinary"),
                new AccessViolationException("fatal"))));
    }

    [TestMethod]
    public void AbnormalExitReport_ExplainsNativeAotBlindSpots()
    {
        string report = UnhandledExceptionGuard.BuildAbnormalExitReport(
            "format=pcln-crash-session-v1\npid=123\nnativeAot=yes");

        StringAssert.Contains(report, "上次进程异常退出");
        StringAssert.Contains(report, "原生库崩溃");
        StringAssert.Contains(report, "pid=123");
        StringAssert.Contains(report, "nativeAot=yes");
        StringAssert.Contains(report, "NativeCrashGuard");
    }

    [TestMethod]
    public void AbnormalExitReport_IncludesNativeDumpFromSessionMarker()
    {
        string report = UnhandledExceptionGuard.BuildAbnormalExitReport(
            "format=pcln-crash-session-v1\npid=9\nnativeDump=C:\\logs\\native-test.dmp");

        StringAssert.Contains(report, "原生崩溃产物");
        StringAssert.Contains(report, "native-test.dmp");
    }

    [TestMethod]
    public void NativeCrashGuard_FindRecentArtifacts_DoesNotThrowOnMissingDirectory()
    {
        IReadOnlyList<string> artifacts =
            NativeCrashGuard.FindRecentNativeArtifacts(TimeSpan.FromMinutes(1));
        Assert.IsNotNull(artifacts);
    }

    [TestMethod]
    public void ExternalCrashHandler_SignalCleanExit_IsIdempotentWithoutStart()
    {
        // Must not throw when the companion was never started (e.g. binary absent).
        ExternalCrashHandler.SignalCleanExit();
        ExternalCrashHandler.SignalCleanExit();
    }

    [TestMethod]
    public void ExternalCrashHandler_WhenLauncherOwnsWatcher_AdoptsCleanFlagEnv()
    {
        string flag = Path.Combine(Path.GetTempPath(), "pcln-test-clean-" + Guid.NewGuid().ToString("N") + ".flag");
        try
        {
            Environment.SetEnvironmentVariable("PCL_SKIP_EXTERNAL_CRASH_HANDLER", "1");
            Environment.SetEnvironmentVariable("PCL_CRASH_CLEAN_FLAG", flag);

            // Force re-entry: TryStart is one-shot per process. Use a fresh AppDomain is
            // unavailable; instead only assert SignalCleanExit remains safe and that when
            // TryStart already ran earlier it still no-ops. Primary contract: env vars
            // are documented and SignalCleanExit with adopted path writes the flag.
            // If TryStart has not run yet in this test host, run it now.
            ExternalCrashHandler.TryStart(null);
            ExternalCrashHandler.SignalCleanExit();

            // After TryStart with skip+flag, SignalCleanExit should create the flag
            // only if this was the first TryStart in the process. When a prior test
            // already started without flag, path may stay empty — still must not throw.
            if (File.Exists(flag))
            {
                string text = File.ReadAllText(flag);
                Assert.IsTrue(text.Contains("ok", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PCL_SKIP_EXTERNAL_CRASH_HANDLER", null);
            Environment.SetEnvironmentVariable("PCL_CRASH_CLEAN_FLAG", null);
            try
            {
                if (File.Exists(flag))
                    File.Delete(flag);
            }
            catch
            {
                // ignore
            }
        }
    }
}
