// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Launching;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftMissingDependencyParserTests
{
    [TestMethod]
    public void Parse_ShouldReadChineseAndEnglishFabricMessages()
    {
        IReadOnlyList<MinecraftMissingDependency> dependencies = MinecraftMissingDependencyParser.Parse(
        [
            "需要模组 'Fabric API' (fabric-api) 的 0.100.0 及以上版本，但没有安装它！",
            "Mod sodium requires mod 'Fabric API' (fabric-api) version 0.100.0 or later, which is missing!",
            "Mod example requires cloth-config any version, which is missing!"
        ]);

        Assert.AreEqual(2, dependencies.Count);
        Assert.AreEqual("fabric-api", dependencies[0].ModId);
        Assert.AreEqual("0.100.0", dependencies[0].RequiredVersion);
        Assert.AreEqual("cloth-config", dependencies[1].ModId);
        Assert.IsNull(dependencies[1].RequiredVersion);
    }

    [TestMethod]
    public void Parse_ShouldIgnoreUnrelatedLogLines()
    {
        IReadOnlyList<MinecraftMissingDependency> dependencies = MinecraftMissingDependencyParser.Parse(
        [
            "[main/INFO]: Loading Minecraft",
            "A mod recommends another optional mod",
            "Game crashed for an unrelated reason"
        ]);

        Assert.AreEqual(0, dependencies.Count);
    }

    [TestMethod]
    public void Parse_ShouldReadNeoForgeMandatoryDependencyLines()
    {
        IReadOnlyList<MinecraftMissingDependency> dependencies = MinecraftMissingDependencyParser.Parse(
        [
            "[main/ERROR] Missing or unsupported mandatory dependencies:",
            "Mod farmersdelight requires version [1.2,) of bookshelf",
            "Mod create requires version 0.5.1 of flywheel"
        ]);

        Assert.AreEqual(2, dependencies.Count);
        Assert.AreEqual("bookshelf", dependencies[0].ModId);
        Assert.AreEqual("[1.2,)", dependencies[0].RequiredVersion);
        Assert.AreEqual("flywheel", dependencies[1].ModId);
        Assert.AreEqual("0.5.1", dependencies[1].RequiredVersion);
    }
}
