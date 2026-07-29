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
    }
}
