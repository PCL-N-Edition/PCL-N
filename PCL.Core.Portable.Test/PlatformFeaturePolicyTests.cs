// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Platform;

namespace PCL.Core.Portable.Test;

[TestClass]
public sealed class PlatformFeaturePolicyTests
{
    [TestMethod]
    public void SystemAccentThemeIsExclusiveToLinuxAndMacOS()
    {
        Assert.IsFalse(PlatformFeaturePolicy.IsSystemAccentThemeSupportedOn(RuntimePlatform.Windows));
        Assert.IsTrue(PlatformFeaturePolicy.IsSystemAccentThemeSupportedOn(RuntimePlatform.Linux));
        Assert.IsTrue(PlatformFeaturePolicy.IsSystemAccentThemeSupportedOn(RuntimePlatform.MacOS));
        Assert.IsFalse(PlatformFeaturePolicy.IsSystemAccentThemeSupportedOn(RuntimePlatform.Other));
    }

    [TestMethod]
    public void CurrentPlatformPolicyMatchesRuntimeDetection()
    {
        var expected = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

        Assert.AreEqual(expected, PlatformFeaturePolicy.IsSystemAccentThemeSupported);
    }

    [TestMethod]
    public void CustomColorPaletteIsUnavailableOnWindows()
    {
        Assert.IsFalse(PlatformFeaturePolicy.IsCustomColorPaletteSupportedOn(RuntimePlatform.Windows));
        Assert.IsTrue(PlatformFeaturePolicy.IsCustomColorPaletteSupportedOn(RuntimePlatform.Linux));
        Assert.IsTrue(PlatformFeaturePolicy.IsCustomColorPaletteSupportedOn(RuntimePlatform.MacOS));
        Assert.IsFalse(PlatformFeaturePolicy.IsCustomColorPaletteSupportedOn(RuntimePlatform.Other));
    }

    [TestMethod]
    public void UseSystemGlfwIsLinuxOnly()
    {
        Assert.IsFalse(PlatformFeaturePolicy.IsUseSystemGlfwSupportedOn(RuntimePlatform.Windows));
        Assert.IsTrue(PlatformFeaturePolicy.IsUseSystemGlfwSupportedOn(RuntimePlatform.Linux));
        Assert.IsFalse(PlatformFeaturePolicy.IsUseSystemGlfwSupportedOn(RuntimePlatform.MacOS));
        Assert.IsFalse(PlatformFeaturePolicy.IsUseSystemGlfwSupportedOn(RuntimePlatform.Other));
        Assert.AreEqual(OperatingSystem.IsLinux(), PlatformFeaturePolicy.IsUseSystemGlfwSupported);
    }

    [TestMethod]
    public void ForceX11OnWaylandIsLinuxOnly()
    {
        Assert.IsFalse(PlatformFeaturePolicy.IsForceX11OnWaylandSupportedOn(RuntimePlatform.Windows));
        Assert.IsTrue(PlatformFeaturePolicy.IsForceX11OnWaylandSupportedOn(RuntimePlatform.Linux));
        Assert.IsFalse(PlatformFeaturePolicy.IsForceX11OnWaylandSupportedOn(RuntimePlatform.MacOS));
        Assert.IsFalse(PlatformFeaturePolicy.IsForceX11OnWaylandSupportedOn(RuntimePlatform.Other));
        Assert.AreEqual(OperatingSystem.IsLinux(), PlatformFeaturePolicy.IsForceX11OnWaylandSupported);
    }
}
