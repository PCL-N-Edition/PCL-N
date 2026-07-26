// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Minecraft.Launch.Arguments;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftLaunchArgumentServiceTests
{
    [TestMethod]
    public void LegacyGameArguments_ShouldAppendResolutionAndNormalizeOptiFineTweaker()
    {
        MinecraftGameArgumentResult result = MinecraftLaunchArgumentService.BuildLegacyGameArguments(
            new MinecraftLegacyGameArgumentRequest
            {
                MinecraftArguments = "--username ${auth_player_name} --tweakClass optifine.OptiFineTweaker",
                HasForge = true,
                HasOptiFine = true
            });

        Assert.AreEqual(OptiFineTweakerAdjustment.ReplacedPlainTweaker, result.OptiFineTweakerAdjustment);
        StringAssert.Contains(result.Arguments, "--height ${resolution_height} --width ${resolution_width}");
        StringAssert.EndsWith(result.Arguments, "--tweakClass optifine.OptiFineForgeTweaker");
    }

    [TestMethod]
    public void ModernGameArguments_ShouldApplyOsRulesAndMergeSwitchValues()
    {
        JsonObject versionJson = JsonNode.Parse(
            """
            {
              "arguments": {
                "game": [
                  "--username",
                  "${auth_player_name}",
                  {
                    "rules": [{ "action": "allow", "os": { "name": "windows" } }],
                    "value": ["--winOnly", "enabled"]
                  },
                  {
                    "rules": [{ "action": "allow", "features": { "is_quick_play_multiplayer": true } }],
                    "value": "--blockedQuickPlay"
                  }
                ]
              }
            }
            """)!.AsObject();

        MinecraftGameArgumentResult result = MinecraftLaunchArgumentService.BuildModernGameArguments(
            new MinecraftModernGameArgumentRequest
            {
                VersionJson = versionJson,
                RuleContext = new MinecraftArgumentRuleContext
                {
                    OperatingSystem = MinecraftArgumentOperatingSystem.Win32,
                    Architecture = MinecraftArgumentArchitecture.X64,
                    OperatingSystemVersion = "10.0.19045",
                }
            });

        Assert.AreEqual("--username ${auth_player_name} --winOnly enabled", result.Arguments);
    }

    [TestMethod]
    [DataRow("arm64", true)]
    [DataRow("aarch64", true)]
    [DataRow("x86", false)]
    [DataRow("i386", false)]
    [DataRow("x86_64", false)]
    [DataRow("amd64", false)]
    [DataRow("unknown", false)]
    public void Rules_ShouldMatchOnlyArm64ArchitectureAliases(
        string ruleArchitecture,
        bool expected)
    {
        JsonNode rules = JsonNode.Parse(
            $$"""
            [
              { "action": "allow", "os": { "arch": "{{ruleArchitecture}}" } }
            ]
            """)!;
        MinecraftArgumentRuleContext context = new()
        {
            OperatingSystem = MinecraftArgumentOperatingSystem.Linux,
            Architecture = MinecraftArgumentArchitecture.Arm64
        };

        Assert.AreEqual(expected, MinecraftLaunchArgumentService.IsRuleAllowed(rules, context));
    }

    [TestMethod]
    public void Rules_ShouldRejectArchitectureConstraintWhenArchitectureIsUnknown()
    {
        JsonNode rules = JsonNode.Parse(
            """
            [{ "action": "allow", "os": { "arch": "arm64" } }]
            """)!;
        MinecraftArgumentRuleContext context = new()
        {
            OperatingSystem = MinecraftArgumentOperatingSystem.Linux,
            Architecture = MinecraftArgumentArchitecture.Unknown
        };

        Assert.IsFalse(MinecraftLaunchArgumentService.IsRuleAllowed(rules, context));
    }

    [TestMethod]
    public void FinalArguments_ShouldReplaceTokensAndRemoveEmptyVersionType()
    {
        MinecraftFinalArgumentResult result = MinecraftLaunchArgumentService.BuildFinalArguments(
            new MinecraftFinalArgumentRequest
            {
                Arguments = "${auth_player_name} --versionType ${version_type} --gameDir ${game_directory}",
                JavaMajorVersion = 17,
                Replacements = new Dictionary<string, string>
                {
                    ["${auth_player_name}"] = "Steve",
                    ["${version_type}"] = "",
                    ["${game_directory}"] = @"D:\Games\PCL Test"
                }
            });

        Assert.AreEqual(
            @"-Dstderr.encoding=UTF-8 -Dstdout.encoding=UTF-8 Steve --gameDir ""D:\Games\PCL Test""",
            result.Arguments);
    }

    [TestMethod]
    public void FinalArguments_ShouldUseQuickPlayForModernServerJoin()
    {
        MinecraftFinalArgumentResult result = MinecraftLaunchArgumentService.BuildFinalArguments(
            new MinecraftFinalArgumentRequest
            {
                Arguments = "--demo",
                JavaMajorVersion = 8,
                Replacements = new Dictionary<string, string>(),
                Server = "play.example.com",
                ReleaseTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });

        Assert.AreEqual("--demo --quickPlayMultiplayer \"play.example.com\"", result.Arguments);
        Assert.IsFalse(result.ShouldWarnOptiFineAutoJoin);
    }

    [TestMethod]
    public void FinalArguments_ShouldUseQuickPlayForSingleplayerWorld()
    {
        MinecraftFinalArgumentResult result = MinecraftLaunchArgumentService.BuildFinalArguments(
            new MinecraftFinalArgumentRequest
            {
                Arguments = "--demo",
                JavaMajorVersion = 17,
                Replacements = new Dictionary<string, string>(),
                WorldName = "New World"
            });

        StringAssert.EndsWith(result.Arguments, "--demo --quickPlaySingleplayer \"New World\"");
    }

    [TestMethod]
    public void FinalArguments_ShouldUseLegacyServerArgumentsForOldVersions()
    {
        MinecraftFinalArgumentResult result = MinecraftLaunchArgumentService.BuildFinalArguments(
            new MinecraftFinalArgumentRequest
            {
                Arguments = "--demo",
                JavaMajorVersion = 8,
                Replacements = new Dictionary<string, string>(),
                Server = "play.example.com:25566",
                ReleaseTime = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero),
                HasOptiFine = true
            });

        Assert.AreEqual("--demo --server play.example.com --port 25566", result.Arguments);
        Assert.IsTrue(result.ShouldWarnOptiFineAutoJoin);
    }
}
