// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Desktop.Features.Community;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class CommunityDownloadPathsTests
{
    [TestMethod]
    public void ResolveDirectory_DataPackTargetOverrideUsesSelectedWorldDirectory()
    {
        string gameDirectory = Path.Combine(Path.GetTempPath(), "minecraft");
        string worldDataPacks = Path.Combine(gameDirectory, "saves", "Test World", "datapacks");

        string result = CommunityDownloadPaths.ResolveDirectory(
            CommunityResourceCategory.DataPack,
            gameDirectory,
            worldDataPacks);

        Assert.AreEqual(Path.GetFullPath(worldDataPacks), result);
        Assert.AreNotEqual(Path.Combine(gameDirectory, "datapacks"), result);
    }

    [TestMethod]
    public void ResolveDirectory_WithoutOverrideUsesCategoryFolder()
    {
        string gameDirectory = Path.Combine(Path.GetTempPath(), "minecraft");

        string result = CommunityDownloadPaths.ResolveDirectory(
            CommunityResourceCategory.DataPack,
            gameDirectory);

        Assert.AreEqual(Path.Combine(gameDirectory, "datapacks"), result);
    }
}
