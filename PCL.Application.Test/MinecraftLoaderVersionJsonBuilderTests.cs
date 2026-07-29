// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Downloads;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftLoaderVersionJsonBuilderTests
{
    [TestMethod]
    public void CreateDefaultVersionId_UsesUpstreamInstanceNamingConvention()
    {
        Assert.AreEqual(
            "1.20.1-Fabric_0.16.14",
            MinecraftLoaderVersionJsonBuilder.CreateDefaultVersionId(
                MinecraftLoaderKind.Fabric,
                "1.20.1",
                "0.16.14"));
        Assert.AreEqual(
            "1.12.2-LegacyFabric_0.16.0",
            MinecraftLoaderVersionJsonBuilder.CreateDefaultVersionId(
                MinecraftLoaderKind.LegacyFabric,
                "1.12.2",
                "0.16.0"));
        Assert.AreEqual(
            "1.20.1-Forge_47.3.0",
            MinecraftLoaderVersionJsonBuilder.CreateDefaultVersionId(
                MinecraftLoaderKind.Forge,
                "1.20.1",
                "47.3.0"));
        Assert.AreEqual(
            "1.21.1-NeoForge_21.1.204",
            MinecraftLoaderVersionJsonBuilder.CreateDefaultVersionId(
                MinecraftLoaderKind.NeoForge,
                "1.21.1",
                "21.1.204"));
        Assert.AreEqual(
            "1.20.1-OptiFine_HD_U_I6",
            MinecraftLoaderVersionJsonBuilder.CreateDefaultVersionId(
                MinecraftLoaderKind.OptiFine,
                "1.20.1",
                "1.20.1_HD_U_I6"));
        Assert.AreEqual(
            "1.21.8-LabyMod_4.5.14_Production",
            MinecraftLoaderVersionJsonBuilder.CreateDefaultVersionId(
                MinecraftLoaderKind.LabyMod,
                "1.21.8",
                "production+4.5.14+prod123"));
        Assert.AreEqual(
            "1.21.8-LabyMod_4.6.0_Snapshot",
            MinecraftLoaderVersionJsonBuilder.CreateDefaultVersionId(
                MinecraftLoaderKind.LabyMod,
                "1.21.8",
                "snapshot+4.6.0+snapshot123"));
    }
}
