// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Launching;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Launching;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MinecraftAiRepairAdvisorTests
{
    [TestMethod]
    public void JvmHostPreMainFailure_RequiresReversibleHostIsolation()
    {
        MinecraftLaunchFaultReport fault = new()
        {
            Code = MinecraftLaunchFaultCode.JvmInitializationFailed,
            Stage = "JvmMode",
            Subsystem = "JvmHost",
            Message = "ExitCode=0xC0000409",
            AllowedActions =
            [
                MinecraftRepairActionKind.SelectCompatibleJava,
                MinecraftRepairActionKind.DisableExperimentalJvmHost,
                MinecraftRepairActionKind.InspectOnly
            ]
        };

        Assert.IsTrue(MinecraftAiRepairAdvisor.IsPreMainJvmHostInitializationFailure(fault));
        string instruction = MinecraftAiRepairAdvisor.BuildJvmHostIsolationInstruction();
        StringAssert.Contains(instruction, "JNA、Netty、LWJGL");
        StringAssert.Contains(instruction, "不得继续请求同类运行时上下文");
        StringAssert.Contains(instruction, "DisableExperimentalJvmHost");
        MinecraftAiRepairSuggestion suggestion = MinecraftAiRepairAdvisor.CreateJvmHostIsolationSuggestion();
        Assert.AreEqual(MinecraftRepairActionKind.DisableExperimentalJvmHost, suggestion.Action);
        Assert.AreEqual(1, suggestion.RepairSteps.Count);
    }

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
    public void ParseUnsafeResultAsInspectOnly_PreservesDiagnosisButRejectsNonResultOrDisallowedFallback()
    {
        const string output =
            "{\"type\":\"result\",\"analysisMarkdown\":\"原生异常退出，但现有证据无法定位具体模组。\",\"confidence\":0.6," +
            "\"steps\":[{\"action\":\"DisableMod\",\"stage\":\"禁用可疑模组\",\"progress\":0.2,\"modId\":null}]}";

        MinecraftAiRepairSuggestion? fallback = MinecraftAiRepairAdvisor.ParseUnsafeResultAsInspectOnly(
            output,
            [MinecraftRepairActionKind.InspectOnly, MinecraftRepairActionKind.DisableMod]);

        Assert.IsNotNull(fallback);
        Assert.IsTrue(fallback.NoAbility);
        Assert.AreEqual(MinecraftRepairActionKind.InspectOnly, fallback.Action);
        StringAssert.Contains(fallback.AnalysisMarkdown, "无法定位具体模组");
        Assert.IsNull(MinecraftAiRepairAdvisor.ParseUnsafeResultAsInspectOnly(
            output,
            [MinecraftRepairActionKind.DisableMod]));
        Assert.IsNull(MinecraftAiRepairAdvisor.ParseUnsafeResultAsInspectOnly(
            "{\"type\":\"context_request\",\"analysisMarkdown\":\"不是终态结果。\"}",
            [MinecraftRepairActionKind.InspectOnly]));
    }

    [TestMethod]
    public void ParseReadOnlyDiagnosis_PreservesAnalysisWhenRepairPlanIsUnsafe()
    {
        const string output = "{\"type\":\"result\",\"analysisMarkdown\":\"### AI 诊断\\nETF 信息不是崩溃原因，建议检查更早的 ERROR 和异常堆栈。\",\"confidence\":0.45," +
                              "\"steps\":[{\"action\":\"ReextractNatives\",\"stage\":\"不安全动作\",\"progress\":1}]}";

        Assert.IsNull(MinecraftAiRepairAdvisor.ParseSuggestion(
            output,
            [MinecraftRepairActionKind.InspectOnly]));
        MinecraftAiRepairSuggestion? diagnosis = MinecraftAiRepairAdvisor.ParseReadOnlyDiagnosis(output);

        Assert.IsNotNull(diagnosis);
        Assert.IsTrue(diagnosis.NoAbility);
        StringAssert.Contains(diagnosis.AnalysisMarkdown, "ETF 信息不是崩溃原因");
        StringAssert.Contains(diagnosis.AnalysisMarkdown, "更早的 ERROR");
    }

    [TestMethod]
    public void ParseReadOnlyDiagnosis_ExtractsCompletedAnalysisFromTruncatedJson()
    {
        MinecraftAiRepairSuggestion? diagnosis = MinecraftAiRepairAdvisor.ParseReadOnlyDiagnosis(
            "{\"type\":\"result\",\"analysisMarkdown\":\"可能是模组冲突。\\n请提供 crash-report。\"," +
            "\"confidence\":0.4,\"steps\":[{\"action\":");

        Assert.IsNotNull(diagnosis);
        Assert.IsTrue(diagnosis.NoAbility);
        Assert.AreEqual("可能是模组冲突。\n请提供 crash-report。", diagnosis.AnalysisMarkdown);
    }

    [TestMethod]
    public void ParseReadOnlyDiagnosis_AcceptsPlainTextButRejectsRuntimeNoise()
    {
        MinecraftAiRepairSuggestion? diagnosis = MinecraftAiRepairAdvisor.ParseReadOnlyDiagnosis(
            "### AI 分析\n当前 ETF 行只是普通 INFO 日志，真正异常可能位于更早位置。");

        Assert.IsNotNull(diagnosis);
        Assert.IsTrue(diagnosis.NoAbility);
        Assert.IsNull(MinecraftAiRepairAdvisor.ParseReadOnlyDiagnosis(
            "llama_model_loader: loaded meta data with 29 key-value pairs"));
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
              {"action":"UpdateMod","stage":"更新前置","progress":0.5,"rationale":"版本不兼容","modId":"fabric-api","modVersion":"0.100.1","javaMajorVersion":null},
              {"action":"RepairVersionFiles","stage":"校验核心文件","progress":0.9,"rationale":"排除缺失库"}
            ]}
            """;

        MinecraftAiRepairSuggestion? suggestion = MinecraftAiRepairAdvisor.ParseSuggestion(
            output,
            [MinecraftRepairActionKind.UpdateMod, MinecraftRepairActionKind.RepairVersionFiles]);

        Assert.IsNotNull(suggestion);
        Assert.AreEqual(2, suggestion.RepairSteps.Count);
        Assert.IsNull(suggestion.RepairSteps[0].Parameters.JavaMajorVersion);
        Assert.AreEqual(MinecraftRepairActionKind.UpdateMod, suggestion.RepairSteps[0].Action);
        Assert.AreEqual("fabric-api", suggestion.RepairSteps[0].Parameters.ModId);
        Assert.AreEqual(MinecraftRepairActionKind.RepairVersionFiles, suggestion.RepairSteps[1].Action);
        Assert.AreEqual("排除缺失库", suggestion.RepairSteps[1].Rationale);
    }

    [TestMethod]
    public void TryParseProgressEvent_ReadsOnlyStructuredProgressLines()
    {
        Assert.IsTrue(MinecraftAiRepairAdvisor.TryParseProgressEvent(
            "prefix {\"type\":\"progress\",\"name\":\"正在匹配动作\",\"progress\":0.7}",
            out MinecraftAiRepairProgress? progress));
        Assert.IsNotNull(progress);
        Assert.AreEqual("正在匹配动作", progress.Stage);
        Assert.AreEqual(0.7d, progress.Progress, 0.001d);
        Assert.IsTrue(MinecraftAiRepairAdvisor.TryParseProgressEvent(
            "{\"type\":\"progress\",\"stage\":\"兼容旧阶段\",\"progress\":0.4}",
            out MinecraftAiRepairProgress? legacy));
        Assert.AreEqual("兼容旧阶段", legacy?.Stage);
        Assert.IsFalse(MinecraftAiRepairAdvisor.TryParseProgressEvent(
            "{\"type\":\"result\",\"stage\":\"完成\",\"progress\":1}",
            out _));
    }

    [TestMethod]
    public void ParseContextRequest_AcceptsOnlyWhitelistedReadOnlyScopes()
    {
        MinecraftAiContextRequest? request = MinecraftAiRepairAdvisor.ParseContextRequest(
            "{\"type\":\"context_request\",\"scopes\":[\"environment\",\"runtime_logs\",\"secrets\",\"runtime_logs\"]," +
            "\"name\":\"读取运行信息\",\"progress\":0.8}");

        Assert.IsNotNull(request);
        CollectionAssert.AreEqual(
            new[] { MinecraftAiContextScope.Environment, MinecraftAiContextScope.RuntimeLogs },
            request.Scopes.ToArray());
        Assert.AreEqual("读取运行信息", request.Stage);
        Assert.AreEqual(0.8d, request.Progress, 0.001d);
    }

    [TestMethod]
    public void ParseModTools_RequireSafeSearchAndProjectIdentifiers()
    {
        MinecraftAiModSearchRequest? search = MinecraftAiRepairAdvisor.ParseModSearchRequest(
            "{\"type\":\"mod_search\",\"query\":\"mafglib\",\"name\":\"搜索前置\",\"progress\":0.75}");
        Assert.IsNotNull(search);
        Assert.AreEqual("mafglib", search.Query);
        Assert.AreEqual("搜索前置", search.Stage);

        MinecraftAiModProjectDetailsRequest? details = MinecraftAiRepairAdvisor.ParseModProjectDetailsRequest(
            "{\"type\":\"mod_project_details\",\"source\":\"Modrinth\",\"projectId\":\"abc-123\"," +
            "\"name\":\"读取兼容详情\",\"progress\":0.82}");
        Assert.IsNotNull(details);
        Assert.AreEqual(CommunityResourceSource.Modrinth, details.Source);
        Assert.AreEqual("abc-123", details.ProjectId);
        Assert.IsNull(MinecraftAiRepairAdvisor.ParseModProjectDetailsRequest(
            "{\"type\":\"mod_project_details\",\"projectId\":\"../unsafe\"}"));
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
        Assert.IsTrue(MinecraftAiRepairAdvisor.ContainsCompleteTerminalResult(
            new System.Text.StringBuilder(
                "模型输出前缀，共超过固定读取块。\n{\n" +
                "  \"type\": \"result\",\n" +
                "  \"analysisMarkdown\": \"包含花括号 {证据} 和转义引号 \\\"内容\\\"\",\n" +
                "  \"steps\": [\n" +
                "    { \"action\": \"InspectOnly\", \"stage\": \"完成\", \"progress\": 1 }\n" +
                "  ]\n" +
                "}")));
        Assert.IsFalse(MinecraftAiRepairAdvisor.ContainsCompleteTerminalResult(
            new System.Text.StringBuilder(
                "{\n  \"type\": \"result\",\n  \"steps\": [\n" +
                "    { \"action\": \"InspectOnly\" }\n")));
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
    public async Task AdviseAsync_HostDrivesMissingDependencyToolsWhenModelReturnsNoAbility()
    {
        const string noAbility =
            "{\"type\":\"noability\",\"analysisMarkdown\":\"不知道 mafglib 的 ProjectId。\",\"confidence\":0.7}";
        const string detailsRequest =
            "{\"type\":\"mod_project_details\",\"source\":\"Modrinth\"," +
            "\"projectId\":\"mafglib-project\",\"name\":\"读取 mafglib 详情\",\"progress\":0.82}";
        const string result =
            "{\"type\":\"result\",\"analysisMarkdown\":\"已确认兼容前置。\",\"confidence\":0.9," +
            "\"steps\":[{\"action\":\"DownloadMod\",\"stage\":\"下载 mafglib\",\"progress\":1," +
            "\"modId\":\"mafglib-project\",\"modVersion\":\"0.4.2\"}]}";
        SequencedChatHandler handler = new(noAbility, detailsRequest, result);
        StubCommunityCatalog catalog = new();
        using MinecraftAiRepairAdvisor advisor = new(
            new HttpClient(handler),
            Path.GetTempPath(),
            communityCatalog: catalog);

        MinecraftAiRepairSuggestion? suggestion = await advisor.AdviseAsync(
            new MinecraftLaunchFaultReport
            {
                Code = MinecraftLaunchFaultCode.MissingModDependency,
                Stage = "GameProcess",
                Message = "tweakerge requires mafglib",
                AllowedActions = [MinecraftRepairActionKind.DownloadMod]
            },
            ["Mod tweakerge requires mafglib 0.4 or above"],
            [],
            new MinecraftAiRepairContext(
                "1.21.1", "neoforge", 21, 4096, "Test OS", "X64", 0, 1, ["mafglib"]),
            contextProvider: null,
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
        Assert.IsFalse(suggestion.NoAbility);
        Assert.AreEqual(MinecraftRepairActionKind.DownloadMod, suggestion.Action);
        Assert.AreEqual("mafglib", catalog.SearchQuery);
        Assert.AreEqual(1, catalog.VersionRequests);
        Assert.AreEqual("mafglib-project", catalog.VersionProjectId);
        Assert.AreEqual(3, handler.RequestBodies.Count);
        StringAssert.Contains(handler.RequestBodies[1], "mafglib-project");
        StringAssert.Contains(handler.RequestBodies[2], "steps[].modId");
    }

    [TestMethod]
    public void FormatProcessExitCodeEvidence_IdentifiesWindowsFastFail()
    {
        string evidence = MinecraftAiRepairAdvisor.FormatProcessExitCodeEvidence(-1073740791);

        StringAssert.Contains(evidence, "Signed=-1073740791");
        StringAssert.Contains(evidence, "Hex=0xC0000409");
        StringAssert.Contains(evidence, "STATUS_STACK_BUFFER_OVERRUN");
        StringAssert.Contains(
            MinecraftAiRepairAdvisor.FormatProcessExitCodeEvidence(0),
            "正常退出");
    }

    [TestMethod]
    public void BuildBoundedRepairPrompt_DropsOldHistoryAndStaysWithinBudget()
    {
        string prompt = MinecraftAiRepairAdvisor.BuildBoundedRepairPrompt(
            "BASE-" + new string('b', 200),
            "TOOLS-" + new string('t', 200),
            "LATEST-" + new string('o', 200),
            "FEEDBACK-" + new string('f', 200),
            240);

        Assert.AreEqual(240, prompt.Length);
        StringAssert.Contains(prompt, "BASE-");
        StringAssert.Contains(prompt, "TOOLS-");
        StringAssert.Contains(prompt, "[模型最近一轮输出]");
        StringAssert.Contains(prompt, "oo");
        StringAssert.Contains(prompt, "[宿主最近一次指令或校验反馈]");
        StringAssert.Contains(prompt, "fff");
        Assert.AreEqual(80, MinecraftAiRepairAdvisor.EstimateTokenCount(new string('x', 240)));
    }

    [TestMethod]
    public async Task AdviseAsync_EmptyResponseRetriesOnceThenFails()
    {
        SequencedChatHandler handler = new(string.Empty, string.Empty);
        using MinecraftAiRepairAdvisor advisor = new(new HttpClient(handler), Path.GetTempPath());

        await Assert.ThrowsAsync<InvalidDataException>(() => advisor.AdviseAsync(
            new MinecraftLaunchFaultReport
            {
                Code = MinecraftLaunchFaultCode.Unknown,
                Stage = "GameProcess",
                Message = "empty model",
                AllowedActions = [MinecraftRepairActionKind.InspectOnly]
            },
            [], [],
            new MinecraftAiRepairContext("1.21.1", "NeoForge", 21, 4096, "Test OS", "X64", 0, 1),
            null,
            "zh-CN",
            new MinecraftAiModelOptions(
                Provider: MinecraftAiProvider.OpenAiCompatible,
                ApiBaseUrl: "https://example.test/v1",
                ApiModel: "reasoning-model",
                ApiKey: "test-secret"),
            null,
            CancellationToken.None));

        Assert.AreEqual(2, handler.RequestBodies.Count);
        Assert.AreNotEqual(handler.RequestBodies[0], handler.RequestBodies[1]);
        StringAssert.Contains(handler.RequestBodies[1], "result");
        StringAssert.Contains(handler.RequestBodies[1], "noability");
    }

    [TestMethod]
    public async Task AdviseAsync_RepeatedContextRequestUsesExistingContextAndRequestsTerminalResult()
    {
        const string contextRequest =
            "{\"type\":\"context_request\",\"scopes\":[\"runtime_logs\"]," +
            "\"name\":\"读取日志\",\"progress\":0.8}";
        const string result =
            "{\"type\":\"result\",\"analysisMarkdown\":\"已根据现有日志完成分析。\"," +
            "\"confidence\":0.6,\"steps\":[{\"action\":\"InspectOnly\"," +
            "\"stage\":\"完成分析\",\"progress\":1}]}";
        SequencedChatHandler handler = new(contextRequest, contextRequest, result);
        using MinecraftAiRepairAdvisor advisor = new(new HttpClient(handler), Path.GetTempPath());
        int contextRequests = 0;

        MinecraftAiRepairSuggestion? suggestion = await advisor.AdviseAsync(
            new MinecraftLaunchFaultReport
            {
                Code = MinecraftLaunchFaultCode.Unknown,
                Stage = "GameProcess",
                Message = "unknown crash",
                AllowedActions = [MinecraftRepairActionKind.InspectOnly]
            },
            [], [],
            new MinecraftAiRepairContext("1.21.1", "Fabric", 21, 4096, "Test OS", "X64", 0, 1),
            (_, _) =>
            {
                contextRequests++;
                return Task.FromResult("runtime detail");
            },
            "zh-CN",
            new MinecraftAiModelOptions(
                Provider: MinecraftAiProvider.OpenAiCompatible,
                ApiBaseUrl: "https://example.test/v1",
                ApiModel: "reasoning-model",
                ApiKey: "test-secret"),
            null,
            CancellationToken.None);

        Assert.IsNotNull(suggestion);
        Assert.AreEqual(1, contextRequests);
        Assert.AreEqual(3, handler.RequestBodies.Count);
        StringAssert.Contains(handler.RequestBodies[2], "context_request");
        StringAssert.Contains(handler.RequestBodies[2], "result");
    }

    [TestMethod]
    public void FormatLocalServerTerminalMetadata_ReportsStopAndTokenFields()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"stop\":true,\"stopped_eos\":true,\"truncated\":false," +
            "\"tokens_predicted\":0,\"tokens_evaluated\":3195," +
            "\"timings\":{\"predicted_n\":0,\"prompt_n\":3195}}");

        string? metadata = MinecraftAiRepairAdvisor.FormatLocalServerTerminalMetadata(document.RootElement);

        Assert.IsNotNull(metadata);
        StringAssert.Contains(metadata, "stopped_eos=True");
        StringAssert.Contains(metadata, "tokens_predicted=0");
        StringAssert.Contains(metadata, "timings.prompt_n=3195");
    }

    [TestMethod]
    public async Task AdviseAsync_UnsafeModActionFallsBackToInspectOnlyWithoutAnotherInference()
    {
        const string unsafeResult =
            "{\"type\":\"result\",\"analysisMarkdown\":\"0xC0000409 表示原生异常，但没有证据定位具体模组。\",\"confidence\":0.6," +
            "\"steps\":[" +
            "{\"action\":\"ReextractNatives\",\"stage\":\"重新提取原生库\",\"progress\":0.33}," +
            "{\"action\":\"InspectOnly\",\"stage\":\"仅检查\",\"progress\":0.66}," +
            "{\"action\":\"DisableMod\",\"stage\":\"禁用可疑模组\",\"progress\":1,\"modId\":null}]}";
        SequencedChatHandler handler = new(unsafeResult);
        using MinecraftAiRepairAdvisor advisor = new(new HttpClient(handler), Path.GetTempPath());

        MinecraftAiRepairSuggestion? suggestion = await advisor.AdviseAsync(
            new MinecraftLaunchFaultReport
            {
                Code = MinecraftLaunchFaultCode.Unknown,
                Stage = "GameProcess",
                Message = "native crash",
                AllowedActions = [
                    MinecraftRepairActionKind.InspectOnly,
                    MinecraftRepairActionKind.ReextractNatives,
                    MinecraftRepairActionKind.DisableMod,
                    MinecraftRepairActionKind.DownloadMod
                ]
            },
            [], [],
            new MinecraftAiRepairContext(
                "1.21.1", "Fabric", 21, 4096, "Windows", "X64", 134, 100, ProcessExitCode: -1073740791),
            null,
            "zh-CN",
            new MinecraftAiModelOptions(
                Provider: MinecraftAiProvider.OpenAiCompatible,
                ApiBaseUrl: "https://example.test/v1",
                ApiModel: "reasoning-model",
                ApiKey: "test-secret"),
            null,
            CancellationToken.None);

        Assert.IsNotNull(suggestion);
        Assert.IsTrue(suggestion.NoAbility);
        Assert.AreEqual(MinecraftRepairActionKind.InspectOnly, suggestion.Action);
        Assert.AreEqual(1, handler.RequestBodies.Count);
    }

    [TestMethod]
    public async Task AdviseAsync_InvalidResultsContinueUntilUserCancellation()
    {
        using CancellationTokenSource cancellation = new();
        CancelAfterRequestsHandler handler = new(cancellation, 10);
        using MinecraftAiRepairAdvisor advisor = new(new HttpClient(handler), Path.GetTempPath());

        await Assert.ThrowsAsync<OperationCanceledException>(() => advisor.AdviseAsync(
            new MinecraftLaunchFaultReport
            {
                Code = MinecraftLaunchFaultCode.Unknown,
                Stage = "GameProcess",
                Message = "invalid model output",
                AllowedActions = [MinecraftRepairActionKind.InspectOnly]
            },
            [],
            [],
            new MinecraftAiRepairContext("1.21.1", "NeoForge", 21, 4096, "Test OS", "X64", 0, 1),
            contextProvider: null,
            "zh-CN",
            new MinecraftAiModelOptions(
                Provider: MinecraftAiProvider.OpenAiCompatible,
                ApiBaseUrl: "https://example.test/v1",
                ApiModel: "reasoning-model",
                ApiKey: "test-secret",
                ReasoningEffort: MinecraftAiReasoningEffort.None),
            progress: null,
            cancellation.Token));

        Assert.AreEqual(10, handler.RequestCount);
    }

    [TestMethod]
    public async Task AdviseAsync_FeedsValidationFailureBackUntilNoAbility()
    {
        const string unsafeResult =
            "{\"type\":\"result\",\"analysisMarkdown\":\"缺少前置。\",\"confidence\":0.8," +
            "\"steps\":[{\"action\":\"DownloadMod\",\"stage\":\"下载前置\",\"progress\":1}]}";
        const string noAbility =
            "{\"type\":\"noability\",\"analysisMarkdown\":\"无法在允许动作内安全安装，建议手动检查前置。\",\"confidence\":0.7}";
        SequencedChatHandler handler = new(unsafeResult, noAbility);
        MinecraftAiRepairAdvisor advisor = new(new HttpClient(handler), Path.GetTempPath());

        MinecraftAiRepairSuggestion? suggestion = await advisor.AdviseAsync(
            new MinecraftLaunchFaultReport
            {
                Code = MinecraftLaunchFaultCode.Unknown,
                Stage = "GameProcess",
                Message = "missing dependency",
                AllowedActions = [MinecraftRepairActionKind.InspectOnly]
            },
            [],
            [],
            new MinecraftAiRepairContext("1.21.1", "NeoForge", 21, 4096, "Test OS", "X64", 0, 1),
            contextProvider: null,
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
        Assert.IsTrue(suggestion.NoAbility);
        Assert.AreEqual(2, handler.RequestBodies.Count);
        StringAssert.Contains(handler.RequestBodies[1], "InspectOnly");
        StringAssert.Contains(handler.RequestBodies[1], "noability");
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
    public void ResolveCompletionExecutable_MapsCliToSiblingCompletion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcln-llama-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string cli = Path.Combine(directory, OperatingSystem.IsWindows() ? "llama-cli.exe" : "llama-cli");
            string completion = Path.Combine(
                directory,
                OperatingSystem.IsWindows() ? "llama-completion.exe" : "llama-completion");
            File.WriteAllText(cli, string.Empty);
            File.WriteAllText(completion, string.Empty);

            Assert.AreEqual(
                completion,
                MinecraftAiRepairAdvisor.ResolveCompletionExecutable(cli));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveCompletionExecutableRejectsCliWithoutCompletionSibling()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcln-llama-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string cli = Path.Combine(directory, OperatingSystem.IsWindows() ? "llama-cli.exe" : "llama-cli");
            File.WriteAllText(cli, string.Empty);
            FileNotFoundException? exception = null;
            try
            {
                MinecraftAiRepairAdvisor.ResolveCompletionExecutable(cli);
            }
            catch (FileNotFoundException caught)
            {
                exception = caught;
            }
            Assert.IsNotNull(exception);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveServerExecutable_MapsCompletionToSiblingServer()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcln-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string completion = Path.Combine(
                directory,
                OperatingSystem.IsWindows() ? "llama-completion.exe" : "llama-completion");
            string server = Path.Combine(
                directory,
                OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
            File.WriteAllText(completion, string.Empty);
            File.WriteAllText(server, string.Empty);

            Assert.AreEqual(server, MinecraftAiRepairAdvisor.ResolveServerExecutable(completion));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveServerExecutable_RejectsMissingServer()
    {
        string completion = Path.Combine(
            Path.GetTempPath(),
            OperatingSystem.IsWindows() ? "llama-completion.exe" : "llama-completion");
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            MinecraftAiRepairAdvisor.ResolveServerExecutable(completion));
    }

    [TestMethod]
    public void CreateServerStartInfo_UsesLoopbackCacheAndSingleSlot()
    {
        ProcessStartInfo startInfo = MinecraftAiRepairAdvisor.CreateServerStartInfo(
            "llama-server.exe",
            "model.gguf",
            31415,
            "secret-key",
            useGpu: true,
            contextSize: 8192);

        AssertArgument(startInfo.ArgumentList, "--host", "127.0.0.1");
        AssertArgument(startInfo.ArgumentList, "--port", "31415");
        AssertArgument(startInfo.ArgumentList, "--model", "model.gguf");
        AssertArgument(startInfo.ArgumentList, "--api-key", "secret-key");
        AssertArgument(startInfo.ArgumentList, "--parallel", "1");
        AssertArgument(startInfo.ArgumentList, "--gpu-layers", "all");
        AssertArgument(startInfo.ArgumentList, "--ctx-size", "8192");
        CollectionAssert.Contains(startInfo.ArgumentList, "--cache-prompt");
        CollectionAssert.Contains(startInfo.ArgumentList, "--no-ui");
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
    }

    private static void AssertArgument(Collection<string> arguments, string name, string expected)
    {
        int index = arguments.IndexOf(name);
        Assert.IsTrue(index >= 0, $"Missing argument {name}.");
        Assert.AreEqual(expected, arguments[index + 1]);
    }

    [TestMethod]
    public void CreateLocalServerCompletionPayload_EnablesPrefixCacheEveryRound()
    {
        JsonObject payload = MinecraftAiRepairAdvisor.CreateLocalServerCompletionPayload("stable prefix", 4096);

        Assert.AreEqual("stable prefix", payload["prompt"]?.GetValue<string>());
        Assert.AreEqual(4096, payload["n_predict"]?.GetValue<int>());
        Assert.IsTrue(payload["stream"]?.GetValue<bool>());
        Assert.IsTrue(payload["cache_prompt"]?.GetValue<bool>());
    }

    [TestMethod]
    public void CreateInferenceStartInfo_UsesCompletionPromptFileUtf8AndGpuLayers()
    {
        const string promptFile = "prompt-utf8.txt";
        var startInfo = MinecraftAiRepairAdvisor.CreateInferenceStartInfo(
            "llama-completion.exe",
            "model.gguf",
            promptFile,
            useGpu: true,
            tokenBudget: 4096);

        CollectionAssert.DoesNotContain(startInfo.ArgumentList, "--no-conversation");
        CollectionAssert.DoesNotContain(startInfo.ArgumentList, "--simple-io");
        CollectionAssert.DoesNotContain(startInfo.ArgumentList, "-p");
        int promptFileArgument = startInfo.ArgumentList.IndexOf("-f");
        Assert.IsTrue(promptFileArgument >= 0);
        Assert.AreEqual(promptFile, startInfo.ArgumentList[promptFileArgument + 1]);
        CollectionAssert.Contains(startInfo.ArgumentList, "-no-cnv");
        int fitArgument = startInfo.ArgumentList.IndexOf("-fit");
        Assert.IsTrue(fitArgument >= 0);
        Assert.AreEqual("off", startInfo.ArgumentList[fitArgument + 1]);
        Assert.IsTrue(startInfo.RedirectStandardInput);
        Assert.AreEqual(System.Text.Encoding.UTF8.WebName, startInfo.StandardOutputEncoding?.WebName);
        Assert.AreEqual(System.Text.Encoding.UTF8.WebName, startInfo.StandardErrorEncoding?.WebName);
        Assert.AreEqual(System.Text.Encoding.UTF8.WebName, startInfo.StandardInputEncoding?.WebName);
        CollectionAssert.DoesNotContain(startInfo.ArgumentList, "--json-schema");
        CollectionAssert.DoesNotContain(startInfo.ArgumentList, "--grammar");
        int tokenArgument = startInfo.ArgumentList.IndexOf("-n");
        Assert.AreEqual("4096", startInfo.ArgumentList[tokenArgument + 1]);
        int contextArgument = startInfo.ArgumentList.IndexOf("-c");
        Assert.AreEqual("8192", startInfo.ArgumentList[contextArgument + 1]);
        int gpuArgument = startInfo.ArgumentList.IndexOf("-ngl");
        Assert.IsTrue(gpuArgument >= 0);
        Assert.AreEqual("all", startInfo.ArgumentList[gpuArgument + 1]);
    }

    private sealed class StubCommunityCatalog : ICommunityResourceCatalog
    {
        public string? SearchQuery { get; private set; }
        public int VersionRequests { get; private set; }
        public string? VersionProjectId { get; private set; }

        public Task<IReadOnlyList<CommunityResourceEntry>> SearchAsync(
            CommunityResourceCategory category,
            string query,
            CommunitySearchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            SearchQuery = query;
            return Task.FromResult<IReadOnlyList<CommunityResourceEntry>>([
                new CommunityResourceEntry(
                    "wrong-first-project", "malilib", "MaLiLib", "different library", "mod", null, 2000, null),
                new CommunityResourceEntry(
                    "mafglib-project", "mafglib", "MaFgLib", "library", "mod", null, 1000, null)
            ]);
        }

        public Task<IReadOnlyList<CommunityResourceVersion>> GetVersionsAsync(
            CommunityResourceEntry entry,
            CommunitySearchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            VersionRequests++;
            VersionProjectId = entry.ProjectId;
            return Task.FromResult<IReadOnlyList<CommunityResourceVersion>>([
                new CommunityResourceVersion(
                    "version-1", "0.4.2", "0.4.2", null, DateTimeOffset.UtcNow,
                    ["1.21.1"], ["neoforge"],
                    [new CommunityResourceDownloadFile("mafglib.jar", "https://example.test/mafglib.jar", 1, "version-1", "0.4.2")])
            ]);
        }

        public Task<CommunityResourceDownloadFile?> ResolveDownloadAsync(
            CommunityResourceEntry entry, CommunitySearchOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult<CommunityResourceDownloadFile?>(null);

        public Task<CommunityResourceEntry?> GetProjectAsync(
            CommunityResourceSource source, string projectId,
            CancellationToken cancellationToken = default) => Task.FromResult<CommunityResourceEntry?>(null);

        public Task<CommunityResourceFileIdentity?> LookupFileBySha1Async(
            string sha1Hex, CancellationToken cancellationToken = default) =>
            Task.FromResult<CommunityResourceFileIdentity?>(null);

        public Task<CommunityResourceVersion?> GetLatestVersionAsync(
            string projectId, CommunitySearchOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult<CommunityResourceVersion?>(null);
    }

    private sealed class CancelAfterRequestsHandler(
        CancellationTokenSource cancellation,
        int requestLimit) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount >= requestLimit)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            const string response =
                "{\"choices\":[{\"message\":{\"content\":\"{invalid}\"}}]}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            });
        }
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
