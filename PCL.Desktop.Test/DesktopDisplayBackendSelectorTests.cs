// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Platform;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class DesktopDisplayBackendSelectorTests
{
    [TestMethod]
    public void WaylandIsNeverForcedOnWindowsOrMacOs()
    {
        bool result = DesktopDisplayBackendSelector.ShouldUseWayland(
            isLinux: false,
            isWindowsSubsystemForLinux: false,
            "wayland-0",
            "/run/user/1000",
            _ => true);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void WaylandIsNeverForcedInsideWslOrWslg()
    {
        bool result = DesktopDisplayBackendSelector.ShouldUseWayland(
            isLinux: true,
            isWindowsSubsystemForLinux: true,
            "wayland-0",
            "/mnt/wslg/runtime-dir",
            _ => true);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void NativeLinuxRequiresAReachableWaylandSocket()
    {
        string? probedPath = null;
        bool result = DesktopDisplayBackendSelector.ShouldUseWayland(
            isLinux: true,
            isWindowsSubsystemForLinux: false,
            "wayland-1",
            "/run/user/1000",
            path =>
            {
                probedPath = path;
                return true;
            });

        Assert.IsTrue(result);
        Assert.AreEqual(Path.Combine("/run/user/1000", "wayland-1"), probedPath);
    }

    [TestMethod]
    public void NativeLinuxFallsBackWhenWaylandSessionIsIncomplete()
    {
        Assert.IsFalse(DesktopDisplayBackendSelector.ShouldUseWayland(
            isLinux: true,
            isWindowsSubsystemForLinux: false,
            waylandDisplay: null,
            "/run/user/1000",
            _ => true));
        Assert.IsFalse(DesktopDisplayBackendSelector.ShouldUseWayland(
            isLinux: true,
            isWindowsSubsystemForLinux: false,
            "wayland-0",
            xdgRuntimeDirectory: null,
            _ => true));
        Assert.IsFalse(DesktopDisplayBackendSelector.ShouldUseWayland(
            isLinux: true,
            isWindowsSubsystemForLinux: false,
            "wayland-0",
            "/run/user/1000",
            _ => false));
    }
}
