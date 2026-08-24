// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Diagnostics;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class UltraLowPowerPolicyTests
{
    [TestMethod]
    public void CanEnter_WhenInactiveAndIdle()
    {
        bool result = UltraLowPowerPolicy.CanEnter(
            enabled: true,
            isWindowActive: false,
            new UltraLowPowerActivity(false, false, false));

        Assert.IsTrue(result);
    }

    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public void CannotEnter_WhileWorkIsActive(
        bool hasActiveTask,
        bool isLaunching,
        bool isLoggingIn)
    {
        bool result = UltraLowPowerPolicy.CanEnter(
            enabled: true,
            isWindowActive: false,
            new UltraLowPowerActivity(hasActiveTask, isLaunching, isLoggingIn));

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void CannotEnter_WhenWindowIsActiveOrFeatureIsDisabled()
    {
        UltraLowPowerActivity idle = new(false, false, false);

        Assert.IsFalse(UltraLowPowerPolicy.CanEnter(false, false, idle));
        Assert.IsFalse(UltraLowPowerPolicy.CanEnter(true, true, idle));
    }
}
