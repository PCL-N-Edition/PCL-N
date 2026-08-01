// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Guarantees that a completed Avalonia lifetime cannot remain as a headless primary instance.
/// </summary>
internal static class DesktopProcessExitGuard
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static int armed;

    public static void Arm(int exitCode)
    {
        if (Interlocked.Exchange(ref armed, 1) != 0)
            return;

        // Environment.Exit bypasses Program.Main's finally block, so mark this controlled
        // shutdown as complete before the fail-safe thread can intervene.
        UnhandledExceptionGuard.CompleteSession(completedNormally: true);
        DesktopFileLog.Info(
            "Startup",
            $"进程退出守卫已启动；若桌面生命周期在 {DefaultTimeout.TotalSeconds:0} 秒内未返回，将终止无窗口残留进程。");

        StartWatchdog(
            DefaultTimeout,
            exitCode,
            Environment.Exit,
            () => DesktopFileLog.Error(
                "Startup",
                "桌面生命周期退出后进程仍未结束；正在终止无窗口残留进程。"));
    }

    internal static Thread StartWatchdog(
        TimeSpan timeout,
        int exitCode,
        Action<int> exitProcess,
        Action timeoutReached)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(exitProcess);
        ArgumentNullException.ThrowIfNull(timeoutReached);

        Thread watchdog = new(() =>
        {
            Thread.Sleep(timeout);
            try
            {
                timeoutReached();
            }
            catch
            {
                // Logging must never prevent the fail-safe exit.
            }

            exitProcess(exitCode);
        })
        {
            IsBackground = true,
            Name = "PCL N process exit watchdog"
        };
        watchdog.Start();
        return watchdog;
    }
}
