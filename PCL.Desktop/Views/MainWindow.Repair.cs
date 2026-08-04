// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PCL.Application.Downloads;
using PCL.Application.Instances;
using PCL.Application.Launching;
using PCL.Application.Minecraft.Java;
using PCL.Application.Minecraft.Launch;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Features.Shared;
using PCL.Desktop.Localization;
using PCL.Desktop.Theme;
using PCL.Domain.Minecraft.Launch;
using PCL.Platform.Java;
using PCL.Platform.Paths;

namespace PCL.Desktop.Views;

public partial class MainWindow
{
    private async Task ObserveRunningGameFaultAsync(RunningGameContext context)
    {
        if (context.FaultReport is null)
            return;
        try
        {
            MinecraftLaunchFaultReport? report = await context.FaultReport.ConfigureAwait(false);
            if (report?.Code != MinecraftLaunchFaultCode.MissingModDependency ||
                !ReferenceEquals(_runningGameContext, context))
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
                _launchRight?.AppendLog("已在游戏仍运行时检测到 NeoForge 缺失依赖，正在进入修复流程。"));
            await TryRepairMissingDependenciesAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DesktopFileLog.Warn("GameProcess", "观察运行中结构化故障失败。", exception);
        }
    }

    private void RunningGameProcess_Exited(object? sender, EventArgs e)
    {
        RunningGameContext? context = _runningGameContext;
        int exitCode = 0;
        if (sender is Process process)
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = -1;
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_runningGameProcess, sender) ||
                _runningGameProcess is null ||
                _runningGameProcess.HasExited)
            {
                SetGameRunningExtras(null);
            }
        }, DispatcherPriority.Background);

        if (exitCode != 0 && context is not null)
            _ = TryRepairMissingDependenciesAsync(context with { ProcessExitCode = exitCode });
    }

    private async Task TryRepairMissingDependenciesAsync(RunningGameContext context)
    {
        _launchCancellation?.Cancel();
        _launchCancellation?.Dispose();
        _launchCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _launchCancellation.Token;
        MinecraftRepairSession session = context.RepairSession ?? new MinecraftRepairSession(context.Settings);
        string gameDirectory = context.Instance.InstanceDirectory;
        MinecraftLaunchFaultReport? fault = null;
        IReadOnlyList<string> crashLines = [];
        string analysisMarkdown = string.Empty;
        bool aiProducedDiagnosis = false;
        MinecraftRepairExecutionResult repair = new("尚未执行修复。", true);
        try
        {
            gameDirectory = await InstanceGameDirectory.ResolveAsync(context.Instance, cancellationToken)
                .ConfigureAwait(false);
            crashLines = await ReadRecentCrashLinesAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
            fault = await AwaitFaultReportAsync(context.FaultReport, cancellationToken).ConfigureAwait(false);
            fault ??= MinecraftLaunchFaultAnalyzer.AnalyzeText(crashLines, "GameProcess");
            IReadOnlyList<MinecraftMissingDependency> dependencies = MinecraftMissingDependencyParser.Parse(crashLines);
            analysisMarkdown = BuildConventionalCrashAnalysis(fault, dependencies);
            if (!string.IsNullOrWhiteSpace(session.LastModelAnalysis))
                analysisMarkdown = session.LastModelAnalysis;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                context.LaunchPage.ShowRepairWorkflow(
                    AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                    AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Parse", "解析 Minecraft 异常"),
                    0.08d,
                    fault.Code.ToString(),
                    context.Instance);
                _launchRight?.AppendLog(
                    $"错误处理器：{fault.Code} · 子系统={fault.Subsystem} · 节点={fault.Stage}" +
                    (string.IsNullOrWhiteSpace(fault.LastClassName) ? string.Empty : " · 类=" + fault.LastClassName));
            });

            if (session.Attempt != MinecraftRepairAttempt.None)
            {
                string failedRepair = BuildFailedRepairFeedback(session, fault, context.ProcessExitCode);
                await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.AppendLog(failedRepair));
            }

            bool aiEnabled = context.Settings.GetBooleanOption(
                LauncherSettingKeys.ExperimentalMinecraftAiRepair,
                LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalMinecraftAiRepair.Value));
            MinecraftRepairActionKind conventionalAction = SelectConventionalRepairAction(
                fault,
                dependencies,
                context.NativesDirectory);
            if (ShouldExecuteConventionalRepairDirectly(
                    session.Attempt == MinecraftRepairAttempt.None,
                    context.Settings.AutomaticallyRepairGameIssues,
                    aiEnabled))
            {
                if (IsAutomaticallyExecutableRepair(conventionalAction))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => context.LaunchPage.ShowRepairWorkflow(
                        AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                        AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复"),
                        0.28d,
                        conventionalAction.ToString(),
                        context.Instance));
                    try
                    {
                        repair = await ExecuteMinecraftRepairAsync(
                                context,
                                fault,
                                conventionalAction,
                                dependencies,
                                gameDirectory,
                                suggestion: null,
                                session.Transaction,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception conventionalException)
                        when (conventionalException is not OperationCanceledException)
                    {
                        repair = new MinecraftRepairExecutionResult(
                            "常规修复执行失败：" + conventionalException.Message,
                            true);
                        DesktopFileLog.Warn(
                            "MinecraftRepair",
                            "常规修复执行失败，将尝试 AI 修复模型。",
                            conventionalException);
                        await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.AppendLog(repair.Message));
                    }
                    if (!repair.IsFailure && repair.MadeChanges)
                    {
                        session.Attempt = MinecraftRepairAttempt.ConventionalApplied;
                        session.LastRepairSummary = BuildRepairAttemptSummary(
                            "常规自动修复",
                            conventionalAction.ToString(),
                            repair);
                        await RestartMinecraftAfterRepairAsync(context, session, repair.Message, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                    if (!repair.IsFailure)
                    {
                        DesktopFileLog.Info(
                            "MinecraftRepair",
                            "常规修复检查完成但没有产生任何改动，将直接调用 AI 修复模型。");
                        await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.AppendLog(
                            "常规修复没有产生改动，跳过无意义的重启并直接调用 AI 修复模型。"));
                    }
                }
            }

            // Auth / account failures need the user to reconnect — never download multi-GB AI models.
            if (aiEnabled &&
                fault.Code is MinecraftLaunchFaultCode.AuthenticationFailed
                    or MinecraftLaunchFaultCode.SessionServiceUnavailable)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    context.LaunchPage.ShowRepairWorkflow(
                        AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "需要重新登录"),
                        AvaloniaLocalizationManager.GetText(
                            "Crash.Repair.Stage.Account",
                            "账户认证失败，请在设置中重新连接在线服务账户"),
                        1d,
                        fault.Code.ToString(),
                        context.Instance);
                    _launchRight?.AppendLog(
                        "启动失败属于账户认证问题，已跳过 AI 修复模型下载。请打开「设置 → 在线 → 账户」连接 PCL N 在线服务。");
                });
                return;
            }

            if (aiEnabled)
            {
                string conventionalSuggestion = IsAutomaticallyExecutableRepair(conventionalAction)
                    ? conventionalAction.ToString()
                    : "无可执行建议";
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    context.LaunchPage.ShowRepairWorkflow(
                        AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                        AvaloniaLocalizationManager.GetText("Crash.Model.Stage.Prepare", "准备 AI 修复模型"),
                        0.01d,
                        $"{MinecraftAiRepairAdvisor.ModelName} · 普通处理器建议：{conventionalSuggestion}",
                        context.Instance);
                    _launchRight?.AppendLog(
                        "实验性 AI 修复已启用；普通错误处理器建议 " + conventionalSuggestion +
                        "，仅转交 AI 判断，不会直接执行。");
                });
                IReadOnlyList<MinecraftModMetadata> installedMods = await Task.Run(
                        () => MinecraftModMetadataReader.ReadDirectory(Path.Combine(gameDirectory, "mods")),
                        cancellationToken)
                    .ConfigureAwait(false);
                MinecraftVersionJsonInfo currentVersion = MinecraftVersionJsonInspector.Read(context.Instance);
                string currentLoader = ResolveCommunityLoader(context.Instance, installedMods);
                MinecraftAiRepairContext modelContext = new(
                    currentVersion.MinecraftVersionId,
                    currentLoader,
                    context.JavaMajorVersion,
                    context.MemoryMegabytes,
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    installedMods.Count,
                    crashLines.Count,
                    dependencies.Select(dependency => dependency.ModId)
                        .Where(modId => !string.IsNullOrWhiteSpace(modId))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    context.ProcessExitCode);
                MinecraftRepairActionKind[] candidateActions = fault.AllowedActions
                    .Where(action => action != MinecraftRepairActionKind.ReextractNatives ||
                                     !string.IsNullOrWhiteSpace(context.NativesDirectory))
                    .Where(action => action != MinecraftRepairActionKind.InstallMissingModDependencies ||
                                     dependencies.Count > 0)
                    .Concat(IsAutomaticallyExecutableRepair(conventionalAction)
                        ? [conventionalAction]
                        : [])
                    .Concat(dependencies.Count > 0
                        ? [
                            MinecraftRepairActionKind.InstallMissingModDependencies,
                            MinecraftRepairActionKind.DownloadMod
                        ]
                        : [])
                    .Distinct()
                    .ToArray();
                if (candidateActions.Length == 0)
                    candidateActions = [MinecraftRepairActionKind.InspectOnly];
                string[] modelCrashLines = crashLines
                    .Select(line => RedactMinecraftAiContext(line, context, gameDirectory))
                    .ToArray();
                string? failedRepairFeedback = session.Attempt == MinecraftRepairAttempt.None
                    ? null
                    : BuildFailedRepairFeedback(session, fault, context.ProcessExitCode);
                MinecraftLaunchFaultReport modelFault = fault with
                {
                    AllowedActions = candidateActions,
                    Message = RedactMinecraftAiContext(
                        string.IsNullOrWhiteSpace(failedRepairFeedback)
                            ? fault.Message
                            : failedRepairFeedback + Environment.NewLine + "本次错误：" + fault.Message,
                        context,
                        gameDirectory),
                    StackTrace = RedactMinecraftAiContext(fault.StackTrace, context, gameDirectory),
                    Evidence = fault.Evidence
                        .Concat(IsAutomaticallyExecutableRepair(conventionalAction)
                            ?
                            [
                                "ConventionalHandlerSuggestion=" + conventionalAction,
                                "ConventionalHandlerSuggestionStatus=AdvisoryOnlyNotExecuted",
                                "Instruction=实验性 AI 修复已启用；普通错误处理器的动作仅作为建议，请结合完整上下文决定是否采用。"
                            ]
                            : [])
                        .Concat(string.IsNullOrWhiteSpace(failedRepairFeedback)
                            ? []
                            :
                            [
                                "PreviousRepairOutcome=FailedAfterRestart",
                                "PreviousRepair=" + session.LastRepairSummary,
                                "Instruction=上次修复已执行但重新启动仍失败；请结合新错误重新判断，不要无依据重复同一修复。"
                            ])
                        .Select(line => RedactMinecraftAiContext(line, context, gameDirectory))
                        .ToArray()
                };
                try
                {
                    int providerValue = context.Settings.GetIntegerOption(
                        LauncherSettingKeys.ExperimentalMinecraftAiProvider,
                        LauncherSettingDefaults.GetInteger(LauncherSettingKeys.ExperimentalMinecraftAiProvider.Value));
                    MinecraftAiProvider provider = Enum.IsDefined(typeof(MinecraftAiProvider), providerValue)
                        ? (MinecraftAiProvider)providerValue
                        : MinecraftAiProvider.Local;
                    int reasoningValue = context.Settings.GetIntegerOption(
                        LauncherSettingKeys.ExperimentalMinecraftAiReasoningEffort,
                        LauncherSettingDefaults.GetInteger(
                            LauncherSettingKeys.ExperimentalMinecraftAiReasoningEffort.Value));
                    MinecraftAiReasoningEffort reasoningEffort =
                        Enum.IsDefined(typeof(MinecraftAiReasoningEffort), reasoningValue)
                            ? (MinecraftAiReasoningEffort)reasoningValue
                            : MinecraftAiReasoningEffort.Medium;
                    string? apiKey = provider == MinecraftAiProvider.OpenAiCompatible
                        ? await MinecraftAiApiCredentialStore.ReadAsync(cancellationToken).ConfigureAwait(false)
                        : null;
                    int localModelValue = context.Settings.GetIntegerOption(
                        LauncherSettingKeys.ExperimentalMinecraftAiLocalModel,
                        LauncherSettingDefaults.GetInteger(
                            LauncherSettingKeys.ExperimentalMinecraftAiLocalModel.Value));
                    MinecraftAiLocalModel localModel = Enum.IsDefined(typeof(MinecraftAiLocalModel), localModelValue)
                        ? (MinecraftAiLocalModel)localModelValue
                        : MinecraftAiLocalModel.Gemma4E2B;
                    int tokenBudget = context.Settings.GetIntegerOption(
                        LauncherSettingKeys.ExperimentalMinecraftAiTokenBudget,
                        LauncherSettingDefaults.GetInteger(
                            LauncherSettingKeys.ExperimentalMinecraftAiTokenBudget.Value));
                    int downloadThreadLimit = Math.Clamp(
                        context.Settings.GetIntegerOption(
                            LauncherSettingKeys.ToolDownloadThread,
                            LauncherSettingDefaults.GetInteger(LauncherSettingKeys.ToolDownloadThread.Value)) + 1,
                        1,
                        32);
                    MinecraftAiModelOptions modelOptions = new(
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiModelPath,
                            LauncherSettingDefaults.GetText(LauncherSettingKeys.ExperimentalMinecraftAiModelPath.Value)),
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiModelSha256,
                            LauncherSettingDefaults.GetText(LauncherSettingKeys.ExperimentalMinecraftAiModelSha256.Value)),
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiRuntimePath,
                            LauncherSettingDefaults.GetText(LauncherSettingKeys.ExperimentalMinecraftAiRuntimePath.Value)),
                        provider,
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiApiBaseUrl,
                            LauncherSettingDefaults.GetText(
                                LauncherSettingKeys.ExperimentalMinecraftAiApiBaseUrl.Value)),
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiApiModel,
                            LauncherSettingDefaults.GetText(LauncherSettingKeys.ExperimentalMinecraftAiApiModel.Value)),
                        apiKey,
                        reasoningEffort,
                        localModel,
                        MinecraftAiRepairAdvisor.NormalizeTokenBudget(tokenBudget),
                        downloadThreadLimit);
                    Task<MinecraftAiRepairSuggestion?> RequestSuggestionAsync(
                        string? followUp,
                        MinecraftAiRepairSuggestion? previous) =>
                        _minecraftAiRepairAdvisor.AdviseAsync(
                            modelFault,
                            modelCrashLines,
                            installedMods,
                            modelContext,
                            (scopes, token) => BuildMinecraftAiDetailedContextAsync(
                                context,
                                gameDirectory,
                                crashLines,
                                installedMods,
                                scopes,
                                modelOptions.Provider == MinecraftAiProvider.OpenAiCompatible ? 48_000 : 14_000,
                                token),
                            AvaloniaLocalizationManager.CurrentLanguageCode,
                            modelOptions,
                            progress => Dispatcher.UIThread.Post(() =>
                            {
                                context.LaunchPage.ShowRepairWorkflow(
                                    AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                                    progress.Stage,
                                    progress.Progress,
                                    progress.Detail,
                                    context.Instance);
                                _launchRight?.AppendLog(
                                    "Minecraft 错误修复模型：" + progress.Stage +
                                    (string.IsNullOrWhiteSpace(progress.Detail) ? string.Empty : " · " + progress.Detail));
                            }, DispatcherPriority.Background),
                            cancellationToken,
                            summaryOnly: session.Attempt == MinecraftRepairAttempt.ModelApplied,
                            userRequestProvider: PromptMinecraftAiRequestAsync,
                            userFollowUp: followUp,
                            previousSuggestion: previous);

                    string? userFollowUp = null;
                    MinecraftAiRepairSuggestion? previousSuggestion = null;
                    while (true)
                    {
                        MinecraftAiRepairSuggestion? aiSuggestion = await RequestSuggestionAsync(
                                userFollowUp,
                                previousSuggestion)
                            .ConfigureAwait(false);
                        userFollowUp = null;
                        previousSuggestion = null;
                        if (aiSuggestion is null)
                            break;
                        analysisMarkdown = string.IsNullOrWhiteSpace(aiSuggestion.AnalysisMarkdown)
                            ? analysisMarkdown
                            : aiSuggestion.AnalysisMarkdown;
                        session.LastModelAnalysis = analysisMarkdown;
                        aiProducedDiagnosis = !string.IsNullOrWhiteSpace(aiSuggestion.AnalysisMarkdown);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            context.LaunchPage.ShowRepairWorkflow(
                                AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                                aiSuggestion.Stage,
                                Math.Max(0.94d, aiSuggestion.Progress),
                                aiSuggestion.NoAbility ? "AI 已完成错误总结" : $"{aiSuggestion.RepairSteps.Count} 个修复步骤",
                                context.Instance);
                                _launchRight?.AppendLog(
                                    aiSuggestion.NoAbility
                                        ? "Minecraft 错误修复模型：没有安全修复能力，已开始总结错误。"
                                        : $"Minecraft 错误修复模型：生成 {aiSuggestion.RepairSteps.Count} 个链式修复步骤；可信度={aiSuggestion.Confidence:P0}.");
                        });
                        if (aiSuggestion.NoAbility)
                        {
                            string? followUp = await PromptMinecraftAiNoAbilityFollowUpAsync(
                                    aiSuggestion,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(followUp))
                            {
                                previousSuggestion = aiSuggestion;
                                userFollowUp = followUp;
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                    _launchRight?.AppendLog("用户在 noability 后补充信息，模型将继续分析。"));
                                continue;
                            }
                            repair = new MinecraftRepairExecutionResult("AI 已完成错误总结，但没有安全可执行的修复动作。", true);
                            break;
                        }
                        if (!context.Settings.AutomaticallyRepairGameIssues ||
                            !aiSuggestion.RepairSteps.All(step => IsAutomaticallyExecutableRepair(step.Action)))
                        {
                            break;
                        }

                        MinecraftAiRepairDecision decision = await ConfirmAiRepairActionAsync(
                                aiSuggestion,
                                cancellationToken)
                            .ConfigureAwait(false);
                        while (decision == MinecraftAiRepairDecision.Question)
                        {
                            string? challenge = await PromptMinecraftAiChallengeAsync(aiSuggestion, cancellationToken)
                                .ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(challenge))
                            {
                                previousSuggestion = aiSuggestion;
                                userFollowUp = challenge;
                                break;
                            }
                            decision = await ConfirmAiRepairActionAsync(aiSuggestion, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        if (!string.IsNullOrWhiteSpace(userFollowUp))
                            continue;
                        if (decision != MinecraftAiRepairDecision.Execute)
                            break;

                        {
                            bool planMadeChanges = false;
                            List<string> completedMessages = [];
                            List<string> completedActions = [];
                            for (int stepIndex = 0; stepIndex < aiSuggestion.RepairSteps.Count; stepIndex++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                MinecraftAiRepairStep step = aiSuggestion.RepairSteps[stepIndex];
                                double planProgress = 0.94d + (0.05d * stepIndex / aiSuggestion.RepairSteps.Count);
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    context.LaunchPage.ShowRepairWorkflow(
                                        AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                                        $"{step.Stage} ({stepIndex + 1}/{aiSuggestion.RepairSteps.Count})",
                                        planProgress,
                                        step.Action.ToString(),
                                        context.Instance);
                                    _launchRight?.AppendLog(
                                        $"模型链式修复 {stepIndex + 1}/{aiSuggestion.RepairSteps.Count}：{step.Action}" +
                                        (string.IsNullOrWhiteSpace(step.Rationale) ? string.Empty : " · " + step.Rationale));
                                });
                                MinecraftAiRepairSuggestion stepSuggestion = new(
                                    step.Action,
                                    aiSuggestion.AnalysisMarkdown,
                                    aiSuggestion.Confidence,
                                    step.Stage,
                                    step.Progress,
                                    step.Parameters);
                                repair = await ExecuteMinecraftRepairAsync(
                                        context,
                                        fault,
                                        step.Action,
                                        dependencies,
                                        gameDirectory,
                                        stepSuggestion,
                                        session.Transaction,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                if (repair.IsFailure)
                                    break;
                                planMadeChanges |= repair.MadeChanges;
                                completedActions.Add(step.Action.ToString());
                                completedMessages.Add(repair.Message);
                            }
                            if (!repair.IsFailure && planMadeChanges)
                            {
                                repair = new MinecraftRepairExecutionResult(
                                    string.Join(" ", completedMessages),
                                    false,
                                    true);
                            }
                            if (!repair.IsFailure && repair.MadeChanges)
                            {
                                session.Attempt = MinecraftRepairAttempt.ModelApplied;
                                session.LastRepairSummary = BuildRepairAttemptSummary(
                                    "AI 链式修复",
                                    string.Join(" -> ", completedActions),
                                    repair);
                                await RestartMinecraftAfterRepairAsync(context, session, repair.Message, cancellationToken)
                                    .ConfigureAwait(false);
                                return;
                            }
                            if (!repair.IsFailure)
                            {
                                DesktopFileLog.Info(
                                    "MinecraftRepairAI",
                                    "模型修复计划执行完成但没有产生任何改动，不会重新启动 Minecraft。");
                                await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.AppendLog(
                                    "模型修复没有产生改动，已停止自动重启。"));
                            }
                        }
                        break;
                    }
                }
                catch (Exception aiException)
                    when (aiException is not OperationCanceledException)
                {
                    DesktopFileLog.Warn("MinecraftRepairAI", "AI 修复模型分析失败，将保留常规分析结果。", aiException);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        _launchRight?.AppendLog("AI 修复模型分析失败，已回退常规分析器：" + aiException.Message));
                }
            }
            repair = new MinecraftRepairExecutionResult(
                aiProducedDiagnosis
                    ? "AI 已完成错误诊断，但没有建议执行可能破坏游戏文件的自动修改。请根据上方 AI 分析检查模组、资源包和日志。"
                    : aiEnabled
                        ? "AI 修复模型未能返回有效诊断或安全可执行的修复计划。"
                        : "常规分析器未能解决错误，且 AI 修复模型功能未启用。",
                true);
            await FinishFailedRepairAsync(
                    context,
                    session,
                    fault,
                    analysisMarkdown,
                    repair.Message,
                    gameDirectory,
                    crashLines)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _minecraftAiRepairAdvisor.StopLocalServerAsync().ConfigureAwait(false);
            await session.Transaction.RollbackAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (context.LaunchPage.IsLaunchInProgress)
                    context.LaunchPage.PageChangeToLogin();
                _launchRight?.AppendLog(
                    session.Transaction.HasChanges
                        ? "Minecraft 修复已取消，本轮更改已回滚。"
                        : "Minecraft 错误分析已取消，本轮未修改任何文件。");
            });
        }
        catch (Exception ex)
        {
            fault ??= MinecraftLaunchFaultAnalyzer.Analyze(ex, "CrashAnalyzer");
            string failure = "崩溃分析或自动修复失败：" + ex.Message;
            await FinishFailedRepairAsync(
                    context,
                    session,
                    fault,
                    string.IsNullOrWhiteSpace(analysisMarkdown)
                        ? BuildConventionalCrashAnalysis(fault, [])
                        : analysisMarkdown,
                    failure,
                    gameDirectory,
                    crashLines)
                .ConfigureAwait(false);
        }
    }

    private static MinecraftRepairActionKind SelectConventionalRepairAction(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<MinecraftMissingDependency> dependencies,
        string? nativesDirectory) =>
        MinecraftRepairPolicy.SelectConventionalRepairAction(fault, dependencies, nativesDirectory);

    private async Task<MinecraftRepairExecutionResult> ExecuteMinecraftRepairAsync(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        MinecraftRepairActionKind action,
        IReadOnlyList<MinecraftMissingDependency> dependencies,
        string gameDirectory,
        MinecraftAiRepairSuggestion? suggestion,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        return action switch
        {
            MinecraftRepairActionKind.RepairVersionFiles =>
                await RepairVersionFilesAfterFaultAsync(context, fault, transaction, cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.ReextractNatives when !string.IsNullOrWhiteSpace(context.NativesDirectory) =>
                await ReextractNativesAfterFaultAsync(context, fault, transaction, cancellationToken).ConfigureAwait(false),
            MinecraftRepairActionKind.InstallMissingModDependencies when dependencies.Count > 0 =>
                await RepairMissingDependenciesAfterFaultAsync(
                        context,
                        dependencies,
                        gameDirectory,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.DownloadMod when suggestion?.Parameters.ModId is { } modId =>
                await RepairRequestedModAsync(
                        context,
                        gameDirectory,
                        modId,
                        suggestion.Parameters.ModVersion,
                        updateExisting: false,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.UpdateMod when suggestion?.Parameters.ModId is { } updateModId =>
                await RepairRequestedModAsync(
                        context,
                        gameDirectory,
                        updateModId,
                        suggestion.Parameters.ModVersion,
                        updateExisting: true,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.DisableMod when suggestion?.Parameters.ModId is { } disableModId =>
                await DisableRequestedModAsync(
                        gameDirectory,
                        disableModId,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.DisableExperimentalJvmHost =>
                DisableExperimentalJvmHost(context),
            MinecraftRepairActionKind.SelectCompatibleJava =>
                await SelectCompatibleJavaAfterFaultAsync(context, transaction, cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.DownloadCompatibleJava =>
                await DownloadCompatibleJavaAfterFaultAsync(context, transaction, cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader =>
                await ReinstallVersionAndUpdateLoaderAsync(
                        context,
                        suggestion,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => new MinecraftRepairExecutionResult(
                "常规错误分析器没有找到可安全自动执行的修复；请查看分析内容和日志。",
                true)
        };
    }

    private static MinecraftRepairExecutionResult DisableExperimentalJvmHost(RunningGameContext context)
    {
        bool enabled = context.Settings.GetBooleanOption(
            LauncherSettingKeys.ExperimentalJvmLifecycleHost,
            LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalJvmLifecycleHost.Value));
        if (!enabled)
        {
            return new MinecraftRepairExecutionResult(
                "实验性 Jvm.NET Host 已处于关闭状态，没有需要修改的设置。",
                false,
                false);
        }
        context.Settings.SetBooleanOption(LauncherSettingKeys.ExperimentalJvmLifecycleHost, false);
        LauncherSettingsPageBinder.UpdateSettings(persisted =>
        {
            persisted.SetBooleanOption(LauncherSettingKeys.ExperimentalJvmLifecycleHost, false);
            return persisted;
        });
        return new MinecraftRepairExecutionResult(
            "已关闭实验性 Jvm.NET Host；下次启动将使用传统 Java 进程。",
            false,
            true);
    }

    private async Task<MinecraftRepairExecutionResult> RepairVersionFilesAfterFaultAsync(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复"),
                0.35d);
            _launchRight?.AppendLog($"自动修复：{fault.Code}，开始校验并补全版本文件。");
        });
        int changedFiles = 0;
        await _minecraftInstallService.RepairAsync(
                new MinecraftRepairRequest
                {
                    VersionId = context.Instance.Name,
                    VersionJsonPath = context.Instance.VersionJsonPath,
                    MinecraftRootDirectory = MinecraftLaunchPlanFactory.GetMinecraftRootFromInstance(context.Instance),
                    InstanceDirectory = context.Instance.InstanceDirectory,
                    PreferOfficialSource = context.Settings.DownloadSource != DownloadSourcePreference.MirrorOnly,
                    BeforeFileChangeAsync = async (path, token) =>
                        await transaction.BackupFileAsync(path, token).ConfigureAwait(false),
                    FileChanged = _ => Interlocked.Increment(ref changedFiles)
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new MinecraftRepairExecutionResult(
            changedFiles > 0
                ? $"自动修复完成：已补全或替换 {changedFiles} 个版本文件，请重新启动游戏。"
                : "版本文件校验完成，没有发现需要修改的文件。",
            false,
            changedFiles > 0);
    }

    private async Task<MinecraftRepairExecutionResult> ReextractNativesAfterFaultAsync(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        string nativesDirectory = context.NativesDirectory!;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复"),
                0.5d);
            _launchRight?.AppendLog($"自动修复：{fault.Code}，准备重新解压 Natives。");
        });
        cancellationToken.ThrowIfCancellationRequested();
        bool existed = Directory.Exists(nativesDirectory);
        transaction.BackupDirectoryByMove(nativesDirectory);
        return new MinecraftRepairExecutionResult(
            existed
                ? "自动修复完成：旧 Natives 已清理，下次启动会重新解压。"
                : "Natives 目录不存在，没有可重新提取的文件。",
            false,
            existed);
    }

    private async Task<MinecraftRepairExecutionResult> RepairMissingDependenciesAfterFaultAsync(
        RunningGameContext context,
        IReadOnlyList<MinecraftMissingDependency> dependencies,
        string gameDirectory,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复"),
                0.32d);
            _launchRight?.AppendLog($"自动修复：发现 {dependencies.Count} 个缺失前置模组。");
        });
        string modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        string gameVersion = MinecraftVersionJsonInspector.Read(context.Instance).MinecraftVersionId;
        string loader = ResolveCommunityLoader(
            context.Instance,
            MinecraftModMetadataReader.ReadDirectory(modsDirectory));
        int repaired = 0;
        int changed = 0;
        using CompositeCommunityResourceCatalog catalog = new();
        ICommunityArtifactDownloader downloader = CommunityOnlineProviderRegistry.CreateArtifactDownloader();
        for (int index = 0; index < dependencies.Count; index++)
        {
            MinecraftMissingDependency dependency = dependencies[index];
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
                context.LaunchPage.UpdateRepairStep(index + 1, dependencies.Count));
            ModDownloadResult result = await DownloadMissingDependencyAsync(
                    catalog,
                    downloader,
                    dependency,
                    gameVersion,
                    loader,
                    modsDirectory,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
                repaired++;
            if (result.Changed)
                changed++;
        }
        return new MinecraftRepairExecutionResult(
            repaired == dependencies.Count
                ? $"自动修复完成：已安装 {repaired} 个前置模组，请重新启动游戏。"
                : $"自动修复完成：已安装 {repaired}/{dependencies.Count} 个前置模组。",
            repaired != dependencies.Count,
            changed > 0);
    }

    private static async Task<MinecraftRepairExecutionResult> RepairRequestedModAsync(
        RunningGameContext context,
        string gameDirectory,
        string modId,
        string? requestedVersion,
        bool updateExisting,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        string modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        IReadOnlyList<MinecraftModMetadata> installed = MinecraftModMetadataReader.ReadDirectory(modsDirectory);
        MinecraftModMetadata? current = installed.FirstOrDefault(mod =>
            string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (updateExisting && current is null)
            return new MinecraftRepairExecutionResult($"未找到要更新的已安装模组：{modId}。", true);

        string gameVersion = MinecraftVersionJsonInspector.Read(context.Instance).MinecraftVersionId;
        string loader = ResolveCommunityLoader(context.Instance, installed);
        CommunitySearchOptions options = new(
            CommunityResourceSort.Relevance,
            GameVersion: gameVersion,
            Loader: loader,
            Source: CommunityResourceSource.All);
        using CompositeCommunityResourceCatalog catalog = new();
        IReadOnlyList<CommunityResourceEntry> projects = await catalog.SearchAsync(
                CommunityResourceCategory.Mod,
                modId,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        CommunityResourceEntry? project = projects
            .OrderBy(entry => string.Equals(entry.ProjectId, modId, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(entry.Slug, modId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(static entry => entry.Downloads)
            .FirstOrDefault();
        if (project is null)
            return new MinecraftRepairExecutionResult($"社区资源中未找到模组：{modId}。", true);

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
                project,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        CommunityResourceVersion? version = string.IsNullOrWhiteSpace(requestedVersion)
            ? versions.OrderByDescending(static item => item.PublishedAt).FirstOrDefault()
            : versions.FirstOrDefault(item =>
                string.Equals(item.VersionId, requestedVersion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.VersionNumber, requestedVersion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, requestedVersion, StringComparison.OrdinalIgnoreCase));
        if (version is null)
            return new MinecraftRepairExecutionResult(
                $"未找到 {project.DisplayTitle} 的目标版本 {requestedVersion ?? "(最新兼容版)"}。",
                true);
        CommunityResourceDownloadFile? file = version.Files.FirstOrDefault(candidate =>
                                                  candidate.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                                              ?? (version.Files.Count > 0 ? version.Files[0] : null);
        if (file is null)
            return new MinecraftRepairExecutionResult("目标模组版本没有可下载文件。", true);

        if (current is not null &&
            File.Exists(current.FilePath) &&
            string.Equals(current.Version, version.VersionNumber, StringComparison.OrdinalIgnoreCase))
        {
            return new MinecraftRepairExecutionResult(
                $"{project.DisplayTitle} 已经是目标版本 {version.VersionNumber}，没有需要修改的文件。",
                false,
                false);
        }

        await Dispatcher.UIThread.InvokeAsync(() => context.LaunchPage.ShowRepairWorkflow(
            AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
            updateExisting ? "正在更新模组" : "正在下载模组",
            0.96d,
            project.DisplayTitle,
            context.Instance));
        string targetPath = Path.Combine(modsDirectory, DesktopPathHelpers.SanitizeFileName(file.FileName));
        await transaction.BackupFileAsync(targetPath, cancellationToken).ConfigureAwait(false);
        foreach (MinecraftModMetadata conflict in installed.Where(mod =>
                     string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase) &&
                     !mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
        {
            string disabledPath = CreateDisabledModPath(conflict.FilePath);
            await transaction.BackupFileAsync(conflict.FilePath, cancellationToken).ConfigureAwait(false);
            await transaction.BackupFileAsync(disabledPath, cancellationToken).ConfigureAwait(false);
            File.Move(conflict.FilePath, disabledPath);
        }

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".PCLDownloading";
        try
        {
            ICommunityArtifactDownloader downloader = CommunityOnlineProviderRegistry.CreateArtifactDownloader();
            await downloader.DownloadAsync(
                    file.CandidateUrls,
                    temporaryPath,
                    static (_, _) => { },
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return new MinecraftRepairExecutionResult(
            updateExisting
                ? $"已将 {project.DisplayTitle} 更新至 {version.VersionNumber}。"
                : $"已下载 {project.DisplayTitle} {version.VersionNumber}。",
            false,
            true);
    }

    private static async Task<MinecraftRepairExecutionResult> DisableRequestedModAsync(
        string gameDirectory,
        string modId,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        string modsDirectory = Path.Combine(gameDirectory, "mods");
        MinecraftModMetadata? metadata = MinecraftModMetadataReader.ReadDirectory(modsDirectory)
            .FirstOrDefault(mod => string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase) &&
                                   !mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
        if (metadata is null)
            return new MinecraftRepairExecutionResult($"未找到可禁用的模组：{modId}。", true);
        string disabledPath = CreateDisabledModPath(metadata.FilePath);
        await transaction.BackupFileAsync(metadata.FilePath, cancellationToken).ConfigureAwait(false);
        await transaction.BackupFileAsync(disabledPath, cancellationToken).ConfigureAwait(false);
        File.Move(metadata.FilePath, disabledPath);
        return new MinecraftRepairExecutionResult(
            $"已禁用模组 {metadata.Name}（{metadata.Id}），将尝试重新启动。",
            false,
            true);
    }

    private static string CreateDisabledModPath(string path)
    {
        string candidate = path + ".disabled";
        for (int index = 2; File.Exists(candidate); index++)
            candidate = path + "." + index.ToString(CultureInfo.InvariantCulture) + ".disabled";
        return candidate;
    }

    private static string ResolveCommunityLoader(
        LaunchInstanceInfo instance,
        IReadOnlyList<MinecraftModMetadata> installedMods)
    {
        string? metadataLoader = installedMods.Select(static mod => mod.Loader)
            .FirstOrDefault(loader => !string.Equals(loader, "unknown", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(metadataLoader))
            return metadataLoader;
        IReadOnlyList<string> libraries = MinecraftVersionJsonInspector.Read(instance).LoaderEntries;
        if (libraries.Any(library => library.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase)))
            return "quilt";
        if (libraries.Any(library => library.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)))
            return "fabric";
        if (libraries.Any(library => library.Contains("neoforged", StringComparison.OrdinalIgnoreCase)))
            return "neoforge";
        return "forge";
    }

    private static async Task<MinecraftRepairExecutionResult> SelectCompatibleJavaAfterFaultAsync(
        RunningGameContext context,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        MinecraftLaunchProfile profile = MinecraftLaunchCoordinator.BuildLaunchProfile(context.Instance);
        JavaRequirementResolution requirement = JavaRuntimeRequirementResolver.Resolve(profile);
        if (!requirement.Success)
            return new MinecraftRepairExecutionResult(requirement.Detail ?? "无法解析 Java 要求。", true);
        InstanceMetadata current = await InstanceMetadataStore.LoadAsync(
                context.Instance.InstanceDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<PCL.Domain.Minecraft.Java.JavaRuntimeCandidate> candidates =
            await JavaRuntimeCatalog.LoadAsync(context.Settings, cancellationToken).ConfigureAwait(false);
        PCL.Domain.Minecraft.Java.JavaRuntimeCandidate? best = JavaRuntimeCatalog.SelectBest(
            candidates.Where(candidate => !string.Equals(
                candidate.Installation.JavaExecutablePath,
                current.SelectedJavaPath,
                StringComparison.OrdinalIgnoreCase)),
            requirement.Range);
        if (best is null)
            return new MinecraftRepairExecutionResult("没有找到另一套兼容且已启用的 Java。", true);
        await transaction.BackupFileAsync(
                InstanceMetadataStore.GetMetadataPath(context.Instance.InstanceDirectory),
                cancellationToken)
            .ConfigureAwait(false);
        await InstanceMetadataStore.UpdateAsync(
                context.Instance.InstanceDirectory,
                metadata => metadata with
                {
                    JavaSelectionMode = 2,
                    SelectedJavaPath = best.Installation.JavaExecutablePath
                },
                cancellationToken)
            .ConfigureAwait(false);
        return new MinecraftRepairExecutionResult(
            $"已切换至 Java {best.Installation.MajorVersion}：{best.Installation.JavaExecutablePath}",
            false,
            true);
    }

    private static async Task<MinecraftRepairExecutionResult> DownloadCompatibleJavaAfterFaultAsync(
        RunningGameContext context,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        MinecraftLaunchProfile profile = MinecraftLaunchCoordinator.BuildLaunchProfile(context.Instance);
        JavaRequirementResolution requirement = JavaRuntimeRequirementResolver.Resolve(profile);
        if (!requirement.Success)
            return new MinecraftRepairExecutionResult(requirement.Detail ?? "无法解析 Java 要求。", true);
        JavaRuntimeAcquisitionDecision acquisition = JavaRuntimeAcquisitionPlanner.Plan(requirement, profile.HasForge);
        if (!acquisition.CanAutoDownload || string.IsNullOrWhiteSpace(acquisition.DownloadComponent))
            return new MinecraftRepairExecutionResult("该版本的 Java 要求不能由启动器安全自动下载。", true);
        string runtimeRoot = Path.Combine(
            PCL.Desktop.Paths.LauncherPathLayout.ResolveDataDirectory(),
            "runtime");
        HashSet<string> existingRuntimeDirectories = Directory.Exists(runtimeRoot)
            ? Directory.EnumerateDirectories(runtimeRoot).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using HttpJavaRuntimeMetadataProvider metadataProvider = new();
        JavaRuntimeInstaller installer = new(metadataProvider);
        Progress<JavaRuntimeInstallProgress> progress = new(update => Dispatcher.UIThread.Post(() =>
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                update.Stage,
                0.94d + (update.Progress * 0.05d),
                update.Detail,
                context.Instance)));
        string javaPath = await installer.InstallAsync(
                acquisition.DownloadComponent,
                runtimeRoot,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        string? installedRuntimeDirectory = FindTopLevelDirectory(runtimeRoot, javaPath);
        if (installedRuntimeDirectory is not null && !existingRuntimeDirectories.Contains(installedRuntimeDirectory))
            transaction.TrackCreatedDirectory(installedRuntimeDirectory);
        await transaction.BackupFileAsync(
                InstanceMetadataStore.GetMetadataPath(context.Instance.InstanceDirectory),
                cancellationToken)
            .ConfigureAwait(false);
        await InstanceMetadataStore.UpdateAsync(
                context.Instance.InstanceDirectory,
                metadata => metadata with { JavaSelectionMode = 2, SelectedJavaPath = javaPath },
                cancellationToken)
            .ConfigureAwait(false);
        return new MinecraftRepairExecutionResult(
            $"已下载并选择兼容 Java：{javaPath}",
            false,
            true);
    }

    private static string? FindTopLevelDirectory(string rootDirectory, string childPath)
    {
        string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetFullPath(childPath);
        if (!current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        string relative = Path.GetRelativePath(root, current);
        string? first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : Path.Combine(root, first);
    }

    private async Task<MinecraftRepairExecutionResult> ReinstallVersionAndUpdateLoaderAsync(
        RunningGameContext context,
        MinecraftAiRepairSuggestion? suggestion,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        MinecraftVersionJsonInfo info = MinecraftVersionJsonInspector.Read(context.Instance);
        (MinecraftLoaderKind Kind, string Version)? loader = DetectInstalledLoader(info.LoaderEntries);
        if (loader is null || string.Equals(context.Instance.Name, info.MinecraftVersionId, StringComparison.OrdinalIgnoreCase))
            return new MinecraftRepairExecutionResult("当前版本没有可安全原位更新的模组加载器。", true);
        MinecraftLoaderMetadataService metadataService = new();
        IReadOnlyList<MinecraftLoaderVersionEntry> candidates = await metadataService.GetLoaderVersionsAsync(
                loader.Value.Kind,
                info.MinecraftVersionId,
                cancellationToken)
            .ConfigureAwait(false);
        string? requested = suggestion?.Parameters.LoaderVersion;
        MinecraftLoaderVersionEntry? target = !string.IsNullOrWhiteSpace(requested)
            ? candidates.FirstOrDefault(candidate => string.Equals(candidate.Version, requested, StringComparison.OrdinalIgnoreCase))
            : candidates.FirstOrDefault(static candidate => candidate.Stable) ??
              (candidates.Count > 0 ? candidates[0] : null);
        if (target is null)
            return new MinecraftRepairExecutionResult("未找到兼容的加载器更新版本。", true);
        IReadOnlyList<MinecraftVersionManifestEntry> manifest = await _minecraftInstallService.GetVersionManifestAsync(
                context.Settings.DownloadSource != DownloadSourcePreference.MirrorOnly,
                cancellationToken)
            .ConfigureAwait(false);
        MinecraftVersionManifestEntry? vanilla = manifest.FirstOrDefault(entry =>
            string.Equals(entry.Id, info.MinecraftVersionId, StringComparison.OrdinalIgnoreCase));
        if (vanilla is null)
            return new MinecraftRepairExecutionResult("无法取得基础 Minecraft 版本元数据。", true);

        await transaction.BackupFileAsync(context.Instance.VersionJsonPath, cancellationToken).ConfigureAwait(false);
        await transaction.BackupFileAsync(
                Path.Combine(context.Instance.InstanceDirectory, context.Instance.Name + ".jar"),
                cancellationToken)
            .ConfigureAwait(false);
        await _minecraftInstallService.InstallAsync(
                new MinecraftInstallRequest
                {
                    VersionId = context.Instance.Name,
                    BaseVersionId = info.MinecraftVersionId,
                    VersionJsonUrl = vanilla.Url,
                    MinecraftRootDirectory = MinecraftLaunchPlanFactory.GetMinecraftRootFromInstance(context.Instance),
                    PreferOfficialSource = context.Settings.DownloadSource != DownloadSourcePreference.MirrorOnly,
                    Loader = new MinecraftLoaderInstallRequest(loader.Value.Kind, target.Version),
                    ReplaceExistingVersion = true,
                    JavaExecutablePath = MinecraftLaunchPlanFactory.ResolvePreferredJavaExecutablePath(forceConsole: true)
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new MinecraftRepairExecutionResult(
            $"已重新安装版本，并将 {loader.Value.Kind} 从 {loader.Value.Version} 更新至 {target.Version}。",
            false,
            true);
    }

    private static (MinecraftLoaderKind Kind, string Version)? DetectInstalledLoader(IReadOnlyList<string> libraries)
    {
        (MinecraftLoaderKind Kind, string[] Needles)[] candidates =
        [
            (MinecraftLoaderKind.NeoForge, ["net.neoforged:neoforge", "net.neoforged:forge"]),
            (MinecraftLoaderKind.Forge, ["net.minecraftforge:forge"]),
            (MinecraftLoaderKind.Quilt, ["quilt-loader"]),
            (MinecraftLoaderKind.LegacyFabric, ["legacyfabric", "legacy-fabric"]),
            (MinecraftLoaderKind.Fabric, ["fabric-loader"]),
            (MinecraftLoaderKind.Cleanroom, ["cleanroom"])
        ];
        foreach ((MinecraftLoaderKind kind, string[] needles) in candidates)
        {
            string? version = MinecraftLoaderLibraryDetector.DetectVersion(libraries, needles);
            if (!string.IsNullOrWhiteSpace(version))
                return (kind, version);
        }
        return null;
    }

    internal static bool IsAutomaticallyExecutableRepairForTest(MinecraftRepairActionKind action) =>
        IsAutomaticallyExecutableRepair(action);

    internal static string DescribeAiRepairStepForTest(MinecraftRepairActionKind action) =>
        DescribeAiRepairStep(action, new MinecraftAiRepairParameters());

    private static bool IsAutomaticallyExecutableRepair(MinecraftRepairActionKind action) =>
        MinecraftRepairPolicy.IsAutomaticallyExecutableRepair(action);


    private static async Task<MinecraftLaunchFaultReport?> AwaitFaultReportAsync(
        Task<MinecraftLaunchFaultReport?>? faultReportTask,
        CancellationToken cancellationToken)
    {
        if (faultReportTask is null)
            return null;
        Task completed = await Task.WhenAny(
                faultReportTask,
                Task.Delay(TimeSpan.FromSeconds(1), cancellationToken))
            .ConfigureAwait(false);
        return ReferenceEquals(completed, faultReportTask)
            ? await faultReportTask.ConfigureAwait(false)
            : null;
    }

    private enum MinecraftAiRepairDecision
    {
        Execute,
        Question,
        Decline
    }

    private Task<MinecraftAiRepairDecision> ConfirmAiRepairActionAsync(
        MinecraftAiRepairSuggestion suggestion,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<MinecraftAiRepairDecision> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<MinecraftAiRepairDecision>)state!).TrySetCanceled(),
            completion);
        string target = string.Join(
            "\n",
            suggestion.RepairSteps.Select((step, index) =>
                $"{index + 1}. {DescribeAiRepairStep(step.Action, step.Parameters)}" +
                (string.IsNullOrWhiteSpace(step.Rationale) ? string.Empty : $"\n   依据：{step.Rationale}")));
        Dispatcher.UIThread.Post(() => ShowMarkdownDialog(
            AvaloniaLocalizationManager.GetText("Crash.Model.Confirm.Title", "模型请求执行修复"),
            $"AI 修复模型生成了以下链式修复计划：\n\n{target}\n\n可信度：{suggestion.Confidence:P0}\n\n" +
            "每一步都会由启动器重新验证参数并记录到同一个可回滚事务中；模型不会直接访问网络或文件。" +
            "你可以执行、质疑模型后要求它重新判断，或拒绝执行。",
            result =>
            {
                registration.Dispose();
                completion.TrySetResult(result switch
                {
                    1 => MinecraftAiRepairDecision.Execute,
                    2 => MinecraftAiRepairDecision.Question,
                    _ => MinecraftAiRepairDecision.Decline
                });
            },
            AvaloniaLocalizationManager.GetText("Crash.Model.Confirm.Execute", "执行修复"),
            AvaloniaLocalizationManager.GetText("Crash.Model.Confirm.Question", "质疑并询问"),
            AvaloniaLocalizationManager.GetText("Crash.Model.Confirm.Decline", "拒绝执行"),
            isWarn: true));
        return completion.Task;
    }

    private Task<string?> PromptMinecraftAiRequestAsync(
        MinecraftAiUserRequest request,
        CancellationToken cancellationToken)
    {
        string options = request.Options.Count == 0
            ? string.Empty
            : "\n\n模型给出的可选参考：\n" + string.Join(
                "\n",
                request.Options.Select((option, index) => $"{index + 1}. {option}"));
        return PromptMinecraftAiInputAsync(
            AvaloniaLocalizationManager.GetText("Crash.Model.Request.Title", "模型需要补充信息"),
            request.Question + options + "\n\n你也可以直接质疑问题中的前提或给出选项外答案。",
            AvaloniaLocalizationManager.GetText("Crash.Model.Request.Hint", "输入回答、补充信息或质疑…"),
            cancellationToken);
    }

    private async Task<string?> PromptMinecraftAiNoAbilityFollowUpAsync(
        MinecraftAiRepairSuggestion suggestion,
        CancellationToken cancellationToken)
    {
        int result = await PromptMinecraftAiMarkdownChoiceAsync(
                AvaloniaLocalizationManager.GetText("Crash.Model.NoAbility.Title", "模型暂时无法安全修复"),
                suggestion.AnalysisMarkdown +
                "\n\n---\n\n你可以继续补充信息、追问或质疑这个结论；模型会保留本轮结论并重新分析。",
                AvaloniaLocalizationManager.GetText("Crash.Model.NoAbility.Continue", "继续询问"),
                AvaloniaLocalizationManager.GetText("Crash.Model.NoAbility.Finish", "结束分析"),
                cancellationToken)
            .ConfigureAwait(false);
        if (result != 1)
            return null;
        return await PromptMinecraftAiInputAsync(
                AvaloniaLocalizationManager.GetText("Crash.Model.NoAbility.InputTitle", "继续询问模型"),
                "补充模型尚未掌握的信息，或说明你不同意结论的原因。",
                AvaloniaLocalizationManager.GetText("Crash.Model.NoAbility.InputHint", "输入补充、问题或质疑…"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<string?> PromptMinecraftAiChallengeAsync(
        MinecraftAiRepairSuggestion suggestion,
        CancellationToken cancellationToken) =>
        PromptMinecraftAiInputAsync(
            AvaloniaLocalizationManager.GetText("Crash.Model.Challenge.Title", "质疑模型修复计划"),
            "请指出你认为不正确的判断、缺失的信息或希望模型重新考虑的条件。\n\n" +
            "当前结论：" + suggestion.AnalysisMarkdown,
            AvaloniaLocalizationManager.GetText("Crash.Model.Challenge.Hint", "输入问题或质疑…"),
            cancellationToken);

    private Task<int> PromptMinecraftAiMarkdownChoiceAsync(
        string title,
        string markdown,
        string primaryButton,
        string secondaryButton,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(),
            completion);
        Dispatcher.UIThread.Post(() =>
        {
            if (completion.Task.IsCompleted)
                return;
            ShowMarkdownDialog(
                title,
                markdown,
                result =>
                {
                    registration.Dispose();
                    completion.TrySetResult(result);
                },
                primaryButton,
                secondaryButton,
                isWarn: true);
        });
        return completion.Task;
    }

    private Task<string?> PromptMinecraftAiInputAsync(
        string title,
        string caption,
        string hint,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<string?>)state!).TrySetCanceled(),
            completion);
        Dispatcher.UIThread.Post(() =>
        {
            if (completion.Task.IsCompleted)
                return;
            ShowInputDialog(
                title,
                caption,
                string.Empty,
                hint,
                value =>
                {
                    registration.Dispose();
                    completion.TrySetResult(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
                });
        });
        return completion.Task;
    }

    private static string DescribeAiRepairStep(
        MinecraftRepairActionKind action,
        MinecraftAiRepairParameters parameters) =>
        MinecraftRepairPolicy.DescribeAiRepairStep(action, parameters);

    private async Task RestartMinecraftAfterRepairAsync(
        RunningGameContext context,
        MinecraftRepairSession session,
        string repairMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            context.LaunchPage.ShowRepairWorkflow(
                session.Attempt == MinecraftRepairAttempt.ModelApplied
                    ? AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型")
                    : AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                AvaloniaLocalizationManager.GetText(
                    "Crash.Repair.Stage.Restart",
                    "修复完成，正在重启 Minecraft"),
                1d,
                repairMessage,
                context.Instance);
            _launchRight?.AppendLog(repairMessage + " 正在自动重启 Minecraft。");
        });
        await StartMinecraftAsync(
                context.LaunchPage,
                context.Instance,
                context.WorldName,
                context.ServerAddress,
                session)
            .ConfigureAwait(false);
    }

    private async Task FinishFailedRepairAsync(
        RunningGameContext context,
        MinecraftRepairSession session,
        MinecraftLaunchFaultReport fault,
        string analysisMarkdown,
        string failure,
        string gameDirectory,
        IReadOnlyList<string> crashLines)
    {
        string rollbackMessage;
        if (!session.Transaction.HasChanges)
        {
            rollbackMessage = "本轮仅完成错误分析，未修改任何 Minecraft 文件。";
        }
        else
        {
            try
            {
                await session.Transaction.RollbackAsync().ConfigureAwait(false);
                rollbackMessage = "本轮修复更改已回滚。";
            }
            catch (Exception rollbackException)
            {
                DesktopFileLog.Error("MinecraftRepair", "回滚 Minecraft 修复更改失败。", rollbackException);
                rollbackMessage = "回滚部分修复更改失败：" + rollbackException.Message;
            }
        }
        string[] recentCrashFiles = FindRecentCrashFiles(gameDirectory);
        string? primaryLog = recentCrashFiles.Length > 0 ? recentCrashFiles[0] : null;
        string outcome = failure + " " + rollbackMessage;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.MinecraftErrorTitle", "Minecraft 出错"),
                "自动修复未能解决问题，正在生成错误报告",
                1d,
                fault.Code.ToString(),
                context.Instance);
            _launchRight?.AppendLog(outcome);
            ShowHint(outcome, critical: true);
            ShowMinecraftCrashDialog(
                context,
                fault,
                analysisMarkdown,
                outcome,
                gameDirectory,
                primaryLog,
                crashLines);
        });
    }

    private void ShowMinecraftCrashDialog(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        string analysisMarkdown,
        string repairOutcome,
        string gameDirectory,
        string? primaryLog,
        IReadOnlyList<string> crashLines)
    {
        string markdown = $"{analysisMarkdown.Trim()}\n\n---\n\n" +
                          $"**定位节点：** `{fault.Subsystem}/{fault.Stage}`  \n" +
                          $"**错误代码：** `{fault.Code}`" +
                          (string.IsNullOrWhiteSpace(fault.LastClassName)
                              ? string.Empty
                              : $"  \n**最后关键类：** `{fault.LastClassName}`") +
                          $"\n\n### 自动处理\n\n{repairOutcome}";
        ShowMarkdownDialog(
            AvaloniaLocalizationManager.GetText("Crash.MinecraftErrorTitle", "Minecraft 出错"),
            markdown,
            result =>
            {
                try
                {
                    if (result == 2)
                        OpenExistingPath(primaryLog ?? gameDirectory);
                    else if (result == 3)
                        _ = ExportMinecraftCrashReportAsync(
                            context,
                            fault,
                            markdown,
                            gameDirectory,
                            crashLines);
                }
                finally
                {
                    if (context.LaunchPage.IsLaunchInProgress)
                        context.LaunchPage.PageChangeToLogin();
                }
            },
            AvaloniaLocalizationManager.GetText("Common.Action.Confirm", "知道了"),
            AvaloniaLocalizationManager.GetText("Crash.Action.ViewLog", "查看日志"),
            AvaloniaLocalizationManager.GetText("Crash.Action.ExportReport", "导出报告"),
            isWarn: true);
    }

    private async Task ExportMinecraftCrashReportAsync(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        string analysisMarkdown,
        string gameDirectory,
        IReadOnlyList<string> crashLines)
    {
        string suggestedName = "Minecraft-Error-Report-" +
                               DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                               ".zip";
        string? targetPath = await PickSaveFilePathAsync(
                AvaloniaLocalizationManager.GetText("Crash.Report.Export.Title", "选择错误报告保存位置"),
                suggestedName,
                new FilePickerFileType("ZIP") { Patterns = ["*.zip"] })
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PCL-N",
            "CrashReport",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            string structured =
                $"Code: {fault.Code}\nStage: {fault.Stage}\nSubsystem: {fault.Subsystem}\n" +
                $"ExceptionType: {fault.ExceptionType}\nMessage: {fault.Message}\n" +
                $"LastClassName: {fault.LastClassName}\nTimestamp: {fault.Timestamp:O}\n" +
                $"AllowedActions: {string.Join(", ", fault.AllowedActions)}\n";
            await File.WriteAllTextAsync(
                    Path.Combine(temporaryDirectory, "分析结果.md"),
                    PortableLog.Redact(analysisMarkdown),
                    Encoding.UTF8)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(temporaryDirectory, "结构化错误.txt"),
                    PortableLog.Redact(structured),
                    Encoding.UTF8)
                .ConfigureAwait(false);
            await File.WriteAllLinesAsync(
                    Path.Combine(temporaryDirectory, "已收集日志片段.txt"),
                    crashLines.Select(PortableLog.Redact),
                    Encoding.UTF8)
                .ConfigureAwait(false);

            List<string> reportFiles =
            [
                .. FindRecentCrashFiles(gameDirectory),
                context.Instance.VersionJsonPath,
                Path.Combine(gameDirectory, "LatestLaunch-PCLN.bat"),
                DesktopFileLog.CurrentLogPath
            ];
            HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (string sourcePath in reportFiles
                         .Where(File.Exists)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                FileInfo info = new(sourcePath);
                if (info.Length > 16L * 1024L * 1024L)
                    continue;
                string name = Path.GetFileName(sourcePath);
                if (!usedNames.Add(name))
                    name = Path.GetFileNameWithoutExtension(name) + "-" + usedNames.Count + Path.GetExtension(name);
                await using FileStream stream = new(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    useAsync: true);
                using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
                string content = await reader.ReadToEndAsync().ConfigureAwait(false);
                await File.WriteAllTextAsync(
                        Path.Combine(temporaryDirectory, name),
                        PortableLog.Redact(content),
                        Encoding.UTF8)
                    .ConfigureAwait(false);
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            ZipFile.CreateFromDirectory(temporaryDirectory, targetPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowHint(AvaloniaLocalizationManager.GetText("Crash.Report.Exported", "错误报告已导出"));
                OpenExistingPath(targetPath);
            });
        }
        catch (Exception ex)
        {
            DesktopFileLog.Error("CrashReport", "导出 Minecraft 错误报告失败。", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowTextDialog(
                    AvaloniaLocalizationManager.GetText("Crash.Report.Export.Failed.Title", "导出错误报告失败"),
                    ex.Message));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static string BuildConventionalCrashAnalysis(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<MinecraftMissingDependency> dependencies)
    {
        string reason = fault.Code switch
        {
            MinecraftLaunchFaultCode.JavaRuntimeMissing => "所选 Java 缺失，或无法加载 JVM 原生库。",
            MinecraftLaunchFaultCode.JavaRuntimeIncompatible => "当前 Java 版本与游戏或模组加载器要求不兼容。",
            MinecraftLaunchFaultCode.JvmInitializationFailed => "JVM 在 Minecraft 主类运行前初始化失败。",
            MinecraftLaunchFaultCode.MainClassMissing => "Minecraft 主类不存在，版本核心文件可能缺失或损坏。",
            MinecraftLaunchFaultCode.ClasspathDependencyMissing => "类路径中的游戏库缺失或损坏。",
            MinecraftLaunchFaultCode.AuthenticationFailed => "登录凭据或会话验证失败。",
            MinecraftLaunchFaultCode.SessionServiceUnavailable => "账户会话服务暂时不可用。",
            MinecraftLaunchFaultCode.NativeLibraryFailed => "LWJGL 或其他原生库加载失败。",
            MinecraftLaunchFaultCode.GraphicsInitializationFailed => "图形驱动、OpenGL/Vulkan 或游戏窗口初始化失败。",
            MinecraftLaunchFaultCode.ModLoaderBootstrapFailed => "模组加载器在引导阶段失败。",
            MinecraftLaunchFaultCode.ModConflict => "一个或多个模组、Mixin 或加载器组件发生冲突。",
            MinecraftLaunchFaultCode.MissingModDependency => "模组缺少必需前置或前置版本不正确。",
            MinecraftLaunchFaultCode.OutOfMemory => "Minecraft 可用内存不足，或 JVM 无法保留所需内存。",
            MinecraftLaunchFaultCode.FileLocked => "游戏文件正被其他进程占用。",
            MinecraftLaunchFaultCode.AccessDenied => "启动器或 Java 没有访问相关文件的权限。",
            _ => "常规错误分析器尚未识别出唯一原因。"
        };
        string dependencyText = dependencies.Count == 0
            ? string.Empty
            : "\n\n### 缺失前置\n\n" + string.Join(
                "\n",
                dependencies.Select(dependency =>
                    $"- `{dependency.ModId}`" +
                    (string.IsNullOrWhiteSpace(dependency.RequiredVersion)
                        ? string.Empty
                        : "，需要 " + dependency.RequiredVersion)));
        return $"### 常规错误分析\n\n{reason}\n\n**原始信息：** {fault.Message}{dependencyText}";
    }

    private static async Task<ModDownloadResult> DownloadMissingDependencyAsync(
        CompositeCommunityResourceCatalog catalog,
        ICommunityArtifactDownloader downloader,
        MinecraftMissingDependency dependency,
        string gameVersion,
        string loader,
        string modsDirectory,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        CommunitySearchOptions options = new(
            CommunityResourceSort.Relevance,
            GameVersion: gameVersion,
            Loader: loader,
            Source: CommunityResourceSource.All);
        IReadOnlyList<CommunityResourceEntry> entries = await catalog.SearchAsync(
                CommunityResourceCategory.Mod,
                dependency.ModId,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        CommunityResourceEntry? entry = entries
            .OrderBy(candidate => GetDependencyMatchScore(candidate, dependency))
            .FirstOrDefault();
        if (entry is null && !string.Equals(dependency.Name, dependency.ModId, StringComparison.OrdinalIgnoreCase))
        {
            entries = await catalog.SearchAsync(
                    CommunityResourceCategory.Mod,
                    dependency.Name,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            entry = entries.OrderBy(candidate => GetDependencyMatchScore(candidate, dependency)).FirstOrDefault();
        }
        if (entry is null)
            return new ModDownloadResult(false, false);

        CommunityResourceDownloadFile? file = await catalog.ResolveDownloadAsync(entry, options, cancellationToken)
            .ConfigureAwait(false);
        if (file is null)
            return new ModDownloadResult(false, false);
        string targetPath = Path.Combine(modsDirectory, DesktopPathHelpers.SanitizeFileName(file.FileName));
        if (File.Exists(targetPath))
            return new ModDownloadResult(true, false);

        await transaction.BackupFileAsync(targetPath, cancellationToken).ConfigureAwait(false);

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".PCLDownloading";
        try
        {
            await downloader.DownloadAsync(
                    file.CandidateUrls,
                    temporaryPath,
                    static (_, _) => { },
                    cancellationToken)
                .ConfigureAwait(false);

            if (MinecraftModMetadataReader.TryRead(temporaryPath, out MinecraftModMetadata? incoming) && incoming is not null)
            {
                foreach (MinecraftModMetadata conflict in MinecraftModMetadataReader.ReadDirectory(modsDirectory)
                             .Where(mod => string.Equals(mod.Id, incoming.Id, StringComparison.OrdinalIgnoreCase) &&
                                           !mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
                {
                    string disabled = CreateDisabledModPath(conflict.FilePath);
                    await transaction.BackupFileAsync(conflict.FilePath, cancellationToken).ConfigureAwait(false);
                    await transaction.BackupFileAsync(disabled, cancellationToken).ConfigureAwait(false);
                    File.Move(conflict.FilePath, disabled);
                }
            }
            File.Move(temporaryPath, targetPath, overwrite: true);
            return new ModDownloadResult(true, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static int GetDependencyMatchScore(
        CommunityResourceEntry entry,
        MinecraftMissingDependency dependency)
    {
        if (string.Equals(entry.Slug, dependency.ModId, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(entry.Title, dependency.Name, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    private static async Task<IReadOnlyList<string>> ReadRecentCrashLinesAsync(
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        List<string> lines = [];
        foreach (string path in FindRecentCrashFiles(gameDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file = new(path);
            if (file.Length > 8L * 1024L * 1024L)
                continue;
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                useAsync: true);
            using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                lines.Add(line);
        }
        return lines;
    }

    private static string[] FindRecentCrashFiles(string gameDirectory)
    {
        List<string> paths = [];
        string latestLog = Path.Combine(gameDirectory, "logs", "latest.log");
        if (File.Exists(latestLog))
            paths.Add(latestLog);
        string debugLog = Path.Combine(gameDirectory, "logs", "debug.log");
        if (File.Exists(debugLog))
            paths.Add(debugLog);
        string crashDirectory = Path.Combine(gameDirectory, "crash-reports");
        if (Directory.Exists(crashDirectory))
        {
            string? latestCrash = Directory.EnumerateFiles(crashDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latestCrash is not null)
                paths.Insert(0, latestCrash);
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<string> BuildMinecraftAiDetailedContextAsync(
        RunningGameContext context,
        string gameDirectory,
        IReadOnlyList<string> crashLines,
        IReadOnlyList<MinecraftModMetadata> installedMods,
        IReadOnlyList<MinecraftAiContextScope> requestedScopes,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        HashSet<MinecraftAiContextScope> scopes = requestedScopes.ToHashSet();
        if (scopes.Count == 0)
            return string.Empty;

        Dictionary<MinecraftAiContextScope, double> weights = new()
        {
            [MinecraftAiContextScope.Environment] = 1d,
            [MinecraftAiContextScope.Instance] = 4d,
            [MinecraftAiContextScope.CrashReports] = 4d,
            [MinecraftAiContextScope.RuntimeLogs] = 5d,
            [MinecraftAiContextScope.LaunchMethod] = 1.5d,
            [MinecraftAiContextScope.LoginMethod] = 1d
        };
        double totalWeight = scopes.Sum(scope => weights[scope]);
        int SectionBudget(MinecraftAiContextScope scope) => Math.Max(
            512,
            (int)((maximumLength - 512L) * weights[scope] / totalWeight));

        StringBuilder result = new();
        foreach (MinecraftAiContextScope scope in Enum.GetValues<MinecraftAiContextScope>())
        {
            if (!scopes.Contains(scope))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            int budget = SectionBudget(scope);
            string content = scope switch
            {
                MinecraftAiContextScope.Environment => BuildMinecraftAiEnvironmentContext(context, gameDirectory),
                MinecraftAiContextScope.Instance => await BuildMinecraftAiInstanceContextAsync(
                        context,
                        gameDirectory,
                        installedMods,
                        cancellationToken)
                    .ConfigureAwait(false),
                MinecraftAiContextScope.CrashReports => await BuildMinecraftAiCrashReportContextAsync(
                        context,
                        gameDirectory,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false),
                MinecraftAiContextScope.RuntimeLogs => await BuildMinecraftAiRuntimeLogContextAsync(
                        context,
                        gameDirectory,
                        crashLines,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false),
                MinecraftAiContextScope.LaunchMethod => BuildMinecraftAiLaunchMethodContext(context),
                MinecraftAiContextScope.LoginMethod => BuildMinecraftAiLoginMethodContext(context),
                _ => string.Empty
            };
            result.Append("\n[").Append(ToMinecraftAiScopeName(scope)).AppendLine("]")
                .AppendLine(MinecraftAiRepairAdvisor.BoundDetailedContext(content, budget));
        }

        string bounded = MinecraftAiRepairAdvisor.BoundDetailedContext(result.ToString().Trim(), maximumLength);
        DesktopFileLog.Info(
            "MinecraftRepairAI",
            $"已提供脱敏只读上下文：{string.Join(", ", scopes)}；字符数={bounded.Length}。");
        return bounded;
    }

#pragma warning disable CA1305 // Diagnostic text is serialized with explicit invariant values where applicable.
    private static string BuildMinecraftAiEnvironmentContext(RunningGameContext context, string gameDirectory)
    {
        using Process process = Process.GetCurrentProcess();
        StringBuilder value = new();
        value.AppendLine($"os={RuntimeInformation.OSDescription}")
            .AppendLine($"osArchitecture={RuntimeInformation.OSArchitecture}")
            .AppendLine($"processArchitecture={RuntimeInformation.ProcessArchitecture}")
            .AppendLine($"framework={RuntimeInformation.FrameworkDescription}")
            .AppendLine($"is64BitProcess={Environment.Is64BitProcess}")
            .AppendLine($"logicalProcessors={Environment.ProcessorCount}")
            .AppendLine($"launcherWorkingSetMiB={process.WorkingSet64 / 1024L / 1024L}")
            .AppendLine($"managedHeapMiB={GC.GetTotalMemory(false) / 1024L / 1024L}")
            .AppendLine($"culture={CultureInfo.CurrentCulture.Name}")
            .AppendLine($"uiCulture={CultureInfo.CurrentUICulture.Name}")
            .AppendLine($"timeZone={TimeZoneInfo.Local.Id}")
            .AppendLine("environmentVariables=not exposed because they may contain credentials");
        return RedactMinecraftAiContext(value.ToString(), context, gameDirectory);
    }

    private static async Task<string> BuildMinecraftAiInstanceContextAsync(
        RunningGameContext context,
        string gameDirectory,
        IReadOnlyList<MinecraftModMetadata> installedMods,
        CancellationToken cancellationToken)
    {
        InstanceMetadata metadata = await InstanceMetadataStore.LoadAsync(
                context.Instance.InstanceDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        MinecraftVersionJsonInfo version = MinecraftVersionJsonInspector.Read(context.Instance);
        StringBuilder value = new();
        value.AppendLine($"instanceName={context.Instance.Name}")
            .AppendLine($"minecraftVersion={version.MinecraftVersionId}")
            .AppendLine($"inheritsFrom={version.InheritsFrom ?? "none"}")
            .AppendLine($"loader={ResolveCommunityLoader(context.Instance, installedMods)}")
            .AppendLine($"description={metadata.Description}")
            .AppendLine($"customInfo={metadata.CustomInfo}")
            .AppendLine($"launchCount={metadata.LaunchCount}")
            .AppendLine($"modpackProjectId={metadata.ModpackProjectId}")
            .AppendLine($"modpackVersion={metadata.ModpackVersion}")
            .AppendLine($"instanceIsolation={metadata.InstanceIsolation}")
            .AppendLine($"disableAssetVerification={metadata.DisableAssetVerification}")
            .AppendLine($"ignoreJavaCompatibility={metadata.IgnoreJavaCompatibility}")
            .AppendLine($"renderer={metadata.Renderer}")
            .AppendLine($"javaSelectionMode={metadata.JavaSelectionMode}")
            .AppendLine($"memorySolution={metadata.MemorySolution}")
            .AppendLine($"customMemorySize={metadata.CustomMemorySize}")
            .AppendLine($"customJvmArgumentsConfigured={!string.IsNullOrWhiteSpace(metadata.JvmArguments)}")
            .AppendLine($"customGameArgumentsConfigured={!string.IsNullOrWhiteSpace(metadata.GameArguments)}")
            .AppendLine($"preLaunchCommandConfigured={!string.IsNullOrWhiteSpace(metadata.PreLaunchCommand)}")
            .AppendLine($"modsDirectoryFileCount={CountFilesSafely(Path.Combine(gameDirectory, "mods"))}")
            .AppendLine($"resourcePacksFileCount={CountFilesSafely(Path.Combine(gameDirectory, "resourcepacks"))}")
            .AppendLine($"shaderPacksFileCount={CountFilesSafely(Path.Combine(gameDirectory, "shaderpacks"))}")
            .AppendLine($"savesDirectoryCount={CountDirectoriesSafely(Path.Combine(gameDirectory, "saves"))}")
            .AppendLine("libraries:");
        foreach (string library in version.Libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value.Append("- ").AppendLine(library);
        }
        value.AppendLine("installedModMetadata:");
        foreach (MinecraftModMetadata mod in installedMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value.Append("- file=").Append(Path.GetFileName(mod.FilePath))
                .Append("; id=").Append(mod.Id)
                .Append("; name=").Append(mod.Name)
                .Append("; version=").Append(mod.Version)
                .Append("; loader=").Append(mod.Loader)
                .Append("; dependencies=").AppendJoin(',', mod.Dependencies)
                .AppendLine();
        }
        return RedactMinecraftAiContext(value.ToString(), context, gameDirectory);
    }

    private static async Task<string> BuildMinecraftAiCrashReportContextAsync(
        RunningGameContext context,
        string gameDirectory,
        int budget,
        CancellationToken cancellationToken)
    {
        List<string> files = [];
        string crashDirectory = Path.Combine(gameDirectory, "crash-reports");
        try
        {
            if (Directory.Exists(crashDirectory))
            {
                files.AddRange(Directory.EnumerateFiles(crashDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(3));
            }
            files.AddRange(Directory.EnumerateFiles(gameDirectory, "hs_err_pid*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(2));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopFileLog.Warn("MinecraftRepairAI", "枚举 Minecraft 崩溃报告失败。", ex);
        }
        return await ReadMinecraftAiDiagnosticFilesAsync(
                files,
                context,
                gameDirectory,
                budget,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> BuildMinecraftAiRuntimeLogContextAsync(
        RunningGameContext context,
        string gameDirectory,
        IReadOnlyList<string> crashLines,
        int budget,
        CancellationToken cancellationToken)
    {
        string[] files =
        [
            Path.Combine(gameDirectory, "logs", "latest.log"),
            Path.Combine(gameDirectory, "logs", "debug.log")
        ];
        string fileContent = await ReadMinecraftAiDiagnosticFilesAsync(
                files.Where(File.Exists),
                context,
                gameDirectory,
                Math.Max(512, budget * 3 / 4),
                cancellationToken)
            .ConfigureAwait(false);
        StringBuilder value = new(fileContent);
        if (crashLines.Count > 0)
        {
            value.AppendLine().AppendLine("--- captured launcher/runtime tail ---");
            foreach (string line in crashLines.TakeLast(160))
            {
                cancellationToken.ThrowIfCancellationRequested();
                value.AppendLine(RedactMinecraftAiContext(line, context, gameDirectory));
            }
        }
        return value.ToString();
    }

    private static string BuildMinecraftAiLaunchMethodContext(RunningGameContext context)
    {
        StringBuilder value = new();
        value.AppendLine($"launcherMode={(context.UsedExperimentalJvmHost ? "Jvm.NET lifecycle host" : "external Java process")}")
            .AppendLine($"javaExecutable={context.JavaExecutableName ?? "unknown"}")
            .AppendLine($"javaMajor={context.JavaMajorVersion?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"maximumHeapMiB={context.MemoryMegabytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"classpathEntryCount={context.ClasspathEntryCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"vmArgumentCount={context.VmArgumentCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"gameArgumentCount={context.GameArgumentCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"launchTarget={(context.WorldName is not null ? "saved world" : context.ServerAddress is not null ? "multiplayer server" : "main menu")}")
            .AppendLine("rawArguments=not exposed because arguments may contain credentials");
        return value.ToString();
    }

    private static string BuildMinecraftAiLoginMethodContext(RunningGameContext context)
    {
        StringBuilder value = new();
        value.AppendLine($"loginMethod={context.LoginMethod ?? "unknown"}")
            .AppendLine($"authenticationServer={context.LoginServerHost ?? "official/default"}")
            .AppendLine($"identityBridge={(context.UsedExperimentalJvmHost ? "Jvm.NET local session bridge" : "traditional launcher authentication")}")
            .AppendLine("profileName=<redacted>")
            .AppendLine("uuid=<redacted>")
            .AppendLine("accessToken=<redacted>")
            .AppendLine("refreshToken=<redacted>");
        return value.ToString();
    }
#pragma warning restore CA1305

    private static async Task<string> ReadMinecraftAiDiagnosticFilesAsync(
        IEnumerable<string> paths,
        RunningGameContext context,
        string gameDirectory,
        int totalBudget,
        CancellationToken cancellationToken)
    {
        string[] existing = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length == 0)
            return "no matching diagnostic files";
        int perFileBudget = Math.Max(512, totalBudget / existing.Length);
        StringBuilder result = new();
        foreach (string path in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(path);
            result.Append("--- ").Append(Path.GetFileName(path))
                .Append(" (").Append(info.Length).AppendLine(" bytes) ---");
            if (info.Length > 16L * 1024L * 1024L)
            {
                result.AppendLine("[file omitted because it exceeds 16 MiB]");
                continue;
            }
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    useAsync: true);
                using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
                StringBuilder head = new();
                Queue<string> tail = new();
                int tailLength = 0;
                int headBudget = perFileBudget / 3;
                int tailBudget = perFileBudget - headBudget;
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    string safe = RedactMinecraftAiContext(line, context, gameDirectory);
                    if (safe.Length > 1_200)
                        safe = safe[..1_200] + " [line truncated]";
                    if (head.Length < headBudget)
                    {
                        head.AppendLine(safe);
                        continue;
                    }
                    tail.Enqueue(safe);
                    tailLength += safe.Length + Environment.NewLine.Length;
                    while (tailLength > tailBudget && tail.TryDequeue(out string? removed))
                        tailLength -= removed.Length + Environment.NewLine.Length;
                }
                result.Append(head);
                if (tail.Count > 0)
                {
                    result.AppendLine("[middle of file omitted]");
                    foreach (string line in tail)
                        result.AppendLine(line);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Append("[unable to read: ").Append(ex.GetType().Name).AppendLine("]");
            }
        }
        return MinecraftAiRepairAdvisor.BoundDetailedContext(result.ToString(), totalBudget);
    }

    private static string RedactMinecraftAiContext(
        string? value,
        RunningGameContext context,
        string gameDirectory)
    {
        string result = PortableLog.Redact(value);
        List<(string Sensitive, string Replacement)> replacements =
        [
            (context.Instance.InstanceDirectory, "<instance-directory>"),
            (gameDirectory, "<game-directory>"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user-home>")
        ];
        if (!string.IsNullOrWhiteSpace(context.ProfileUsername))
            replacements.Add((context.ProfileUsername, "<profile-name>"));
        if (!string.IsNullOrWhiteSpace(context.ProfileUuid))
        {
            replacements.Add((context.ProfileUuid, "<profile-uuid>"));
            replacements.Add((context.ProfileUuid.Replace("-", string.Empty, StringComparison.Ordinal), "<profile-uuid>"));
        }
        if (!string.IsNullOrWhiteSpace(context.JavaExecutablePathForRedaction))
            replacements.Add((context.JavaExecutablePathForRedaction, "<java-path>"));
        if (!string.IsNullOrWhiteSpace(context.WorldName))
            replacements.Add((context.WorldName, "<world-name>"));
        if (!string.IsNullOrWhiteSpace(context.ServerAddress))
            replacements.Add((context.ServerAddress, "<server-address>"));
        foreach ((string sensitive, string replacement) in replacements.DistinctBy(item => item.Sensitive,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(sensitive))
                result = result.Replace(sensitive, replacement, StringComparison.OrdinalIgnoreCase);
        }
        result = QuotedAbsolutePathPattern().Replace(result, "<local-path>");
        result = WindowsAbsolutePathPattern().Replace(result, "<local-path>");
        result = UnixAbsolutePathPattern().Replace(result, "<local-path>");
        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        "(?i)(?:\"[A-Z]:\\\\[^\"\\r\\n]+\"|'[A-Z]:\\\\[^'\\r\\n]+'|\"/(?:home|Users|tmp|var|opt|usr|mnt|media|run)/[^\"\\r\\n]+\"|'/(?:home|Users|tmp|var|opt|usr|mnt|media|run)/[^'\\r\\n]+')",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex QuotedAbsolutePathPattern();

    [System.Text.RegularExpressions.GeneratedRegex(
        "(?i)\\b[A-Z]:\\\\[^\\s\"',;|<>]+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex WindowsAbsolutePathPattern();

    [System.Text.RegularExpressions.GeneratedRegex(
        "(?i)(?<![:/A-Z0-9_])/(?:home|Users|tmp|var|opt|usr|mnt|media|run)/[^\\s\"',;|<>]+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UnixAbsolutePathPattern();

    private static int CountFilesSafely(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static int CountDirectoriesSafely(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static string ToMinecraftAiScopeName(MinecraftAiContextScope scope) => scope switch
    {
        MinecraftAiContextScope.Environment => "environment",
        MinecraftAiContextScope.Instance => "instance",
        MinecraftAiContextScope.CrashReports => "crash_reports",
        MinecraftAiContextScope.RuntimeLogs => "runtime_logs",
        MinecraftAiContextScope.LaunchMethod => "launch_method",
        MinecraftAiContextScope.LoginMethod => "login_method",
        _ => scope.ToString()
    };

    private static string? ResolveLoginServerHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "custom authentication server";
    }

    internal static bool ShouldExecuteConventionalRepairDirectly(
        bool isFirstAttempt,
        bool automaticRepairEnabled,
        bool experimentalAiRepairEnabled) =>
        MinecraftRepairPolicy.ShouldExecuteConventionalRepairDirectly(
            isFirstAttempt, automaticRepairEnabled, experimentalAiRepairEnabled);

    private static string BuildRepairAttemptSummary(
        string source,
        string actions,
        MinecraftRepairExecutionResult result) =>
        $"来源={source}；动作={actions}；执行结果={result.Message}；" +
        $"实际修改文件={(result.MadeChanges ? "是" : "否")}；执行失败={(result.IsFailure ? "是" : "否")}。";

    private static string BuildFailedRepairFeedback(
        MinecraftRepairSession session,
        MinecraftLaunchFaultReport currentFault,
        int? processExitCode) =>
        MinecraftRepairPolicy.FormatFailedRepairFeedback(
            session.LastRepairSummary,
            currentFault.Code,
            currentFault.Stage,
            processExitCode);

    internal static string FormatFailedRepairFeedback(
        string? previousRepairSummary,
        MinecraftLaunchFaultCode currentCode,
        string? currentStage,
        int? processExitCode) =>
        MinecraftRepairPolicy.FormatFailedRepairFeedback(
            previousRepairSummary, currentCode, currentStage, processExitCode);

}
