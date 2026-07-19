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
    public void ParseSuggestion_AcceptsNoAbilityToolResult()
    {
        MinecraftAiRepairSuggestion? suggestion = MinecraftAiRepairAdvisor.ParseSuggestion(
            "{\"type\":\"noability\",\"analysisMarkdown\":\"没有安全修复动作，建议检查模组兼容性。\",\"confidence\":0.64}",
            []);

        Assert.IsNotNull(suggestion);
        Assert.IsTrue(suggestion.NoAbility);
        Assert.AreEqual(0, suggestion.RepairSteps.Count);
        StringAssert.Contains(suggestion.AnalysisMarkdown, "没有安全修复动作");
    }

    [TestMethod]
    public void ParseSuggestion_RejectsNoAbilityWithoutSummary()
    {
        MinecraftAiRepairSuggestion? suggestion = MinecraftAiRepairAdvisor.ParseSuggestion(
            "{\"type\":\"noability\"}",
            []);

        Assert.IsNull(suggestion);
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
    public void ParseSuggestion_AcceptsValidatedChainedRepairPlan()
    {
        const string output = """
            {"type":"progress","stage":"归纳异常","progress":0.25}
            {"type":"result","analysisMarkdown":"先更新前置，再校验文件。","confidence":0.81,"steps":[
              {"action":"UpdateMod","stage":"更新前置","progress":0.5,"rationale":"版本不兼容","modId":"fabric-api","modVersion":"0.100.1"},
              {"action":"RepairVersionFiles","stage":"校验核心文件","progress":0.9,"rationale":"排除缺失库"}
            ]}
            """;

        MinecraftAiRepairSuggestion? suggestion = MinecraftAiRepairAdvisor.ParseSuggestion(
            output,
            [MinecraftRepairActionKind.UpdateMod, MinecraftRepairActionKind.RepairVersionFiles]);

        Assert.IsNotNull(suggestion);
        Assert.AreEqual(2, suggestion.RepairSteps.Count);
        Assert.AreEqual(MinecraftRepairActionKind.UpdateMod, suggestion.RepairSteps[0].Action);
        Assert.AreEqual("fabric-api", suggestion.RepairSteps[0].Parameters.ModId);
        Assert.AreEqual(MinecraftRepairActionKind.RepairVersionFiles, suggestion.RepairSteps[1].Action);
        Assert.AreEqual("排除缺失库", suggestion.RepairSteps[1].Rationale);
    }

    [TestMethod]
    public void TryParseProgressEvent_ReadsOnlyStructuredProgressLines()
    {
        Assert.IsTrue(MinecraftAiRepairAdvisor.TryParseProgressEvent(
            "prefix {\"type\":\"progress\",\"stage\":\"正在匹配动作\",\"progress\":0.7}",
            out MinecraftAiRepairProgress? progress));
        Assert.IsNotNull(progress);
        Assert.AreEqual("正在匹配动作", progress.Stage);
        Assert.AreEqual(0.7d, progress.Progress, 0.001d);
        Assert.IsFalse(MinecraftAiRepairAdvisor.TryParseProgressEvent(
            "{\"type\":\"result\",\"stage\":\"完成\",\"progress\":1}",
            out _));
    }

    [TestMethod]
    public void ParseContextRequest_AcceptsOnlyWhitelistedReadOnlyScopes()
    {
        MinecraftAiContextRequest? request = MinecraftAiRepairAdvisor.ParseContextRequest(
            "{\"type\":\"context_request\",\"scopes\":[\"environment\",\"runtime_logs\",\"secrets\",\"runtime_logs\"]," +
            "\"stage\":\"读取运行信息\",\"progress\":0.8}");

        Assert.IsNotNull(request);
        CollectionAssert.AreEqual(
            new[] { MinecraftAiContextScope.Environment, MinecraftAiContextScope.RuntimeLogs },
            request.Scopes.ToArray());
        Assert.AreEqual("读取运行信息", request.Stage);
        Assert.AreEqual(0.8d, request.Progress, 0.001d);
    }

    [TestMethod]
    public void ParseSuggestion_StripsPrivateThinkingBeforeReadingResult()
    {
        const string output = "<think>private chain of thought {not valid json}</think>\n" +
                              "{\"type\":\"result\",\"analysisMarkdown\":\"可审计结论\",\"confidence\":0.7," +
                              "\"steps\":[{\"action\":\"InspectOnly\",\"stage\":\"检查\",\"progress\":1}]}";

        MinecraftAiRepairSuggestion? suggestion = MinecraftAiRepairAdvisor.ParseSuggestion(
            output,
            [MinecraftRepairActionKind.InspectOnly]);

        Assert.IsNotNull(suggestion);
        Assert.AreEqual("可审计结论", suggestion.AnalysisMarkdown);
    }

    [TestMethod]
    public void ContainsCompleteTerminalResult_RequiresClosedFinalJson()
    {
        Assert.IsFalse(MinecraftAiRepairAdvisor.ContainsCompleteTerminalResult(
            new System.Text.StringBuilder(
                "{\"type\":\"progress\",\"stage\":\"分析\",\"progress\":0.7}\n" +
                "{\"type\":\"result\",\"analysisMarkdown\":\"尚未完成")));
        Assert.IsTrue(MinecraftAiRepairAdvisor.ContainsCompleteTerminalResult(
            new System.Text.StringBuilder(
                "{\"type\":\"result\",\"analysisMarkdown\":\"完成\",\"steps\":[" +
                "{\"action\":\"InspectOnly\",\"stage\":\"完成\",\"progress\":1}]}")));
        Assert.IsTrue(MinecraftAiRepairAdvisor.ContainsCompleteTerminalResult(
            new System.Text.StringBuilder(
                "{\"type\":\"noability\",\"analysisMarkdown\":\"无法安全修复\"}")));
    }
    [TestMethod]
    public void BoundDetailedContext_PreservesBeginningAndEnd()
    {
        string bounded = MinecraftAiRepairAdvisor.BoundDetailedContext(
            "BEGIN-" + new string('x', 300) + "-END",
            96);

        Assert.AreEqual(96, bounded.Length);
        StringAssert.StartsWith(bounded, "BEGIN-");
        StringAssert.EndsWith(bounded, "-END");
        StringAssert.Contains(bounded, "上下文中段已由宿主截断");
    }

    [TestMethod]
    public async Task AdviseAsync_OpenAiCompatibleModelCanRequestOneContextRoundTrip()
    {
        const string contextRequest =
            "{\"type\":\"context_request\",\"scopes\":[\"runtime_logs\",\"launch_method\"]," +
            "\"stage\":\"读取诊断\",\"progress\":0.8}";
        const string result =
            "{\"type\":\"result\",\"analysisMarkdown\":\"日志表明需要继续检查。\",\"confidence\":0.6," +
            "\"steps\":[{\"action\":\"InspectOnly\",\"stage\":\"完成分析\",\"progress\":1}]}";
        SequencedChatHandler handler = new(contextRequest, result);
        MinecraftAiRepairAdvisor advisor = new(new HttpClient(handler), Path.GetTempPath());
        IReadOnlyList<MinecraftAiContextScope>? requestedScopes = null;

        MinecraftAiRepairSuggestion? suggestion = await advisor.AdviseAsync(
            new MinecraftLaunchFaultReport
            {
                Code = MinecraftLaunchFaultCode.Unknown,
                Stage = "GameProcess",
                Message = "crashed",
                AllowedActions = [MinecraftRepairActionKind.InspectOnly]
            },
            ["runtime line"],
            [],
            new MinecraftAiRepairContext("1.21.1", "Fabric", 21, 4096, "Test OS", "X64", 0, 1),
            (scopes, _) =>
            {
                requestedScopes = scopes;
                return Task.FromResult("[runtime_logs]\nredacted marker");
            },
            "zh-CN",
            new MinecraftAiModelOptions(
                Provider: MinecraftAiProvider.OpenAiCompatible,
                ApiBaseUrl: "https://example.test/v1",
                ApiModel: "reasoning-model",
                ApiKey: "test-secret",
                ReasoningEffort: MinecraftAiReasoningEffort.None),
            progress: null,
            CancellationToken.None);

        Assert.IsNotNull(suggestion);
        Assert.AreEqual(MinecraftRepairActionKind.InspectOnly, suggestion.Action);
        Assert.IsNotNull(requestedScopes);
        CollectionAssert.AreEqual(
            new[] { MinecraftAiContextScope.RuntimeLogs, MinecraftAiContextScope.LaunchMethod },
            requestedScopes.ToArray());
        Assert.AreEqual(2, handler.RequestBodies.Count);
        StringAssert.Contains(handler.RequestBodies[1], "redacted marker");
        Assert.IsTrue(handler.SawBearerHeader);
    }

    [TestMethod]
    [DataRow(0, "E2B", 3_000_000_000L)]
    [DataRow(1, "E4B", 4_000_000_000L)]
    public void ResolveLocalModel_ProvidesPinnedGemmaPackages(
        int modelValue,
        string sizeName,
        long minimumBytes)
    {
        MinecraftAiLocalModel model = (MinecraftAiLocalModel)modelValue;
        MinecraftAiRepairAdvisor.LocalModelPackage package = MinecraftAiRepairAdvisor.ResolveLocalModel(model);

        StringAssert.Contains(package.DisplayName, sizeName);
        StringAssert.EndsWith(package.FileName, ".gguf");
        Assert.AreEqual(64, package.Sha256.Length);
        Assert.IsTrue(package.ApproximateBytes >= minimumBytes);
        Assert.AreEqual("hf-mirror.com", package.Urls[0].Host);
        Assert.AreEqual("huggingface.co", package.Urls[1].Host);
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

    [TestMethod]
    [DataRow("win-x64", "vulkan")]
    [DataRow("win-arm64", "opencl")]
    [DataRow("linux-x64", "vulkan")]
    [DataRow("linux-arm64", "vulkan")]
    [DataRow("osx-x64", "metal")]
    [DataRow("osx-arm64", "metal")]
    public void ResolveGpuRuntimePackage_CoversEveryDesktopRid(string runtimeId, string backendName)
    {
        MinecraftAiRepairAdvisor.RuntimePackage? package =
            MinecraftAiRepairAdvisor.ResolveGpuRuntimePackage(runtimeId);

        Assert.IsNotNull(package);
        StringAssert.Contains(package.RuntimeId, backendName, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(64, package.Sha256.Length);
        StringAssert.Contains(package.Backend, "GPU", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow(0, 256)]
    [DataRow(4096, 4096)]
    [DataRow(100000, 32768)]
    public void NormalizeTokenBudget_ClampsToSafeRange(int value, int expected)
    {
        Assert.AreEqual(expected, MinecraftAiRepairAdvisor.NormalizeTokenBudget(value));
    }
    [TestMethod]
    public void CreateInferenceStartInfo_DisablesInteractiveModeAndSelectsGpuLayers()
    {
        var startInfo = MinecraftAiRepairAdvisor.CreateInferenceStartInfo(
            "llama-cli.exe",
            "model.gguf",
            "prompt",
            useGpu: true,
            tokenBudget: 4096);

        CollectionAssert.Contains(startInfo.ArgumentList, "--no-conversation");
        CollectionAssert.Contains(startInfo.ArgumentList, "--simple-io");
        int tokenArgument = startInfo.ArgumentList.IndexOf("-n");
        Assert.AreEqual("4096", startInfo.ArgumentList[tokenArgument + 1]);
        int contextArgument = startInfo.ArgumentList.IndexOf("-c");
        Assert.AreEqual("8192", startInfo.ArgumentList[contextArgument + 1]);
        int gpuArgument = startInfo.ArgumentList.IndexOf("-ngl");
        Assert.IsTrue(gpuArgument >= 0);
        Assert.AreEqual("all", startInfo.ArgumentList[gpuArgument + 1]);
    }

    private sealed class SequencedChatHandler(params string[] modelOutputs) : HttpMessageHandler
    {
        private int _index;

        public List<string> RequestBodies { get; } = [];

        public bool SawBearerHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            SawBearerHeader |= request.Headers.Authorization is { Scheme: "Bearer", Parameter: "test-secret" };
            string output = modelOutputs[Math.Min(_index++, modelOutputs.Length - 1)]
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            string response = "{\"choices\":[{\"message\":{\"content\":\"" + output + "\"}}]}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
