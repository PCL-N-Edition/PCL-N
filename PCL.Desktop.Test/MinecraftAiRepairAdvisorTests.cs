// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Launching;
using PCL.Desktop.Features.Launching;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MinecraftAiRepairAdvisorTests
{
    [TestMethod]
    public void ParseSuggestion_AcceptsAllowlistedActionAndMarkdown()
    {
        MinecraftAiRepairSuggestion? suggestion = MinecraftAiRepairAdvisor.ParseSuggestion(
            "prefix {\"action\":\"RepairVersionFiles\",\"analysisMarkdown\":\"### 原因\\n核心库缺失\",\"confidence\":0.82," +
            "\"stage\":\"正在校验核心库\",\"progress\":0.9} suffix",
            [MinecraftRepairActionKind.RepairVersionFiles]);

        Assert.IsNotNull(suggestion);
        Assert.AreEqual(MinecraftRepairActionKind.RepairVersionFiles, suggestion.Action);
        StringAssert.Contains(suggestion.AnalysisMarkdown, "核心库缺失");
        Assert.AreEqual(0.82d, suggestion.Confidence, 0.001d);
        Assert.IsNull(suggestion.Parameters.ModId);
    }

    [TestMethod]
    public void ParseSuggestion_RejectsActionOutsideRepairAllowlist()
    {
        MinecraftAiRepairSuggestion? suggestion = MinecraftAiRepairAdvisor.ParseSuggestion(
            "{\"action\":\"ReextractNatives\",\"analysisMarkdown\":\"delete\",\"confidence\":1," +
            "\"stage\":\"重新生成 Natives\",\"progress\":0.8}",
            [MinecraftRepairActionKind.InspectOnly]);

        Assert.IsNull(suggestion);
    }

    [TestMethod]
    public void ParseSuggestion_RequiresValidatedParametersForModUpdate()
    {
        MinecraftAiRepairSuggestion? suggestion = MinecraftAiRepairAdvisor.ParseSuggestion(
            "{\"action\":\"UpdateMod\",\"analysisMarkdown\":\"更新冲突模组\",\"confidence\":0.7," +
            "\"stage\":\"更新模组\",\"progress\":0.75," +
            "\"modId\":\"sodium\",\"modVersion\":\"0.6.9+mc1.21.1\"}",
            [MinecraftRepairActionKind.UpdateMod]);

        Assert.IsNotNull(suggestion);
        Assert.AreEqual("sodium", suggestion.Parameters.ModId);
        Assert.AreEqual("0.6.9+mc1.21.1", suggestion.Parameters.ModVersion);

        Assert.IsNull(MinecraftAiRepairAdvisor.ParseSuggestion(
            "{\"action\":\"DisableMod\",\"analysisMarkdown\":\"bad\",\"stage\":\"禁用模组\"," +
            "\"progress\":0.8,\"modId\":\"..\\\\evil\"}",
            [MinecraftRepairActionKind.DisableMod]));
    }

    [TestMethod]
    [DataRow("win-x64", ".zip")]
    [DataRow("win-arm64", ".zip")]
    [DataRow("linux-x64", ".tar.gz")]
    [DataRow("linux-arm64", ".tar.gz")]
    [DataRow("osx-x64", ".tar.gz")]
    [DataRow("osx-arm64", ".tar.gz")]
    public void ResolveRuntimePackage_CoversEveryDesktopRid(string runtimeId, string extension)
    {
        MinecraftAiRepairAdvisor.RuntimePackage package = MinecraftAiRepairAdvisor.ResolveRuntimePackage(runtimeId);

        Assert.AreEqual(runtimeId, package.RuntimeId);
        StringAssert.EndsWith(package.ArchiveFileName, extension);
        Assert.AreEqual(64, package.Sha256.Length);
        Assert.AreEqual("sourceforge.net", package.Urls[0].Host);
        Assert.AreEqual("github.com", package.Urls[1].Host);
    }
}
