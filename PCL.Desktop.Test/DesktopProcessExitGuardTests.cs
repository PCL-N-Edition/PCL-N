// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Diagnostics;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class DesktopProcessExitGuardTests
{
    [TestMethod]
    public void Watchdog_ForcesConfiguredExitAfterTimeout()
    {
        using ManualResetEventSlim exited = new(initialState: false);
        int observedExitCode = int.MinValue;
        int timeoutNotifications = 0;

        Thread watchdog = DesktopProcessExitGuard.StartWatchdog(
            TimeSpan.FromMilliseconds(20),
            exitCode: 23,
            code =>
            {
                observedExitCode = code;
                exited.Set();
            },
            () => Interlocked.Increment(ref timeoutNotifications));

        Assert.IsTrue(watchdog.IsBackground);
        Assert.IsTrue(exited.Wait(TimeSpan.FromSeconds(2)), "Exit watchdog did not fire.");
        Assert.IsTrue(watchdog.Join(TimeSpan.FromSeconds(2)), "Exit watchdog thread did not finish.");
        Assert.AreEqual(23, observedExitCode);
        Assert.AreEqual(1, timeoutNotifications);
    }
}
