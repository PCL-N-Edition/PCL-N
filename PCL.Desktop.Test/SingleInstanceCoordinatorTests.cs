// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Test;

[TestClass]
public sealed class SingleInstanceCoordinatorTests
{
    [TestMethod]
    public void SecondaryCanTakeOverAfterPreviousInstanceReleasesMutex()
    {
        using SingleInstanceCoordinator primary = SingleInstanceCoordinator.Create();
        Assert.IsTrue(primary.IsPrimaryInstance);

        using SingleInstanceCoordinator secondary = SingleInstanceCoordinator.Create();
        Assert.IsFalse(secondary.IsPrimaryInstance);

        primary.Dispose();

        Assert.IsTrue(secondary.TryBecomePrimary(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(secondary.IsPrimaryInstance);
    }
}
