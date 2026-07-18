// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Core.App;
using PCL.Desktop.Hosting;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class LauncherUpdatePolicyTests
{
    [TestMethod]
    public void Resolve_UsesPersistedChannelAndMode()
    {
        LauncherSettings settings = new();
        settings.SetIntegerOption(LauncherUpdatePolicy.ChannelSettingKey, 1);
        settings.SetIntegerOption(LauncherUpdatePolicy.ModeSettingKey, 2);

        LauncherUpdatePolicy policy = LauncherUpdatePolicy.Resolve(settings, "Release");

        Assert.AreEqual(UpdateChannel.Beta, policy.Channel);
        Assert.AreEqual(2, policy.Mode);
    }

    [TestMethod]
    [DataRow("Release", UpdateChannel.Release)]
    [DataRow("Beta", UpdateChannel.Beta)]
    [DataRow("CI", UpdateChannel.CI)]
    [DataRow("Dev", UpdateChannel.CI)]
    public void Resolve_MissingChannelUsesBuildConfiguration(string configuration, UpdateChannel expected)
    {
        LauncherUpdatePolicy policy = LauncherUpdatePolicy.Resolve(new LauncherSettings(), configuration);

        Assert.AreEqual(expected, policy.Channel);
        Assert.AreEqual(1, policy.Mode);
    }

    [TestMethod]
    public void Resolve_ClampsCorruptPersistedValues()
    {
        LauncherSettings settings = new();
        settings.SetIntegerOption(LauncherUpdatePolicy.ChannelSettingKey, 99);
        settings.SetIntegerOption(LauncherUpdatePolicy.ModeSettingKey, -8);

        LauncherUpdatePolicy policy = LauncherUpdatePolicy.Resolve(settings, "Release");

        Assert.AreEqual(UpdateChannel.CI, policy.Channel);
        Assert.AreEqual(0, policy.Mode);
    }
}
