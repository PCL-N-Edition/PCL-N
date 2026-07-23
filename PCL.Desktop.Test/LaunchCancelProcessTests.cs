// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Desktop.Features.Launching;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class LaunchCancelProcessTests
{
    [TestMethod]
    public void TryTerminateIncompleteLaunch_KillsRunningProcessTree()
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"")
            {
                UseShellExecute = false
            };

        using Process process = Process.Start(startInfo)!;
        Assert.IsFalse(process.HasExited);

        MinecraftLaunchCoordinator.TryTerminateIncompleteLaunch(process, Guid.Empty);

        Assert.IsTrue(process.WaitForExit(5000));
        Assert.IsTrue(process.HasExited);
    }

    [TestMethod]
    public void TryTerminateIncompleteLaunch_IsNoOpForNullOrExitedProcess()
    {
        MinecraftLaunchCoordinator.TryTerminateIncompleteLaunch(null, Guid.Empty);

        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { UseShellExecute = false };
        using Process process = Process.Start(startInfo)!;
        Assert.IsTrue(process.WaitForExit(5000));

        MinecraftLaunchCoordinator.TryTerminateIncompleteLaunch(process, Guid.Empty);
        Assert.IsTrue(process.HasExited);
    }
}
