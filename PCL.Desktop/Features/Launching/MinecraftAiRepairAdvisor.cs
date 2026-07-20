// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Application.Downloads;
using PCL.Application.Launching;
using PCL.Core.IO.Download;
using PCL.Core.Logging;
using PCL.Desktop.Features.Community;
using PCL.Platform.Abstractions.Security;
using PCL.Platform.Paths;
using PCL.Platform.Security;

namespace PCL.Desktop.Features.Launching;

internal static class MinecraftAiApiCredentialStore
{
    private const string StorageKey = "minecraft-ai-repair/openai-compatible-api-key";

    public static async ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        SecureStorageReadResult result = await CreateStorage().ReadAsync(StorageKey, cancellationToken)
            .ConfigureAwait(false);
        return result is { Status: SecureStorageStatus.Success, Value: { Length: > 0 } value }
            ? Encoding.UTF8.GetString(value)
            : null;
    }

    public static ValueTask<SecureStorageOperationResult> WriteAsync(
        string apiKey,
        CancellationToken cancellationToken = default) =>
        CreateStorage().WriteAsync(StorageKey, Encoding.UTF8.GetBytes(apiKey), cancellationToken);

    public static ValueTask<SecureStorageOperationResult> DeleteAsync(CancellationToken cancellationToken = default) =>
        CreateStorage().DeleteAsync(StorageKey, cancellationToken);

    private static DefaultSecureStorage CreateStorage()
    {
        DefaultPlatformPathProvider paths = new();
        return new DefaultSecureStorage(paths.ApplicationDataDirectory);
    }
}

internal sealed record MinecraftAiRepairSuggestion(
    MinecraftRepairActionKind Action,
    string AnalysisMarkdown,
    double Confidence,
    string Stage,
    double Progress,
    MinecraftAiRepairParameters Parameters,
    IReadOnlyList<MinecraftAiRepairStep>? Steps = null,
    bool NoAbility = false)
{
    public IReadOnlyList<MinecraftAiRepairStep> RepairSteps => NoAbility
        ? []
        : Steps is { Count: > 0 }
            ? Steps
            : [new MinecraftAiRepairStep(Action, Stage, Progress, string.Empty, Parameters)];
}

internal sealed record MinecraftAiRepairStep(
    MinecraftRepairActionKind Action,
    string Stage,
    double Progress,
    string Rationale,
    MinecraftAiRepairParameters Parameters);

internal sealed record MinecraftAiRepairParameters(
    string? ModId = null,
    string? ModVersion = null,
    int? JavaMajorVersion = null,
    string? LoaderKind = null,
    string? LoaderVersion = null);

internal sealed record MinecraftAiRepairProgress(string Stage, double Progress, string? Detail = null);

internal enum MinecraftAiLocalModel
{
    Gemma4E2B,
    Gemma4E4B,
    Custom
}

internal sealed record MinecraftAiModelOptions(
    string? ModelPath = null,
    string? ModelSha256 = null,
    string? RuntimePath = null,
    MinecraftAiProvider Provider = MinecraftAiProvider.Local,
    string? ApiBaseUrl = null,
    string? ApiModel = null,
    string? ApiKey = null,
    MinecraftAiReasoningEffort ReasoningEffort = MinecraftAiReasoningEffort.Medium,
    MinecraftAiLocalModel LocalModel = MinecraftAiLocalModel.Gemma4E2B,
    int TokenBudget = 4096,
    int DownloadThreadLimit = 8);

internal enum MinecraftAiProvider
{
    Local,
    OpenAiCompatible
}

internal enum MinecraftAiReasoningEffort
{
    None,
    Low,
    Medium,
    High
}

internal sealed record MinecraftAiRepairContext(
    string MinecraftVersion,
    string Loader,
    int? JavaMajorVersion,
    int? MemoryMegabytes,
    string OperatingSystem,
    string Architecture,
    int InstalledModCount,
    int CrashLogLineCount,
    IReadOnlyList<string>? MissingDependencyIds = null,
    int? ProcessExitCode = null);

internal enum MinecraftAiContextScope
{
    Environment,
    Instance,
    CrashReports,
    RuntimeLogs,
    LaunchMethod,
    LoginMethod
}

internal sealed record MinecraftAiContextRequest(
    IReadOnlyList<MinecraftAiContextScope> Scopes,
    string Stage,
    double Progress);

internal sealed record MinecraftAiModSearchRequest(string Query, string Stage, double Progress);

internal sealed record MinecraftAiModProjectDetailsRequest(
    CommunityResourceSource Source,
    string ProjectId,
    string Stage,
    double Progress);

/// <summary>
/// Runs a small local model as an advisor for the deterministic repair pipeline. The model never
/// receives a shell/tool surface: its only accepted output is one action already allowlisted by
/// <see cref="MinecraftLaunchFaultReport.AllowedActions"/>.
/// </summary>
internal sealed class MinecraftAiRepairAdvisor : IDisposable
{
    internal const int MinimumTokenBudget = 256;
    internal const int MaximumTokenBudget = 32768;

    internal static int NormalizeTokenBudget(int value) =>
        Math.Clamp(value, MinimumTokenBudget, MaximumTokenBudget);

    internal const string ModelName = "Gemma 4 E2B Instruct Q4_K_M";
    internal const long ApproximateModelBytes = 3_110_000_000;
    private static readonly LocalModelPackage[] LocalModels =
    [
        new(
            MinecraftAiLocalModel.Gemma4E2B,
            ModelName,
            "gemma-4-E2B-it-Q4_K_M.gguf",
            "740185b21d22ceb83a11c3aa62ad5842ef32c70f6096d756bbee85a1e4ec34b8",
            3_110_000_000,
            [
                new Uri("https://hf-mirror.com/unsloth/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q4_K_M.gguf"),
                new Uri("https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q4_K_M.gguf")
            ]),
        new(
            MinecraftAiLocalModel.Gemma4E4B,
            "Gemma 4 E4B Instruct Q4_K_M",
            "gemma-4-E4B-it-Q4_K_M.gguf",
            "85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87",
            4_980_000_000,
            [
                new Uri("https://hf-mirror.com/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf"),
                new Uri("https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf")
            ])
    ];

    private readonly string _rootDirectory;
    private readonly HttpClient _httpClient;
    private readonly DownloadService _downloadService = new();
    private readonly ICommunityResourceCatalog _communityCatalog;
    private readonly SemaphoreSlim _localServerGate = new(1, 1);
    private MinecraftAiLocalServerSession? _localServer;
    private CancellationTokenSource? _localServerIdleCancellation;
    private int _downloadThreadLimit;

    public MinecraftAiRepairAdvisor(
        HttpClient? httpClient = null,
        string? rootDirectory = null,
        int downloadThreadLimit = 8,
        ICommunityResourceCatalog? communityCatalog = null)
    {
        DefaultPlatformPathProvider paths = new();
        _rootDirectory = rootDirectory ?? Path.Combine(
            paths.ApplicationDataDirectory,
            "PCL-N",
            "AI",
            "MinecraftRepair");
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _communityCatalog = communityCatalog ?? new CompositeCommunityResourceCatalog();
        _downloadThreadLimit = Math.Clamp(downloadThreadLimit, 1, 32);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N-Minecraft-Repair/1.0");
    }

    public async Task<MinecraftAiRepairSuggestion?> AdviseAsync(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<string> crashLines,
        IReadOnlyList<MinecraftModMetadata> installedMods,
        MinecraftAiRepairContext repairContext,
        Func<IReadOnlyList<MinecraftAiContextScope>, CancellationToken, Task<string>>? contextProvider,
        string languageCode,
        MinecraftAiModelOptions options,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken,
        bool summaryOnly = false)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(crashLines);
        ArgumentNullException.ThrowIfNull(installedMods);
        ArgumentNullException.ThrowIfNull(repairContext);
        _downloadThreadLimit = Math.Clamp(options.DownloadThreadLimit, 1, 32);
        if (fault.AllowedActions.Length == 0 && !summaryOnly)
            return null;

        string contextDetail = BuildContextDetail(repairContext);
        progress?.Invoke(new MinecraftAiRepairProgress(
            summaryOnly ? "整理最终错误总结" : "整理当前游戏信息",
            0.7d,
            contextDetail));
        string prompt = BuildPrompt(
            fault,
            crashLines,
            installedMods,
            repairContext,
            languageCode,
            summaryOnly,
            singleJsonResult: options.Provider == MinecraftAiProvider.Local);
        Func<string, CancellationToken, Task<string>> inference;
        if (options.Provider == MinecraftAiProvider.OpenAiCompatible)
        {
            progress?.Invoke(new MinecraftAiRepairProgress(
                "正在连接 OpenAI 兼容 API",
                0.72d,
                contextDetail));
            inference = (input, token) => RunOpenAiCompatibleInferenceAsync(
                options,
                input,
                progress,
                summaryOnly,
                token);
        }
        else
        {
            Directory.CreateDirectory(_rootDirectory);
            ResolvedRuntime runtime = await ResolveRuntimeAsync(options, progress, cancellationToken).ConfigureAwait(false);
            string modelPath;
            if (options.LocalModel == MinecraftAiLocalModel.Custom)
            {
                if (string.IsNullOrWhiteSpace(options.ModelPath))
                    throw new InvalidOperationException("选择自定义模型后必须指定 GGUF 模型路径。");
                modelPath = Path.GetFullPath(options.ModelPath.Trim());
                if (!File.Exists(modelPath) || !modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("自定义 GGUF 模型不存在。", modelPath);
                if (!string.IsNullOrWhiteSpace(options.ModelSha256))
                {
                    string expected = NormalizeSha256(options.ModelSha256);
                    if (!await HasExpectedHashAsync(modelPath, expected, cancellationToken).ConfigureAwait(false))
                        throw new InvalidDataException("自定义模型 SHA-256 校验失败。");
                }
                progress?.Invoke(new MinecraftAiRepairProgress("验证自定义模型", 0.66d, Path.GetFileName(modelPath)));
            }
            else
            {
                LocalModelPackage package = ResolveLocalModel(options.LocalModel);
                modelPath = Path.Combine(_rootDirectory, "models", package.FileName);
                await EnsureDownloadedFileAsync(
                        package.Urls,
                        modelPath,
                        package.Sha256,
                        package.DisplayName,
                        0.16d,
                        0.66d,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            progress?.Invoke(new MinecraftAiRepairProgress(
                "模型正在读取错误上下文",
                0.74d,
                $"{runtime.DeviceDescription} · {contextDetail}"));
            inference = (input, token) => RunLocalInferenceWithFallbackAsync(
                runtime,
                modelPath,
                options,
                input,
                progress,
                token);
        }
        bool requiresHostIsolation = IsPreMainJvmHostInitializationFailure(fault);
        bool hostIsolationCorrectionIssued = false;
        bool requiresModSearchBeforeNoAbility =
            !summaryOnly &&
            fault.AllowedActions.Contains(MinecraftRepairActionKind.DownloadMod) &&
            repairContext.MissingDependencyIds?.Any(id => !string.IsNullOrWhiteSpace(id)) == true;
        bool modSearchCompleted = false;
        string? pendingHostDependencySearch = requiresModSearchBeforeNoAbility
            ? repairContext.MissingDependencyIds?.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            : null;
        string basePrompt = prompt;
        StringBuilder durableToolContext = new();
        string? latestFeedback = null;
        string? latestModelOutput = null;
        string BuildCurrentPrompt() => BuildBoundedRepairPrompt(
            basePrompt,
            durableToolContext.ToString(),
            latestModelOutput,
            latestFeedback,
            options.Provider == MinecraftAiProvider.OpenAiCompatible ? 48_000 : 14_000);
        int inferenceSequence = 0;
        async Task<string> RunLoggedInferenceAsync(string phase)
        {
            string input = BuildCurrentPrompt();
            PortableLog.Debug(
                "MinecraftRepairAI",
                $"模型输入；NextInference={inferenceSequence + 1}；Phase={phase}；" +
                $"PromptCharacters={input.Length}；EstimatedTokens≈{EstimateTokenCount(input)}。");
            string modelOutput = await inference(input, cancellationToken).ConfigureAwait(false);
            inferenceSequence++;
            LogRawModelOutput(inferenceSequence, phase, modelOutput);
            if (!string.IsNullOrWhiteSpace(modelOutput))
                return modelOutput;

            const string recoveryInstruction =
                "\n\n[空响应恢复指令]\n上一请求没有生成任何内容。不得重复 context_request；" +
                "请立即输出一行 progress JSON，然后输出完整 result；" +
                "若无法形成安全白名单修复，则输出完整 noability JSON。不要输出空白。";
            string recoveryInput = BuildBoundedRepairPrompt(
                basePrompt,
                durableToolContext.ToString(),
                latestModelOutput,
                (latestFeedback ?? string.Empty) + recoveryInstruction,
                options.Provider == MinecraftAiProvider.OpenAiCompatible ? 48_000 : 14_000);
            PortableLog.Warn(
                "MinecraftRepairAI",
                $"模型返回空响应，将使用恢复指令重试；PromptCharacters={recoveryInput.Length}；" +
                $"EstimatedTokens≈{EstimateTokenCount(recoveryInput)}。");
            if (options.Provider == MinecraftAiProvider.Local)
                await StopLocalServerAsync().ConfigureAwait(false);
            modelOutput = await inference(recoveryInput, cancellationToken).ConfigureAwait(false);
            inferenceSequence++;
            LogRawModelOutput(inferenceSequence, phase + "_empty_retry", modelOutput);
            if (string.IsNullOrWhiteSpace(modelOutput))
                throw new InvalidDataException("模型连续两次返回空响应，已停止当前错误分析。");
            return modelOutput;
        }
        string output = await RunLoggedInferenceAsync("initial").ConfigureAwait(false);
        HashSet<MinecraftAiContextScope> providedScopes = [];
        Dictionary<string, CommunityResourceEntry> searchedProjects = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> verifiedProjectVersions = new(StringComparer.OrdinalIgnoreCase);
        MinecraftAiRepairSuggestion? suggestion = null;
        int round = 0;
        while (true)
        {
            round++;
            cancellationToken.ThrowIfCancellationRequested();
            MinecraftAiModSearchRequest? modSearchRequest = ParseModSearchRequest(output);
            if (!modSearchCompleted && pendingHostDependencySearch is { } dependencyId)
            {
                modSearchRequest = new MinecraftAiModSearchRequest(
                    dependencyId,
                    "正在搜索缺失前置模组",
                    0.75d);
                pendingHostDependencySearch = null;
                PortableLog.Info(
                    "MinecraftRepairAI",
                    $"宿主根据已解析依赖证据执行 mod_search：{dependencyId}。");
            }
            if (modSearchRequest is not null)
            {
                progress?.Invoke(new MinecraftAiRepairProgress(
                    modSearchRequest.Stage,
                    Math.Min(0.92d, 0.84d + round * 0.015d),
                    modSearchRequest.Query));
                IReadOnlyList<CommunityResourceEntry> entries = await _communityCatalog.SearchAsync(
                        CommunityResourceCategory.Mod,
                        modSearchRequest.Query,
                        new CommunitySearchOptions(
                            GameVersion: repairContext.MinecraftVersion,
                            Loader: repairContext.Loader),
                        cancellationToken)
                    .ConfigureAwait(false);
                CommunityResourceEntry[] candidates = entries.Take(8).ToArray();
                modSearchCompleted = true;
                foreach (CommunityResourceEntry entry in candidates)
                    searchedProjects[ProjectKey(entry.Source, entry.ProjectId)] = entry;
                durableToolContext.Append("\n\nmod_search 工具结果（以下是候选列表，宿主没有替你选择项目；" +
                                          "你必须比较 slug/title 与缺失依赖名称，然后显式调用 mod_project_details）：\n")
                    .Append(FormatModSearchResults(candidates));
                latestModelOutput = output;
                latestFeedback = "从候选列表中选择最可能对应缺失依赖的项目，并调用 mod_project_details；" +
                                 "不要直接输出 DownloadMod。";
                output = await RunLoggedInferenceAsync($"round_{round}_after_mod_search").ConfigureAwait(false);
                continue;
            }

            MinecraftAiModProjectDetailsRequest? detailsRequest = ParseModProjectDetailsRequest(output);
            if (detailsRequest is not null)
            {
                string key = ProjectKey(detailsRequest.Source, detailsRequest.ProjectId);
                if (!searchedProjects.TryGetValue(key, out CommunityResourceEntry? entry))
                {
                    latestModelOutput = output;
                    latestFeedback = "宿主工具拒绝：该 projectId 不在本会话 mod_search 候选列表中。" +
                                     "请从已返回列表中选择 source/projectId 并调用 mod_project_details。";
                    output = await RunLoggedInferenceAsync($"round_{round}_invalid_project").ConfigureAwait(false);
                    continue;
                }
                progress?.Invoke(new MinecraftAiRepairProgress(
                    detailsRequest.Stage,
                    Math.Min(0.93d, 0.87d + round * 0.015d),
                    entry.Title));
                IReadOnlyList<CommunityResourceVersion> versions = await _communityCatalog.GetVersionsAsync(
                        entry,
                        new CommunitySearchOptions(),
                        cancellationToken)
                    .ConfigureAwait(false);
                CommunityResourceVersion[] compatible = versions
                    .Where(version => version.GameVersions.Contains(
                                          repairContext.MinecraftVersion,
                                          StringComparer.OrdinalIgnoreCase) &&
                                      version.Loaders.Contains(
                                          repairContext.Loader,
                                          StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(version => version.PublishedAt ?? DateTimeOffset.MinValue)
                    .Take(12)
                    .ToArray();
                verifiedProjectVersions[key] = compatible
                    .Select(version => version.VersionNumber)
                    .Where(version => !string.IsNullOrWhiteSpace(version))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                durableToolContext.Append("\n\nmod_project_details 工具结果（已验证项目）：\n")
                    .Append(FormatModProjectDetails(entry, versions, compatible, repairContext));
                latestModelOutput = output;
                latestFeedback = compatible.Length > 0
                    ? $"如选择 DownloadMod，steps[].modId 必须精确填写已验证项目 ID `{entry.ProjectId}`，" +
                      "steps[].modVersion 必须填写 compatible_versions 中的版本；不能填写模组声明 ID，也不能为 null。"
                    : "该项目没有当前 Minecraft 与加载器的兼容版本；请返回候选列表选择其他项目，或在无候选时输出 noability。";
                output = await RunLoggedInferenceAsync($"round_{round}_after_project_details").ConfigureAwait(false);
                continue;
            }

            MinecraftAiContextRequest? contextRequest = ParseContextRequest(output);
            if (requiresHostIsolation && contextRequest is not null)
            {
                latestModelOutput = output;
                latestFeedback = BuildJvmHostIsolationInstruction();
                PortableLog.Warn(
                    "MinecraftRepairAI",
                    "模型在主类执行前的 JvmHost 初始化故障中继续请求上下文；宿主要求优先执行隔离动作。");
                if (hostIsolationCorrectionIssued)
                    return CreateJvmHostIsolationSuggestion();
                hostIsolationCorrectionIssued = true;
                output = await RunLoggedInferenceAsync($"round_{round}_jvm_host_isolation").ConfigureAwait(false);
                continue;
            }
            List<MinecraftAiContextScope> requestedScopes = contextRequest?.Scopes
                .Where(scope => !providedScopes.Contains(scope))
                .ToList() ?? [];
            if (!summaryOnly && installedMods.Count > 0 &&
                !providedScopes.Contains(MinecraftAiContextScope.Instance) &&
                !requestedScopes.Contains(MinecraftAiContextScope.Instance))
            {
                requestedScopes.Add(MinecraftAiContextScope.Instance);
            }

            if (contextRequest is not null && requestedScopes.Count == 0)
            {
                latestModelOutput = output;
                latestFeedback = "你请求的上下文 scope 已经全部提供，内容位于“本会话持久工具结果”部分。" +
                                 "不得再次输出 context_request；现在必须基于已有信息输出完整 result，" +
                                 "或在没有安全白名单动作时输出 noability。";
                PortableLog.Warn(
                    "MinecraftRepairAI",
                    "模型重复请求已提供的上下文，将要求其基于现有工具结果生成终态输出。");
                progress?.Invoke(new MinecraftAiRepairProgress(
                    "模型正在根据已有上下文生成结论",
                    Math.Min(0.93d, 0.89d + round * 0.005d),
                    "已提供：" + string.Join(", ", contextRequest.Scopes)));
                output = await RunLoggedInferenceAsync($"round_{round}_duplicate_context").ConfigureAwait(false);
                continue;
            }

            if (requestedScopes.Count > 0)
            {
                if (contextProvider is null)
                    throw new InvalidOperationException("模型请求了详细上下文，但当前启动会话无法提供。");
                string contextStage = contextRequest?.Stage ?? "正在读取已安装模组列表";
                progress?.Invoke(new MinecraftAiRepairProgress(
                    contextStage,
                    Math.Min(0.92d, 0.86d + round * 0.015d),
                    "读取：" + string.Join(", ", requestedScopes)));
                string detailedContext = await contextProvider(requestedScopes, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(detailedContext))
                    throw new InvalidDataException("模型请求的上下文为空，无法继续推理。");
                foreach (MinecraftAiContextScope scope in requestedScopes)
                    providedScopes.Add(scope);
                int contextLimit = options.Provider == MinecraftAiProvider.OpenAiCompatible ? 48_000 : 14_000;
                detailedContext = BoundDetailedContext(detailedContext, contextLimit);
                durableToolContext.Append("\n\n宿主根据白名单 context_request 返回的脱敏只读上下文：\n")
                    .Append(detailedContext);
                latestModelOutput = output;
                latestFeedback = "基于已安装模组列表和依赖信息重新判断，并输出合法工具调用或终态结果。";
                progress?.Invoke(new MinecraftAiRepairProgress(
                    "模型正在结合工具上下文复核",
                    Math.Min(0.93d, 0.88d + round * 0.015d),
                    contextDetail));
                output = await RunLoggedInferenceAsync($"round_{round}_after_context").ConfigureAwait(false);
                continue;
            }

            progress?.Invoke(new MinecraftAiRepairProgress(
                "验证模型修复计划",
                Math.Min(0.94d, 0.9d + round * 0.01d)));
            suggestion = ParseSuggestion(output, fault.AllowedActions, allowNoAbility: true);
            bool retryForRequiredModTool = false;
            if (suggestion is { NoAbility: true } && requiresModSearchBeforeNoAbility)
            {
                if (!modSearchCompleted)
                {
                    suggestion = null;
                    retryForRequiredModTool = true;
                    latestModelOutput = output;
                    latestFeedback = "宿主拒绝 noability：必须先用缺失依赖名称调用 mod_search；不得直接返回 noability。";
                }
                else if (searchedProjects.Count > 0 && verifiedProjectVersions.Count == 0)
                {
                    suggestion = null;
                    retryForRequiredModTool = true;
                    latestModelOutput = output;
                    latestFeedback = "宿主拒绝 noability：mod_search 已返回完整候选列表，" +
                                     "必须由你从列表中选择最相关的 source/projectId 并调用 mod_project_details。";
                }
            }
            if (suggestion is { NoAbility: true } && requiresHostIsolation)
            {
                suggestion = null;
                latestModelOutput = output;
                latestFeedback = BuildJvmHostIsolationInstruction();
                if (hostIsolationCorrectionIssued)
                    return CreateJvmHostIsolationSuggestion();
                hostIsolationCorrectionIssued = true;
                output = await RunLoggedInferenceAsync($"round_{round}_required_jvm_host_isolation")
                    .ConfigureAwait(false);
                continue;
            }
            if (suggestion is not null &&
                !HasVerifiedDownloadProjects(suggestion, searchedProjects, verifiedProjectVersions))
            {
                suggestion = null;
            }
            if (suggestion is not null)
                return suggestion;

            MinecraftAiRepairSuggestion? inspectOnlyFallback = !requiresModSearchBeforeNoAbility
                ? ParseUnsafeResultAsInspectOnly(output, fault.AllowedActions)
                : null;
            if (inspectOnlyFallback is not null)
            {
                PortableLog.Warn(
                    "MinecraftRepairAI",
                    "模型诊断有效但修复步骤不安全，已确定性降级为 InspectOnly；不会执行模型建议的文件修改。");
                return inspectOnlyFallback;
            }
            if (retryForRequiredModTool)
            {
                progress?.Invoke(new MinecraftAiRepairProgress(
                    !modSearchCompleted ? "模型必须先搜索缺失模组" : "模型必须读取项目兼容详情",
                    Math.Min(0.93d, 0.9d + round * 0.005d)));
                output = await RunLoggedInferenceAsync($"round_{round}_required_tool").ConfigureAwait(false);
                continue;
            }

            string validationFeedback = BuildRepairValidationFeedback(fault.AllowedActions, output, round);
            PortableLog.Warn("MinecraftRepairAI", validationFeedback);
            latestModelOutput = output;
            latestFeedback = validationFeedback +
                             "\n请重新生成合法 result。DownloadMod 的 steps[].modId 是经过 " +
                             "mod_project_details 验证的项目 ProjectId（不是缺失依赖的声明 ID），且不能为 null；" +
                             "steps[].modVersion 必须来自 compatible_versions。若无法形成安全计划则输出 noability。";
            progress?.Invoke(new MinecraftAiRepairProgress(
                "模型正在根据安全校验修正计划",
                Math.Min(0.93d, 0.91d + round * 0.005d),
                $"第 {round} 次纠正"));
            output = await RunLoggedInferenceAsync($"round_{round}_validation").ConfigureAwait(false);
        }
    }

    internal static bool IsPreMainJvmHostInitializationFailure(MinecraftLaunchFaultReport fault) =>
        fault.Code == MinecraftLaunchFaultCode.JvmInitializationFailed &&
        string.Equals(fault.Subsystem, "JvmHost", StringComparison.OrdinalIgnoreCase) &&
        (fault.Stage is "HostStarting" or "BridgeReady" or "JvmStarting" or "JvmArgumentsPrepared" or
            "ClasspathPrepared" or "ModulePathPrepared" or "JvmMode") &&
        fault.AllowedActions.Contains(MinecraftRepairActionKind.DisableExperimentalJvmHost);

    internal static string BuildJvmHostIsolationInstruction() =>
        "该故障发生在实验性 Jvm.NET Host 的 JVM 初始化阶段，尚未出现 JvmRunning/MainInvoking。" +
        "Minecraft 主类、NeoForge/Fabric、模组、JNA、Netty、LWJGL 与显卡渲染代码均尚未执行，" +
        "不得将它们列为主要归因，也不得继续请求同类运行时上下文。" +
        "允许动作已包含 DisableExperimentalJvmHost；这是可逆的启动路径隔离，不修改游戏文件。" +
        "请立即输出包含 DisableExperimentalJvmHost 的单步 result。";

    internal static MinecraftAiRepairSuggestion CreateJvmHostIsolationSuggestion() =>
        new(
            MinecraftRepairActionKind.DisableExperimentalJvmHost,
            "实验性 Jvm.NET Host 在 Minecraft 主类执行前发生原生快速失败。先关闭实验 Host 并改用传统 Java 进程，可安全隔离宿主初始化路径，且不会修改游戏文件或模组。",
            0.95d,
            "关闭实验性 JVM 主机",
            0.96d,
            new MinecraftAiRepairParameters(),
            [
                new MinecraftAiRepairStep(
                    MinecraftRepairActionKind.DisableExperimentalJvmHost,
                    "关闭实验性 JVM 主机",
                    0.96d,
                    "故障发生在 JvmRunning/MainInvoking 之前，优先隔离实验宿主路径。",
                    new MinecraftAiRepairParameters())
            ]);

    private static void LogRawModelOutput(int sequence, string phase, string output)
    {
        string normalized = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        PortableLog.Debug(
            "MinecraftRepairAI",
            $"模型原始输出 BEGIN；Inference={sequence}；Phase={phase}；Characters={output.Length}\n" +
            (normalized.Length == 0 ? "<empty>" : normalized) +
            $"\n模型原始输出 END；Inference={sequence}；Phase={phase}");
    }

    internal static MinecraftAiRepairSuggestion? ParseSuggestion(
        string output,
        IReadOnlyCollection<MinecraftRepairActionKind> allowedActions,
        bool allowNoAbility = true)
    {
        if (string.IsNullOrWhiteSpace(output) || allowedActions.Count == 0 && !allowNoAbility)
            return null;
        output = RemoveThinkingBlocks(output);
        string[] outputLines = output.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string candidate = string.Join(
            '\n',
            outputLines.Where(line => !TryParseProgressEvent(line, out _)));
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = output;
        int start = candidate.IndexOf('{');
        int end = candidate.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(candidate[start..(end + 1)]);
            JsonElement root = document.RootElement;
            if (allowNoAbility && IsNoAbilityResult(root))
            {
                string noAbilitySummary = root.TryGetProperty("analysisMarkdown", out JsonElement noAbilityAnalysisElement)
                    ? noAbilityAnalysisElement.GetString() ?? string.Empty
                    : root.TryGetProperty("summary", out JsonElement noAbilitySummaryElement)
                        ? noAbilitySummaryElement.GetString() ?? string.Empty
                        : string.Empty;
                noAbilitySummary = SanitizeAnalysis(noAbilitySummary);
                if (string.IsNullOrWhiteSpace(noAbilitySummary))
                    return null;
                double noAbilityConfidence = root.TryGetProperty("confidence", out JsonElement noAbilityConfidenceElement) &&
                                             noAbilityConfidenceElement.TryGetDouble(out double parsedConfidence)
                    ? Math.Clamp(parsedConfidence, 0d, 1d)
                    : 0d;
                return new MinecraftAiRepairSuggestion(
                    MinecraftRepairActionKind.InspectOnly,
                    noAbilitySummary,
                    noAbilityConfidence,
                    "AI 已完成错误总结",
                    1d,
                    new MinecraftAiRepairParameters(),
                    NoAbility: true);
            }
            List<MinecraftAiRepairStep> steps = [];
            if (root.TryGetProperty("steps", out JsonElement stepsElement) &&
                stepsElement.ValueKind == JsonValueKind.Array)
            {
                if (stepsElement.GetArrayLength() is <= 0 or > 4)
                    return null;
                foreach (JsonElement stepElement in stepsElement.EnumerateArray())
                {
                    if (!TryReadRepairStep(stepElement, allowedActions, out MinecraftAiRepairStep? step) || step is null)
                        return null;
                    steps.Add(step);
                }
            }
            else if (TryReadRepairStep(root, allowedActions, out MinecraftAiRepairStep? legacyStep) &&
                     legacyStep is not null)
            {
                steps.Add(legacyStep);
            }
            if (steps.Count == 0)
                return null;

            string analysis = root.TryGetProperty("analysisMarkdown", out JsonElement analysisElement)
                ? analysisElement.GetString() ?? string.Empty
                : root.TryGetProperty("summary", out JsonElement summaryElement)
                    ? summaryElement.GetString() ?? string.Empty
                : string.Empty;
            analysis = SanitizeAnalysis(analysis);
            double confidence = root.TryGetProperty("confidence", out JsonElement confidenceElement) &&
                                confidenceElement.TryGetDouble(out double parsed)
                ? Math.Clamp(parsed, 0d, 1d)
                : 0d;
            MinecraftAiRepairStep first = steps[0];
            MinecraftAiRepairStep last = steps[^1];
            return new MinecraftAiRepairSuggestion(
                first.Action,
                analysis,
                confidence,
                last.Stage,
                last.Progress,
                first.Parameters,
                steps);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static MinecraftAiRepairSuggestion? ParseUnsafeResultAsInspectOnly(
        string output,
        IReadOnlyCollection<MinecraftRepairActionKind> allowedActions)
    {
        if (!allowedActions.Contains(MinecraftRepairActionKind.InspectOnly) || string.IsNullOrWhiteSpace(output))
            return null;
        string cleaned = RemoveThinkingBlocks(output);
        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(cleaned[start..(end + 1)]);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out JsonElement typeElement) ||
                !string.Equals(typeElement.GetString(), "result", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string analysis = root.TryGetProperty("analysisMarkdown", out JsonElement analysisElement)
                ? analysisElement.GetString() ?? string.Empty
                : root.TryGetProperty("summary", out JsonElement summaryElement)
                    ? summaryElement.GetString() ?? string.Empty
                    : string.Empty;
            analysis = SanitizeAnalysis(analysis);
            if (analysis.Length < 12)
                return null;
            double confidence = root.TryGetProperty("confidence", out JsonElement confidenceElement) &&
                                confidenceElement.TryGetDouble(out double parsed)
                ? Math.Clamp(parsed, 0d, 1d)
                : 0d;
            return new MinecraftAiRepairSuggestion(
                MinecraftRepairActionKind.InspectOnly,
                analysis,
                confidence,
                "AI 已完成只读错误诊断",
                1d,
                new MinecraftAiRepairParameters(),
                NoAbility: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static MinecraftAiRepairSuggestion? ParseReadOnlyDiagnosis(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        string cleaned = RemoveThinkingBlocks(output).Trim();
        string analysis = ExtractJsonStringProperty(cleaned, "analysisMarkdown") ??
                          ExtractJsonStringProperty(cleaned, "summary") ??
                          ExtractPlainTextDiagnosis(cleaned);
        analysis = SanitizeAnalysis(analysis);
        if (analysis.Length < 12)
            return null;
        return new MinecraftAiRepairSuggestion(
            MinecraftRepairActionKind.InspectOnly,
            analysis,
            0d,
            "AI 已完成只读错误诊断",
            1d,
            new MinecraftAiRepairParameters(),
            NoAbility: true);
    }

    private static string? ExtractJsonStringProperty(string value, string propertyName)
    {
        string marker = "\"" + propertyName + "\"";
        int searchIndex = 0;
        while (true)
        {
            int propertyIndex = value.IndexOf(marker, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (propertyIndex < 0)
                return null;
            int colonIndex = value.IndexOf(':', propertyIndex + marker.Length);
            if (colonIndex < 0)
                return null;
            int quoteIndex = colonIndex + 1;
            while (quoteIndex < value.Length && char.IsWhiteSpace(value[quoteIndex]))
                quoteIndex++;
            if (quoteIndex >= value.Length || value[quoteIndex] != '"')
            {
                searchIndex = propertyIndex + marker.Length;
                continue;
            }

            StringBuilder decoded = new();
            bool escaped = false;
            for (int index = quoteIndex + 1; index < value.Length; index++)
            {
                char current = value[index];
                if (escaped)
                {
                    switch (current)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            decoded.Append(current);
                            break;
                        case 'b':
                            decoded.Append('\b');
                            break;
                        case 'f':
                            decoded.Append('\f');
                            break;
                        case 'n':
                            decoded.Append('\n');
                            break;
                        case 'r':
                            decoded.Append('\r');
                            break;
                        case 't':
                            decoded.Append('\t');
                            break;
                        case 'u' when index + 4 < value.Length &&
                                           ushort.TryParse(
                                               value.AsSpan(index + 1, 4),
                                               NumberStyles.HexNumber,
                                               CultureInfo.InvariantCulture,
                                               out ushort codePoint):
                            decoded.Append((char)codePoint);
                            index += 4;
                            break;
                        default:
                            decoded.Append(current);
                            break;
                    }
                    escaped = false;
                    continue;
                }
                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (current == '"')
                    return decoded.ToString();
                decoded.Append(current);
            }
            return decoded.Length > 0 ? decoded.ToString() : null;
        }
    }

    private static string ExtractPlainTextDiagnosis(string value)
    {
        string normalized = value
            .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (normalized.StartsWith('{') ||
            normalized.StartsWith("llama_", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("system_info", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        return normalized;
    }

    internal static MinecraftAiModProjectDetailsRequest? ParseModProjectDetailsRequest(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        output = RemoveThinkingBlocks(output);
        foreach (string line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            int start = line.IndexOf('{');
            int end = line.LastIndexOf('}');
            if (start < 0 || end <= start)
                continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line[start..(end + 1)]);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("type", out JsonElement type) ||
                    !string.Equals(type.GetString(), "mod_project_details", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("projectId", out JsonElement projectIdElement) ||
                    projectIdElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string? projectId = NormalizeProjectId(projectIdElement.GetString());
                if (projectId is null)
                    return null;
                CommunityResourceSource source = root.TryGetProperty("source", out JsonElement sourceElement) &&
                                                 sourceElement.ValueKind == JsonValueKind.String &&
                                                 Enum.TryParse(sourceElement.GetString(), true, out CommunityResourceSource parsedSource) &&
                                                 parsedSource is CommunityResourceSource.Modrinth or CommunityResourceSource.CurseForge
                    ? parsedSource
                    : CommunityResourceSource.Modrinth;
                string stage = TryReadProgressName(root, out string requestedStage)
                    ? SanitizeStage(requestedStage)
                    : "正在读取模组项目详情";
                double progress = root.TryGetProperty("progress", out JsonElement progressElement) &&
                                  progressElement.ValueKind == JsonValueKind.Number &&
                                  progressElement.TryGetDouble(out double parsed)
                    ? Math.Clamp(parsed, 0d, 1d)
                    : 0.82d;
                return new MinecraftAiModProjectDetailsRequest(source, projectId, stage, progress);
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    internal static MinecraftAiModSearchRequest? ParseModSearchRequest(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        output = RemoveThinkingBlocks(output);
        foreach (string line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            int start = line.IndexOf('{');
            int end = line.LastIndexOf('}');
            if (start < 0 || end <= start)
                continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line[start..(end + 1)]);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("type", out JsonElement type) ||
                    !string.Equals(type.GetString(), "mod_search", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("query", out JsonElement queryElement) ||
                    queryElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string query = (queryElement.GetString() ?? string.Empty).Trim();
                if (query.Length is < 2 or > 80 || query.Any(char.IsControl))
                    return null;
                string stage = TryReadProgressName(root, out string requestedStage)
                    ? SanitizeStage(requestedStage)
                    : "正在搜索社区模组";
                double progress = root.TryGetProperty("progress", out JsonElement progressElement) &&
                                  progressElement.ValueKind == JsonValueKind.Number &&
                                  progressElement.TryGetDouble(out double parsed)
                    ? Math.Clamp(parsed, 0d, 1d)
                    : 0.75d;
                return new MinecraftAiModSearchRequest(query, stage, progress);
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    internal static MinecraftAiContextRequest? ParseContextRequest(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        output = RemoveThinkingBlocks(output);
        foreach (string line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            int start = line.IndexOf('{');
            int end = line.LastIndexOf('}');
            if (start < 0 || end <= start)
                continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line[start..(end + 1)]);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("type", out JsonElement type) ||
                    !string.Equals(type.GetString(), "context_request", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("scopes", out JsonElement scopesElement) ||
                    scopesElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                List<MinecraftAiContextScope> scopes = [];
                foreach (JsonElement scopeElement in scopesElement.EnumerateArray())
                {
                    string normalized = (scopeElement.GetString() ?? string.Empty).Replace("_", string.Empty);
                    if (Enum.TryParse(normalized, ignoreCase: true, out MinecraftAiContextScope scope) &&
                        !scopes.Contains(scope))
                    {
                        scopes.Add(scope);
                    }
                }
                if (scopes.Count == 0)
                    return null;
                string stage = TryReadProgressName(root, out string requestedStage)
                    ? SanitizeStage(requestedStage)
                    : "正在读取模型请求的上下文";
                double progressValue = root.TryGetProperty("progress", out JsonElement progressElement) &&
                                       progressElement.TryGetDouble(out double parsed)
                    ? Math.Clamp(parsed, 0d, 1d)
                    : 0.5d;
                return new MinecraftAiContextRequest(scopes, stage, progressValue);
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    private static bool IsNoAbilityResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        string? type = root.TryGetProperty("type", out JsonElement typeElement) &&
                       typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        string? tool = root.TryGetProperty("tool", out JsonElement toolElement) &&
                       toolElement.ValueKind == JsonValueKind.String
            ? toolElement.GetString()
            : null;
        string? name = root.TryGetProperty("name", out JsonElement nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        return string.Equals(type, "noability", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "no_ability", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tool, "noability", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "noability", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadRepairStep(
        JsonElement element,
        IReadOnlyCollection<MinecraftRepairActionKind> allowedActions,
        out MinecraftAiRepairStep? step)
    {
        step = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("action", out JsonElement actionElement) ||
            actionElement.ValueKind != JsonValueKind.String ||
            !Enum.TryParse(actionElement.GetString(), ignoreCase: true, out MinecraftRepairActionKind action) ||
            !allowedActions.Contains(action) ||
            !element.TryGetProperty("stage", out JsonElement stageElement) ||
            stageElement.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("progress", out JsonElement progressElement) ||
            progressElement.ValueKind != JsonValueKind.Number ||
            !progressElement.TryGetDouble(out double progressValue))
        {
            return false;
        }
        MinecraftAiRepairParameters parameters = new(
            ReadSafeToken(element, "modId", 128),
            ReadSafeToken(element, "modVersion", 128),
            ReadJavaMajor(element),
            ReadLoaderKind(element),
            ReadSafeToken(element, "loaderVersion", 128));
        if (action is (MinecraftRepairActionKind.DownloadMod or MinecraftRepairActionKind.DisableMod or
            MinecraftRepairActionKind.UpdateMod) && string.IsNullOrWhiteSpace(parameters.ModId))
        {
            return false;
        }
        if (action == MinecraftRepairActionKind.UpdateMod && string.IsNullOrWhiteSpace(parameters.ModVersion))
            return false;
        string rationale = element.TryGetProperty("rationale", out JsonElement rationaleElement) &&
                           rationaleElement.ValueKind == JsonValueKind.String
            ? SanitizeAnalysis(rationaleElement.GetString() ?? string.Empty)
            : string.Empty;
        step = new MinecraftAiRepairStep(
            action,
            SanitizeStage(stageElement.GetString()),
            Math.Clamp(progressValue, 0d, 1d),
            rationale,
            parameters);
        return true;
    }

    private async Task<ResolvedRuntime> ResolveRuntimeAsync(
        MinecraftAiModelOptions options,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.RuntimePath))
        {
            string customRuntime = options.RuntimePath.Trim();
            if (!File.Exists(customRuntime))
                throw new FileNotFoundException("自定义 llama.cpp 可执行文件不存在。", customRuntime);
            string fullPath = ResolveCompletionExecutable(Path.GetFullPath(customRuntime));
            bool customGpu = await ProbeGpuAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return new ResolvedRuntime(fullPath, customGpu, customGpu ? "自定义 GPU" : "自定义 CPU");
        }
        string? configured = Environment.GetEnvironmentVariable("PCL_LLAMA_CLI");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            string fullPath = ResolveCompletionExecutable(Path.GetFullPath(configured.Trim()));
            bool configuredGpu = await ProbeGpuAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return new ResolvedRuntime(fullPath, configuredGpu, configuredGpu ? "环境变量 GPU" : "环境变量 CPU");
        }
        string? pathExecutable = FindExecutable("llama-completion");
        if (pathExecutable is not null)
        {
            bool pathGpu = await ProbeGpuAsync(pathExecutable, cancellationToken).ConfigureAwait(false);
            return new ResolvedRuntime(pathExecutable, pathGpu, pathGpu ? "PATH GPU" : "PATH CPU");
        }

        RuntimePackage cpuPackage = ResolveRuntimePackage();
        RuntimePackage? gpuPackage = ResolveGpuRuntimePackage(cpuPackage.RuntimeId);
        if (gpuPackage is not null)
        {
            progress?.Invoke(new MinecraftAiRepairProgress("准备显卡加速运行时", 0.02d, gpuPackage.Backend));
            string gpuExecutable = await EnsureRuntimePackageAsync(gpuPackage, progress, cancellationToken)
                .ConfigureAwait(false);
            progress?.Invoke(new MinecraftAiRepairProgress("检测可用显卡", 0.145d, gpuPackage.Backend));
            if (await ProbeGpuAsync(gpuExecutable, cancellationToken).ConfigureAwait(false))
            {
                progress?.Invoke(new MinecraftAiRepairProgress("已启用显卡推理", 0.15d, gpuPackage.Backend));
                return new ResolvedRuntime(gpuExecutable, true, gpuPackage.Backend);
            }
            progress?.Invoke(new MinecraftAiRepairProgress("未发现可用显卡，切换 CPU", 0.15d, gpuPackage.Backend));
        }

        return await ResolveCpuRuntimeAsync(options, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedRuntime> ResolveCpuRuntimeAsync(
        MinecraftAiModelOptions options,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.RuntimePath))
            return new ResolvedRuntime(
                ResolveCompletionExecutable(Path.GetFullPath(options.RuntimePath.Trim())),
                false,
                "自定义 CPU");
        string? configured = Environment.GetEnvironmentVariable("PCL_LLAMA_CLI");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
            return new ResolvedRuntime(
                ResolveCompletionExecutable(Path.GetFullPath(configured.Trim())),
                false,
                "环境变量 CPU");
        string? pathExecutable = FindExecutable("llama-completion");
        if (pathExecutable is not null)
            return new ResolvedRuntime(pathExecutable, false, "PATH CPU");

        RuntimePackage package = ResolveRuntimePackage();
        string executable = await EnsureRuntimePackageAsync(package, progress, cancellationToken).ConfigureAwait(false);
        return new ResolvedRuntime(executable, false, package.Backend);
    }

    private async Task<string> EnsureRuntimePackageAsync(
        RuntimePackage package,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        string runtimeDirectory = Path.Combine(_rootDirectory, "runtime", package.RuntimeId);
        string localExecutable = Path.Combine(
            runtimeDirectory,
            OperatingSystem.IsWindows() ? "llama-completion.exe" : "llama-completion");
        if (File.Exists(localExecutable))
            return localExecutable;

        progress?.Invoke(new MinecraftAiRepairProgress("下载本地模型运行时", 0.02d, package.RuntimeId));
        string archivePath = Path.Combine(_rootDirectory, package.ArchiveFileName);
        await EnsureDownloadedFileAsync(
                package.Urls,
                archivePath,
                package.Sha256,
                "llama.cpp 运行时",
                0.02d,
                0.14d,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        string temporaryDirectory = runtimeDirectory + "." + Guid.NewGuid().ToString("N") + ".extract";
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            ExtractRuntimeArchive(archivePath, temporaryDirectory);
            string? extractedExecutable = Directory.EnumerateFiles(
                    temporaryDirectory,
                    OperatingSystem.IsWindows() ? "llama-completion.exe" : "llama-completion",
                    SearchOption.AllDirectories)
                .FirstOrDefault();
            if (extractedExecutable is null)
                throw new InvalidDataException("llama.cpp 运行时包缺少 llama-completion 可执行文件。");
            Directory.CreateDirectory(runtimeDirectory);
            string sourceDirectory = Path.GetDirectoryName(extractedExecutable)!;
            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(runtimeDirectory, Path.GetFileName(file)), overwrite: true);
            EnsureExecutableBit(localExecutable);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
        if (!File.Exists(localExecutable))
            throw new FileNotFoundException("本地模型运行时安装失败。", localExecutable);
        progress?.Invoke(new MinecraftAiRepairProgress("本地模型运行时已就绪", 0.15d, package.RuntimeId));
        return localExecutable;
    }

    private async Task EnsureDownloadedFileAsync(
        IReadOnlyList<Uri> sources,
        string targetPath,
        string expectedSha256,
        string displayName,
        double progressStart,
        double progressEnd,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(targetPath) &&
            await HasExpectedHashAsync(targetPath, expectedSha256, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        DownloadTransferResult result = await _downloadService.DownloadAsync(
                new DownloadRequest
                {
                    Sources = sources.Select(static uri => uri.AbsoluteUri).ToArray(),
                    DestinationPath = targetPath,
                    MaxParallelSegments = _downloadThreadLimit,
                    ConnectionFactory = url => new HttpDlConnection(_httpClient, url)
                },
                download =>
                {
                    if (download.Stage != DownloadStage.Downloading || download.TotalBytes <= 0)
                        return;
                    int percent = (int)Math.Clamp(
                        download.DownloadedBytes * 100 / download.TotalBytes,
                        0,
                        100);
                    double normalized = progressStart + ((progressEnd - progressStart) * percent / 100d);
                    progress?.Invoke(new MinecraftAiRepairProgress(
                        "下载" + displayName,
                        normalized,
                        $"{percent}%（{FormatBytes(download.DownloadedBytes)}/{FormatBytes(download.TotalBytes)}）" +
                        $" · {FormatBytes(download.BytesPerSecond)}/s · {_downloadThreadLimit} 线程"));
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            Exception? failure = result.Errors.Count > 0 ? result.Errors[^1].Exception : null;
            throw new HttpRequestException(displayName + "的所有下载源均不可用。", failure);
        }
        if (!await HasExpectedHashAsync(targetPath, expectedSha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(targetPath);
            throw new InvalidDataException(displayName + " SHA-256 校验失败，文件已丢弃。");
        }
        progress?.Invoke(new MinecraftAiRepairProgress(
            displayName + "校验完成",
            progressEnd,
            Uri.TryCreate(result.SuccessfulSource, UriKind.Absolute, out Uri? successfulUri)
                ? successfulUri.Host
                : string.Empty));
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ProbeGpuAsync(string executable, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "llama", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add("cli");
        startInfo.ArgumentList.Add("--list-devices");
        try
        {
            using Process process = Process.Start(startInfo)
                                    ?? throw new InvalidOperationException("无法启动 llama.cpp 设备检测。");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw;
            }
            string devices = (await stdout.ConfigureAwait(false)) + "\n" + await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 ||
                devices.Contains("no devices", StringComparison.OrdinalIgnoreCase) ||
                devices.Contains("no GPU", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string[] softwareMarkers = ["llvmpipe", "swiftshader", "software rasterizer", "microsoft basic render"];
            if (softwareMarkers.Any(marker => devices.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                return false;
            return devices.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
                   devices.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ||
                   devices.Contains("Metal", StringComparison.OrdinalIgnoreCase) ||
                   devices.Contains("OpenCL", StringComparison.OrdinalIgnoreCase) ||
                   devices.Contains("ROCm", StringComparison.OrdinalIgnoreCase) ||
                   devices.Contains("GPU", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            PortableLog.Debug(exception, "MinecraftRepairAI", "llama.cpp 显卡设备检测失败，将使用 CPU。");
            return false;
        }
    }

    private async Task<string> RunLocalInferenceWithFallbackAsync(
        ResolvedRuntime runtime,
        string modelPath,
        MinecraftAiModelOptions options,
        string prompt,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunLocalServerInferenceAsync(
                    runtime,
                    modelPath,
                    options.TokenBudget,
                    prompt,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception serverException) when (!cancellationToken.IsCancellationRequested)
        {
            PortableLog.Warn(
                serverException,
                "MinecraftRepairAI",
                runtime.UseGpu
                    ? "GPU 常驻模型服务不可用，将切换 CPU 常驻服务。"
                    : "CPU 常驻模型服务不可用，将回退单次 completion 推理。");
            await StopLocalServerAsync().ConfigureAwait(false);
            if (runtime.UseGpu)
            {
                progress?.Invoke(new MinecraftAiRepairProgress(
                    "显卡模型服务不可用，切换 CPU",
                    0.74d,
                    SanitizeAnalysis(serverException.Message)));
                ResolvedRuntime cpuRuntime = await ResolveCpuRuntimeAsync(options, progress, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    return await RunLocalServerInferenceAsync(
                            cpuRuntime,
                            modelPath,
                            options.TokenBudget,
                            prompt,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception cpuServerException) when (!cancellationToken.IsCancellationRequested)
                {
                    PortableLog.Warn(
                        cpuServerException,
                        "MinecraftRepairAI",
                        "CPU 常驻模型服务不可用，将回退单次 completion 推理。");
                    await StopLocalServerAsync().ConfigureAwait(false);
                    return await RunInferenceAsync(
                            cpuRuntime,
                            modelPath,
                            prompt,
                            options.TokenBudget,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        try
        {
            return await RunInferenceAsync(runtime, modelPath, prompt, options.TokenBudget, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception gpuException)
            when (runtime.UseGpu && gpuException is not OperationCanceledException)
        {
            PortableLog.Warn(gpuException, "MinecraftRepairAI", "显卡推理失败，将回退 CPU 运行时重试。");
            progress?.Invoke(new MinecraftAiRepairProgress(
                "显卡推理不可用，切换 CPU",
                0.74d,
                SanitizeAnalysis(gpuException.Message)));
            ResolvedRuntime cpuRuntime = await ResolveCpuRuntimeAsync(options, progress, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                return await RunLocalServerInferenceAsync(
                        cpuRuntime,
                        modelPath,
                        options.TokenBudget,
                        prompt,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception cpuServerException) when (!cancellationToken.IsCancellationRequested)
            {
                PortableLog.Warn(
                    cpuServerException,
                    "MinecraftRepairAI",
                    "CPU 常驻模型服务不可用，将回退单次 completion 推理。");
                await StopLocalServerAsync().ConfigureAwait(false);
            }
            return await RunInferenceAsync(
                    cpuRuntime,
                    modelPath,
                    prompt,
                    options.TokenBudget,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<string> RunLocalServerInferenceAsync(
        ResolvedRuntime runtime,
        string modelPath,
        int tokenBudget,
        string prompt,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _localServerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelLocalServerIdleShutdown();
            int contextSize = Math.Clamp(
                NormalizeTokenBudget(tokenBudget) + 4096,
                8192,
                MaximumTokenBudget + 4096);
            string serverPath = ResolveServerExecutable(runtime.ExecutablePath);
            if (_localServer is null ||
                !_localServer.Matches(serverPath, modelPath, runtime.UseGpu, contextSize) ||
                _localServer.HasExited)
            {
                if (_localServer is not null)
                    await DisposeLocalServerCoreAsync().ConfigureAwait(false);
                progress?.Invoke(new MinecraftAiRepairProgress(
                    "正在启动本地模型服务",
                    0.72d,
                    runtime.DeviceDescription));
                _localServer = await MinecraftAiLocalServerSession.StartAsync(
                        serverPath,
                        modelPath,
                        runtime.UseGpu,
                        runtime.DeviceDescription,
                        contextSize,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                PortableLog.Info(
                    "MinecraftRepairAI",
                    $"复用本地模型服务；PID={_localServer.ProcessId}；Device={runtime.DeviceDescription}。");
            }
            string result = await _localServer.InferAsync(prompt, tokenBudget, progress, cancellationToken)
                .ConfigureAwait(false);
            ScheduleLocalServerIdleShutdown();
            return result;
        }
        finally
        {
            _localServerGate.Release();
        }
    }

    public async Task StopLocalServerAsync()
    {
        await _localServerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelLocalServerIdleShutdown();
            await DisposeLocalServerCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _localServerGate.Release();
        }
    }

    public void Dispose()
    {
        StopLocalServer();
        _localServerGate.Dispose();
    }

    public void StopLocalServer()
    {
        CancelLocalServerIdleShutdown();
        MinecraftAiLocalServerSession? server = Interlocked.Exchange(ref _localServer, null);
        server?.Dispose();
    }

    private async Task DisposeLocalServerCoreAsync()
    {
        MinecraftAiLocalServerSession? server = _localServer;
        _localServer = null;
        if (server is not null)
            await server.DisposeAsync().ConfigureAwait(false);
    }

    private void ScheduleLocalServerIdleShutdown()
    {
        CancelLocalServerIdleShutdown();
        CancellationTokenSource cancellation = new();
        _localServerIdleCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), cancellation.Token).ConfigureAwait(false);
                await StopLocalServerAsync().ConfigureAwait(false);
                PortableLog.Info("MinecraftRepairAI", "本地模型服务空闲超时，已释放模型资源。");
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelLocalServerIdleShutdown()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _localServerIdleCancellation, null);
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static async Task<string> RunInferenceAsync(
        ResolvedRuntime runtime,
        string modelPath,
        string prompt,
        int tokenBudget,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        using TemporaryPromptFile promptFile = TemporaryPromptFile.Create(prompt);
        ProcessStartInfo startInfo = CreateInferenceStartInfo(
            runtime.ExecutablePath,
            modelPath,
            promptFile.Path,
            runtime.UseGpu,
            tokenBudget);

        using Process process = Process.Start(startInfo)
                                 ?? throw new InvalidOperationException("无法启动本地模型运行时。");
        process.StandardInput.Close();
        PortableLog.Info(
            "MinecraftRepairAI",
            $"本地模型推理进程已启动；PID={process.Id}；Device={runtime.DeviceDescription}；GPU={runtime.UseGpu}。");
        StringBuilder outputBuilder = new();
        StringBuilder errorBuilder = new();
        int receivedCharacters = 0;
        TaskCompletionSource<bool> terminalResultReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task stdout = ReadProcessOutputAsync(
            process.StandardOutput,
            outputBuilder,
            chunk =>
            {
                Interlocked.Add(ref receivedCharacters, chunk.Length);
                if (ContainsCompleteTerminalResult(outputBuilder))
                    terminalResultReceived.TrySetResult(true);
            },
            line =>
            {
                if (TryParseProgressEvent(line, out MinecraftAiRepairProgress? modelProgress) &&
                    modelProgress is not null)
                {
                    progress?.Invoke(modelProgress with
                    {
                        Progress = 0.78d + (Math.Clamp(modelProgress.Progress, 0d, 1d) * 0.14d),
                        Detail = runtime.DeviceDescription
                    });
                }
            });
        Task stderr = ReadProcessLinesAsync(process.StandardError, errorBuilder);
        using CancellationTokenSource heartbeatCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = ReportInferenceHeartbeatAsync(
            runtime,
            () => Volatile.Read(ref receivedCharacters),
            progress,
            heartbeatCancellation.Token);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(runtime.UseGpu ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(8));
        bool timedOut = false;
        bool stoppedAfterResult = false;
        try
        {
            Task processExit = process.WaitForExitAsync(timeout.Token);
            Task completed = await Task.WhenAny(processExit, terminalResultReceived.Task).ConfigureAwait(false);
            if (completed == terminalResultReceived.Task && !process.HasExited)
            {
                stoppedAfterResult = true;
                progress?.Invoke(new MinecraftAiRepairProgress(
                    "模型已生成完整结果",
                    0.93d,
                    runtime.DeviceDescription));
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await processExit.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            timedOut = true;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        string output = outputBuilder.ToString();
        string error = errorBuilder.ToString();
        if (timedOut)
        {
            throw new TimeoutException(
                $"本地模型在 {(runtime.UseGpu ? 5 : 8)} 分钟内没有完成推理。" +
                (string.IsNullOrWhiteSpace(error) ? string.Empty : " llama.cpp：" + SanitizeAnalysis(error)));
        }
        if (process.ExitCode != 0 && !stoppedAfterResult)
        {
            string diagnostics =
                $"exitCode={process.ExitCode}; stdoutLength={output.Length}; stderrLength={error.Length}; " +
                $"stdoutTail={FormatProcessOutputTail(output)}; stderr={FormatRuntimeError(error)}";
            PortableLog.Warn("MinecraftRepairAI", "本地模型进程异常退出；" + diagnostics);
            throw new InvalidOperationException("本地模型推理失败：" + diagnostics);
        }
        progress?.Invoke(new MinecraftAiRepairProgress("模型已完成推理", 0.93d, runtime.DeviceDescription));
        return output;
    }

    private async Task<string> RunOpenAiCompatibleInferenceAsync(
        MinecraftAiModelOptions options,
        string prompt,
        Action<MinecraftAiRepairProgress>? progress,
        bool summaryOnly,
        CancellationToken cancellationToken)
    {
        Uri endpoint = ResolveChatCompletionsEndpoint(options.ApiBaseUrl);
        string model = options.ApiModel?.Trim() ?? string.Empty;
        string apiKey = options.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("OpenAI 兼容 API 模型名不能为空。");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("尚未保存 OpenAI 兼容 API Key。");

        JsonArray messages = [];
        messages.Add((JsonNode)new JsonObject
        {
            ["role"] = "system",
            ["content"] = "Use private reasoning when supported, but never reveal hidden chain-of-thought. " +
                          "Return only the requested progress JSON lines, an optional context request, " +
                          "and the final auditable repair plan."
        });
        messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = prompt });
        JsonArray requiredNoAbilityArguments = [];
        requiredNoAbilityArguments.Add((JsonNode?)JsonValue.Create("analysisMarkdown"));
        JsonObject noAbilityTool = new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "noability",
                ["description"] = "Use when no safe allowlisted repair can solve the error, or when asked to produce the final error summary.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["analysisMarkdown"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Concise user-facing error summary with cause, evidence, and manual next steps."
                        },
                        ["confidence"] = new JsonObject { ["type"] = "number" }
                    },
                    ["required"] = requiredNoAbilityArguments
                }
            }
        };
        JsonArray tools = [];
        tools.Add((JsonNode?)noAbilityTool);
        JsonObject payload = new()
        {
            ["model"] = model,
            ["stream"] = true,
            ["temperature"] = 0.1,
            ["max_tokens"] = NormalizeTokenBudget(options.TokenBudget),
            ["messages"] = messages,
            ["tools"] = tools,
            ["tool_choice"] = summaryOnly ? "required" : "auto"
        };
        if (options.ReasoningEffort != MinecraftAiReasoningEffort.None)
        {
            payload["reasoning_effort"] = options.ReasoningEffort switch
            {
                MinecraftAiReasoningEffort.Low => "low",
                MinecraftAiReasoningEffort.High => "high",
                _ => "medium"
            };
        }

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        PortableLog.Info(
            "MinecraftRepairAI",
            $"开始调用 OpenAI 兼容 API；Endpoint={endpoint.GetLeftPart(UriPartial.Path)}；Model={model}；" +
            $"Reasoning={options.ReasoningEffort}。");

        int reasoningCharacters = 0;
        int answerCharacters = 0;
        using CancellationTokenSource heartbeatCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = ReportApiHeartbeatAsync(
            model,
            () => Volatile.Read(ref reasoningCharacters),
            () => Volatile.Read(ref answerCharacters),
            progress,
            heartbeatCancellation.Token);
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"OpenAI 兼容 API 返回 HTTP {(int)response.StatusCode}：{SanitizeAnalysis(error)}");
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                string content = ExtractChatCompletionResult(json);
                Interlocked.Add(ref answerCharacters, content.Length);
                return content;
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using StreamReader reader = new(responseStream, Encoding.UTF8);
            StringBuilder answer = new();
            StringBuilder pendingLine = new();
            StringBuilder toolArguments = new();
            string? toolName = null;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;
                string data = line[5..].Trim();
                if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    continue;
                using JsonDocument chunk = JsonDocument.Parse(data);
                JsonElement choice = chunk.RootElement.TryGetProperty("choices", out JsonElement choices) &&
                                     choices.ValueKind == JsonValueKind.Array &&
                                     choices.GetArrayLength() > 0
                    ? choices[0]
                    : default;
                if (choice.ValueKind != JsonValueKind.Object ||
                    !choice.TryGetProperty("delta", out JsonElement delta))
                {
                    continue;
                }
                string reasoning = ReadJsonText(delta, "reasoning_content") ??
                                   ReadJsonText(delta, "reasoning") ?? string.Empty;
                if (delta.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                    toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement toolCall in toolCalls.EnumerateArray())
                    {
                        if (!toolCall.TryGetProperty("function", out JsonElement function))
                            continue;
                        toolName ??= ReadJsonText(function, "name");
                        if (ReadJsonText(function, "arguments") is { } arguments)
                            toolArguments.Append(arguments);
                    }
                }
                if (reasoning.Length > 0)
                    Interlocked.Add(ref reasoningCharacters, reasoning.Length);
                string content = ReadJsonText(delta, "content") ?? string.Empty;
                if (content.Length == 0)
                    continue;
                answer.Append(content);
                pendingLine.Append(content);
                Interlocked.Add(ref answerCharacters, content.Length);
                ReportCompletedApiLines(pendingLine, progress);
            }
            if (string.Equals(toolName, "noability", StringComparison.OrdinalIgnoreCase))
                return NormalizeNoAbilityToolArguments(toolArguments.ToString());
            return answer.ToString();
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static Uri ResolveChatCompletionsEndpoint(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri? baseUri) ||
            baseUri.Scheme is not ("https" or "http") ||
            baseUri.Scheme == "http" && !baseUri.IsLoopback)
        {
            throw new InvalidOperationException("OpenAI 兼容 API 地址必须是 HTTPS；仅本机地址可使用 HTTP。");
        }
        string value = baseUri.ToString().TrimEnd('/');
        return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? new Uri(value)
            : new Uri(value + "/chat/completions");
    }

    private static string ExtractChatCompletionResult(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out JsonElement message))
        {
            throw new InvalidDataException("OpenAI 兼容 API 响应缺少 choices[0].message。");
        }
        if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement toolCall in toolCalls.EnumerateArray())
            {
                if (!toolCall.TryGetProperty("function", out JsonElement function) ||
                    !string.Equals(ReadJsonText(function, "name"), "noability", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return NormalizeNoAbilityToolArguments(ReadJsonText(function, "arguments") ?? "{}");
            }
        }
        return ReadJsonText(message, "content")
               ?? throw new InvalidDataException("OpenAI 兼容 API 响应缺少文本或工具调用。");
    }

    private static string NormalizeNoAbilityToolArguments(string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        JsonElement root = document.RootElement;
        string analysis = root.TryGetProperty("analysisMarkdown", out JsonElement analysisElement)
            ? analysisElement.GetString() ?? string.Empty
            : root.TryGetProperty("summary", out JsonElement summaryElement)
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;
        double confidence = root.TryGetProperty("confidence", out JsonElement confidenceElement) &&
                            confidenceElement.TryGetDouble(out double parsed)
            ? Math.Clamp(parsed, 0d, 1d)
            : 0d;
        JsonObject result = new()
        {
            ["type"] = "noability",
            ["analysisMarkdown"] = analysis,
            ["confidence"] = confidence
        };
        return result.ToJsonString();
    }

    internal static string? FormatLocalServerTerminalMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        List<string> fields = [];
        AddJsonMetadataField(root, fields, "stop");
        AddJsonMetadataField(root, fields, "stopped_eos");
        AddJsonMetadataField(root, fields, "stopped_limit");
        AddJsonMetadataField(root, fields, "stopped_word");
        AddJsonMetadataField(root, fields, "truncated");
        AddJsonMetadataField(root, fields, "tokens_predicted");
        AddJsonMetadataField(root, fields, "tokens_evaluated");
        if (root.TryGetProperty("timings", out JsonElement timings) && timings.ValueKind == JsonValueKind.Object)
        {
            AddJsonMetadataField(timings, fields, "predicted_n", "timings.predicted_n");
            AddJsonMetadataField(timings, fields, "prompt_n", "timings.prompt_n");
        }
        return fields.Count == 0 ? null : string.Join(',', fields);
    }

    private static void AddJsonMetadataField(
        JsonElement root, List<string> fields, string propertyName, string? displayName = null)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }
        string text = value.ToString().ReplaceLineEndings(" ");
        if (text.Length > 64)
            text = text[..64];
        fields.Add($"{displayName ?? propertyName}={text}");
    }

    private static string? ReadJsonText(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void ReportCompletedApiLines(
        StringBuilder pendingLine,
        Action<MinecraftAiRepairProgress>? progress)
    {
        while (true)
        {
            int newline = pendingLine.ToString().IndexOf('\n');
            if (newline < 0)
                return;
            string line = pendingLine.ToString(0, newline).TrimEnd('\r');
            pendingLine.Remove(0, newline + 1);
            if (TryParseProgressEvent(line, out MinecraftAiRepairProgress? modelProgress) &&
                modelProgress is not null)
            {
                progress?.Invoke(modelProgress with
                {
                    Progress = 0.78d + (Math.Clamp(modelProgress.Progress, 0d, 1d) * 0.14d),
                    Detail = "OpenAI 兼容 API"
                });
            }
        }
    }

    private static async Task ReportApiHeartbeatAsync(
        string model,
        Func<int> reasoningCharacters,
        Func<int> answerCharacters,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            int reasoning = reasoningCharacters();
            int answer = answerCharacters();
            string stage = reasoning > 0
                ? "在线模型正在进行 thinking 推理"
                : answer > 0 ? "在线模型正在生成链式修复计划" : "等待在线模型响应";
            progress?.Invoke(new MinecraftAiRepairProgress(
                stage,
                Math.Min(0.92d, 0.75d + (stopwatch.Elapsed.TotalSeconds / 600d * 0.17d)),
                $"{model} · {stopwatch.Elapsed:mm\\:ss}" +
                (answer > 0 ? $" · 已生成 {answer} 字符" : string.Empty)));
        }
    }

    internal static ProcessStartInfo CreateInferenceStartInfo(
        string executablePath,
        string modelPath,
        string promptFilePath,
        bool useGpu,
        int tokenBudget = 4096)
    {
        tokenBudget = NormalizeTokenBudget(tokenBudget);
        int contextSize = Math.Clamp(tokenBudget + 4096, 8192, MaximumTokenBudget + 4096);
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(promptFilePath);
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(tokenBudget.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(contextSize.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-ngl");
        startInfo.ArgumentList.Add(useGpu ? "all" : "0");
        startInfo.ArgumentList.Add("--temp");
        startInfo.ArgumentList.Add("0.1");
        startInfo.ArgumentList.Add("--top-k");
        startInfo.ArgumentList.Add("20");
        startInfo.ArgumentList.Add("--no-display-prompt");
        startInfo.ArgumentList.Add("-no-cnv");
        startInfo.ArgumentList.Add("-fit");
        startInfo.ArgumentList.Add("off");
        return startInfo;
    }

    private static async Task ReadProcessOutputAsync(
        StreamReader reader,
        StringBuilder destination,
        Action<string> chunkReceived,
        Action<string>? lineReceived = null)
    {
        char[] buffer = new char[256];
        StringBuilder pendingLine = new();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
                break;
            string chunk = new(buffer, 0, read);
            destination.Append(chunk);
            pendingLine.Append(chunk);
            chunkReceived(chunk);
            ReportCompletedOutputLines(pendingLine, lineReceived);
        }
        if (pendingLine.Length > 0)
            lineReceived?.Invoke(pendingLine.ToString().TrimEnd('\r'));
    }

    private static void ReportCompletedOutputLines(StringBuilder pendingLine, Action<string>? lineReceived)
    {
        while (true)
        {
            int newline = pendingLine.ToString().IndexOf('\n');
            if (newline < 0)
                return;
            string line = pendingLine.ToString(0, newline).TrimEnd('\r');
            pendingLine.Remove(0, newline + 1);
            lineReceived?.Invoke(line);
        }
    }

    internal static bool ContainsCompleteTerminalResult(StringBuilder output)
    {
        string value = RemoveThinkingBlocks(output.ToString());
        for (int start = value.Length - 1; start >= 0; start--)
        {
            if (value[start] != '{' || !TryFindJsonObjectEnd(value, start, out int end))
                continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(value[start..(end + 1)]);
                JsonElement root = document.RootElement;
                if (IsNoAbilityResult(root))
                {
                    string? summary = ReadJsonText(root, "analysisMarkdown") ?? ReadJsonText(root, "summary");
                    return !string.IsNullOrWhiteSpace(summary);
                }
                if (root.TryGetProperty("type", out JsonElement type) &&
                    string.Equals(type.GetString(), "result", StringComparison.OrdinalIgnoreCase) &&
                    (root.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array ||
                     root.TryGetProperty("action", out _)))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }
        return false;
    }

    private static bool TryFindJsonObjectEnd(string value, int start, out int end)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = start; index < value.Length; index++)
        {
            char current = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (current == '"')
                    inString = false;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }
            if (current == '{')
            {
                depth++;
                continue;
            }
            if (current != '}')
                continue;

            depth--;
            if (depth == 0)
            {
                end = index;
                return true;
            }
            if (depth < 0)
                break;
        }

        end = -1;
        return false;
    }

    private static async Task ReadProcessLinesAsync(
        StreamReader reader,
        StringBuilder destination,
        Action<string>? lineReceived = null)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            destination.AppendLine(line);
            lineReceived?.Invoke(line);
        }
    }

    private static async Task ReportInferenceHeartbeatAsync(
        ResolvedRuntime runtime,
        Func<int> receivedCharacters,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            double seconds = stopwatch.Elapsed.TotalSeconds;
            string stage = seconds switch
            {
                < 5d => "正在将模型载入内存",
                < 15d => "模型正在读取错误与模组信息",
                _ => "模型正在推理修复方案"
            };
            double currentProgress = Math.Min(0.92d, 0.75d + (seconds / 600d * 0.17d));
            int received = receivedCharacters();
            string detail = $"{runtime.DeviceDescription} · {stopwatch.Elapsed:mm\\:ss}" +
                            (received > 0 ? $" · 已生成 {received} 字符" : string.Empty);
            progress?.Invoke(new MinecraftAiRepairProgress(stage, currentProgress, detail));
        }
    }

    internal static bool TryParseProgressEvent(string line, out MinecraftAiRepairProgress? progress)
    {
        progress = null;
        int start = line.IndexOf('{');
        int end = line.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line[start..(end + 1)]);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type) ||
                !string.Equals(type.GetString(), "progress", StringComparison.OrdinalIgnoreCase) ||
                !TryReadProgressName(root, out string progressName) ||
                !root.TryGetProperty("progress", out JsonElement progressElement) ||
                !progressElement.TryGetDouble(out double progressValue))
            {
                return false;
            }
            progress = new MinecraftAiRepairProgress(
                SanitizeStage(progressName),
                Math.Clamp(progressValue, 0d, 1d));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadProgressName(JsonElement root, out string name)
    {
        name = string.Empty;
        if (root.TryGetProperty("name", out JsonElement nameElement) &&
            nameElement.ValueKind == JsonValueKind.String)
        {
            name = nameElement.GetString() ?? string.Empty;
        }
        else if (root.TryGetProperty("stage", out JsonElement stageElement) &&
                 stageElement.ValueKind == JsonValueKind.String)
        {
            name = stageElement.GetString() ?? string.Empty;
        }
        return !string.IsNullOrWhiteSpace(name);
    }

    private static string BuildPrompt(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<string> crashLines,
        IReadOnlyList<MinecraftModMetadata> installedMods,
        MinecraftAiRepairContext repairContext,
        string languageCode,
        bool summaryOnly = false,
        bool singleJsonResult = false)
    {
        string actions = string.Join(',', fault.AllowedActions.Select(action => action.ToString()));
        StringBuilder evidence = new();
        foreach (string line in fault.Evidence.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(12))
        {
            if (evidence.Length >= 1_500)
                break;
            string safeLine = PortableLog.Redact(line);
            evidence.Append("[structured] ")
                .AppendLine(safeLine.Length > 384 ? safeLine[..384] : safeLine);
        }
        foreach (string line in (fault.StackTrace ?? string.Empty).Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(6))
        {
            if (evidence.Length >= 2_500)
                break;
            string safeLine = PortableLog.Redact(line);
            evidence.Append("[stack] ")
                .AppendLine(safeLine.Length > 384 ? safeLine[..384] : safeLine);
        }
        string outputLanguage = languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "English"
            : languageCode.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
              languageCode.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                ? "繁體中文"
                : "简体中文";
        string modMetadata = installedMods.Count == 0
            ? "未加载模组 metadata；如需确认模组依赖，请请求 instance 上下文。"
            : $"已检测到 {installedMods.Count} 个模组；完整模组 metadata 不会预装，请请求 instance 上下文。";
        string lastClass = PortableLog.Redact(fault.LastClassName);
        if (string.IsNullOrWhiteSpace(lastClass))
            lastClass = "未知";
        string modeInstruction = summaryOnly
            ? singleJsonResult
                ? "这是最终错误总结阶段。不要提出可执行修复步骤；必须输出 type=noability 的 JSON 对象，并在 analysisMarkdown 中说明原因、证据和人工建议。\n"
                : "这是最终错误总结阶段。不要提出可执行修复步骤；必须调用 noability 工具，参数中给出 analysisMarkdown、confidence，并说明原因、证据和人工建议。\n"
            : singleJsonResult
                ? "如果没有安全且有把握的白名单修复动作，必须输出 type=noability 的 JSON 对象，并在 analysisMarkdown 中给出错误总结。\n"
                : "如果没有安全且有把握的白名单修复动作，必须调用 noability 工具，参数中给出 analysisMarkdown、confidence，并开始错误总结。\n";
        string exitCodeEvidence = FormatProcessExitCodeEvidence(repairContext.ProcessExitCode);
        string jvmHostInstruction = IsPreMainJvmHostInitializationFailure(fault)
            ? "\n[JvmHost 主类前故障硬约束]\n" + BuildJvmHostIsolationInstruction() + "\n"
            : string.Empty;
        return modeInstruction + jvmHostInstruction +
               "你是 PCL N 的 Minecraft 崩溃分析器。常规分析器已经给出结构化证据；请生成清楚、克制的分析。" +
               "你可以给出 1 到 4 个有先后依赖的修复步骤；每一步只能从允许动作中选择，不得生成命令或文件路径。" +
               "不要输出隐藏思维过程，只输出可审计的步骤依据。模组操作只能使用下方 metadata 中存在的 modId，DownloadMod 除外。\n" +
               "证据优先级：操作系统进程退出码与 native crash 文件高于普通日志尾部。" +
               "Stopping!、Disconnected from server、关闭资源等文本只表示部分关闭流程曾执行，" +
               "如果进程退出码非 0，不得据此声称游戏正常退出、用户主动退出或属于假性崩溃。" +
               "Windows NTSTATUS 异常终止应优先检查 hs_err_pid、JVM native crash、显卡驱动和 native 组件；" +
               "没有足够证据时不得臆测具体模组。\n" +
               $"analysisMarkdown 必须使用 {outputLanguage}。\n" +
               (singleJsonResult
                   ? "仍然必须先输出 progress JSON 行；然后只输出一个最终 JSON 对象。不得输出 Markdown 代码围栏或其他文字。进度对象格式为 {\"type\":\"progress\",\"name\":\"当前进度名称\",\"progress\":0到1}。信息不足时输出 context_request；能安全修复时输出 result；否则输出 noability。宿主会严格验证所有字段和动作。\n"
                   : "每当分析阶段变化时，立即单独输出一行 progress JSON，必须包含当前进度名称 name 和 0 到 1 的 progress，例如：" +
                     "{\"type\":\"progress\",\"name\":\"正在归纳异常\",\"progress\":0.25}。至少输出归纳异常、匹配安全修复动作两个阶段；最后输出 result 或 noability JSON。\n") +
               "需要查找缺失模组时，先输出 {\"type\":\"mod_search\",\"query\":\"缺失依赖名称，例如 mafglib\",\"name\":\"正在搜索缺失前置\",\"progress\":0.75}。" +
               "宿主会返回多个候选项目的 source、projectId、slug 和 title，但不会替你选择。你必须比较候选与缺失依赖名称，" +
               "再输出 {\"type\":\"mod_project_details\",\"source\":\"候选的 Modrinth 或 CurseForge\",\"projectId\":\"候选的 projectId\",\"name\":\"正在读取项目兼容详情\",\"progress\":0.82}。" +
               "读取详情后，若选择 DownloadMod，steps[].modId 必须填写该候选的 projectId（不是依赖声明 ID 或 slug，绝不能为 null），" +
               "steps[].modVersion 必须精确填写 compatible_versions 中的版本；不得只把 ProjectId 写在 stage、rationale 或 analysisMarkdown。\n" +
               "初始上下文只包含摘要；不要假设未提供的日志、模组或环境细节。需要详细信息时，使用白名单 context_request 工具协议：" +
               "{\"type\":\"context_request\",\"scopes\":[\"environment\",\"instance\",\"crash_reports\",\"runtime_logs\",\"launch_method\",\"login_method\"]," +
               "\"name\":\"正在读取必要信息\",\"progress\":0.8}。scopes 只选择确实需要的类别；宿主只会返回脱敏的只读信息，且只允许请求一次。\n" +
               "信息足够时，最后严格输出一行结果 JSON：{\"type\":\"result\",\"analysisMarkdown\":\"含原因、证据、处理建议的 Markdown，不超过300字\",\"confidence\":0到1," +
               "\"steps\":[{\"action\":\"允许动作之一\",\"stage\":\"当前步骤，最多32字\",\"progress\":0到1," +
               "\"rationale\":\"该步骤的简短可审计依据\",\"modId\":null,\"modVersion\":null,\"javaMajorVersion\":null," +
               "\"loaderKind\":null,\"loaderVersion\":null}]}\n" +
               "DisableMod 必须给出已安装 modId；UpdateMod 必须给出已安装 modId 和目标 modVersion；DownloadMod 必须给出项目 modId。" +
               "如果允许动作包含 InstallMissingModDependencies 且证据表明前置缺失，应优先选择该动作；该动作不需要填写 modId，宿主只会安装自己已解析验证的缺失依赖。" +
               "Java 与加载器字段仅是建议，宿主会重新验证兼容性和可用版本。\n" +
               $"当前环境：Minecraft={repairContext.MinecraftVersion}；Loader={repairContext.Loader}；" +
               $"Java={repairContext.JavaMajorVersion?.ToString(CultureInfo.InvariantCulture) ?? "未知"}；" +
               $"内存MiB={repairContext.MemoryMegabytes?.ToString(CultureInfo.InvariantCulture) ?? "未知"}；" +
               $"系统={repairContext.OperatingSystem}/{repairContext.Architecture}；" +
               $"模组数={repairContext.InstalledModCount}；错误日志行数={repairContext.CrashLogLineCount}\n" +
               $"进程退出证据：{exitCodeEvidence}\n" +
               $"允许动作：{actions}\n错误代码：{fault.Code}\n子系统：{fault.Subsystem}\n节点：{fault.Stage}\n" +
               $"最后类：{lastClass}\n" +
               $"异常：{PortableLog.Redact(fault.ExceptionType)}: {PortableLog.Redact(fault.Message)}\n" +
               $"模组摘要：{modMetadata}\n初始关键证据（详细信息请通过工具获取）：\n{evidence}";
    }

    internal static string FormatProcessExitCodeEvidence(int? exitCode)
    {
        if (exitCode is null)
            return "未捕获退出码，不能据此判断正常或异常退出。";
        uint unsigned = unchecked((uint)exitCode.Value);
        string classification = unsigned switch
        {
            0 => "正常退出",
            0xC0000005 => "Windows STATUS_ACCESS_VIOLATION（原生内存访问异常）",
            0xC000001D => "Windows STATUS_ILLEGAL_INSTRUCTION（非法指令）",
            0xC0000094 => "Windows STATUS_INTEGER_DIVIDE_BY_ZERO（整数除零）",
            0xC0000409 => "Windows STATUS_STACK_BUFFER_OVERRUN（原生快速失败/安全检查失败）",
            _ when (unsigned & 0xC0000000u) == 0xC0000000u => "Windows NTSTATUS 异常终止",
            _ when exitCode.Value != 0 => "非零退出码，属于异常终止",
            _ => "正常退出"
        };
        return $"Signed={exitCode.Value.ToString(CultureInfo.InvariantCulture)}；" +
               $"Hex=0x{unsigned:X8}；Classification={classification}。";
    }

    internal static string BuildBoundedRepairPrompt(
        string basePrompt,
        string durableToolContext,
        string? latestModelOutput,
        string? latestFeedback,
        int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        if (string.IsNullOrEmpty(durableToolContext) &&
            string.IsNullOrEmpty(latestModelOutput) &&
            string.IsNullOrEmpty(latestFeedback))
        {
            return BoundDetailedContext(basePrompt, maximumLength);
        }

        const string toolsHeader = "\n\n[本会话持久工具结果]\n";
        const string outputHeader = "\n\n[模型最近一轮输出]\n";
        const string feedbackHeader = "\n\n[宿主最近一次指令或校验反馈]\n";
        int headerLength = toolsHeader.Length + outputHeader.Length + feedbackHeader.Length;
        if (maximumLength <= headerLength + 4)
            return BoundDetailedContext(basePrompt, maximumLength);
        int available = maximumLength - headerLength;
        int baseBudget = Math.Max(1, available * 45 / 100);
        int toolBudget = Math.Max(1, available * 35 / 100);
        int outputBudget = Math.Max(1, available * 10 / 100);
        int feedbackBudget = Math.Max(1, available - baseBudget - toolBudget - outputBudget);
        string boundedBase = BoundDetailedContext(basePrompt, baseBudget);
        string boundedTools = BoundDetailedContext(durableToolContext, toolBudget);
        string boundedOutput = BoundDetailedContext(latestModelOutput ?? string.Empty, outputBudget);
        string boundedFeedback = BoundDetailedContext(latestFeedback ?? string.Empty, feedbackBudget);
        return boundedBase + toolsHeader + boundedTools + outputHeader + boundedOutput +
               feedbackHeader + boundedFeedback;
    }

    internal static int EstimateTokenCount(string value) =>
        (value.Length + 2) / 3;

    internal static string BoundDetailedContext(string value, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        if (value.Length <= maximumLength)
            return value;

        const string marker = "\n[上下文中段已由宿主截断]\n";
        int available = maximumLength - marker.Length;
        if (available <= 1)
            return value[..maximumLength];
        int headLength = available / 2;
        int tailLength = available - headLength;
        return value[..headLength] + marker + value[^tailLength..];
    }

    private static string BuildContextDetail(MinecraftAiRepairContext context) =>
        $"Minecraft {context.MinecraftVersion} · {context.Loader} · " +
        (context.JavaMajorVersion is { } java ? $"Java {java} · " : string.Empty) +
        $"{context.InstalledModCount} 个模组 · {context.CrashLogLineCount} 行错误信息";

    private static string SanitizeStage(string? value)
    {
        string normalized = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "模型已生成修复计划";
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static RuntimePackage ResolveRuntimePackage()
    {
        string runtimeId;
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            runtimeId = "win-x64";
        else if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            runtimeId = "win-arm64";
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            runtimeId = "linux-x64";
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            runtimeId = "linux-arm64";
        else if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            runtimeId = "osx-x64";
        else if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            runtimeId = "osx-arm64";
        else
            throw new PlatformNotSupportedException(
                $"当前平台没有内置 llama.cpp 运行时：{RuntimeInformation.OSDescription}/{RuntimeInformation.ProcessArchitecture}。" +
                "可在实验性设置中指定自定义 llama.cpp 路径。");

        return ResolveRuntimePackage(runtimeId);
    }

    internal static RuntimePackage ResolveRuntimePackage(string runtimeId)
    {
        (string archive, string sha256) = runtimeId switch
        {
            "win-x64" => ("llama-b9637-bin-win-cpu-x64.zip", "f7783c2b8c007f95e710ac40f26a24861a80b603b0b739fc54d7c926a4716c1e"),
            "win-arm64" => ("llama-b9637-bin-win-cpu-arm64.zip", "db1d3f4c13c08b693f539e100bf6d3a435148b0ffc186b044fdd65d490cc6df7"),
            "linux-x64" => ("llama-b9637-bin-ubuntu-x64.tar.gz", "a50ee14f021a9d8e92e30f622f7e3be1318ee1125bb9a9ba8d2025388df48743"),
            "linux-arm64" => ("llama-b9637-bin-ubuntu-arm64.tar.gz", "211d9e9ee738698beb7ca271be82661ae2b5da3fbb489cf7d9e4e6ed601be106"),
            "osx-x64" => ("llama-b9637-bin-macos-x64.tar.gz", "71743f8db0958e7c266cceb7add7b16aa418a964667e471094aa6ae65b9c8298"),
            "osx-arm64" => ("llama-b9637-bin-macos-arm64.tar.gz", "72a93f3e68c31de3e438d462669aad1fcdb423b995e9c41033cc7d27a9a3ac69"),
            _ => throw new PlatformNotSupportedException("不支持的模型运行时 RID：" + runtimeId)
        };
        return CreateRuntimePackage(runtimeId, archive, sha256, "CPU");
    }

    internal static RuntimePackage? ResolveGpuRuntimePackage(string runtimeId)
    {
        (string packageId, string archive, string sha256, string backend)? package = runtimeId switch
        {
            "win-x64" => (
                "win-x64-vulkan",
                "llama-b9637-bin-win-vulkan-x64.zip",
                "a353945604cffdac3d0d6da6392de78ca565a531a6f2ff3521f44b9b7c6e553f",
                "Vulkan GPU"),
            "win-arm64" => (
                "win-arm64-opencl",
                "llama-b9637-bin-win-opencl-adreno-arm64.zip",
                "5aefa4f2a80630a471662539ef530f61fb4a5a14fd4e557e8edef3e6757614a6",
                "OpenCL GPU"),
            "linux-x64" => (
                "linux-x64-vulkan",
                "llama-b9637-bin-ubuntu-vulkan-x64.tar.gz",
                "6ca268d758aae9e8518afa43042678e8b60b47f0d34df7d6efff4ca622c74313",
                "Vulkan GPU"),
            "linux-arm64" => (
                "linux-arm64-vulkan",
                "llama-b9637-bin-ubuntu-vulkan-arm64.tar.gz",
                "65f64966d412fbf64e6d024b206dbb63615bf19eb94631c02907e3927995c74e",
                "Vulkan GPU"),
            "osx-x64" => (
                "osx-x64-metal",
                "llama-b9637-bin-macos-x64.tar.gz",
                "71743f8db0958e7c266cceb7add7b16aa418a964667e471094aa6ae65b9c8298",
                "Metal GPU"),
            "osx-arm64" => (
                "osx-arm64-metal",
                "llama-b9637-bin-macos-arm64.tar.gz",
                "72a93f3e68c31de3e438d462669aad1fcdb423b995e9c41033cc7d27a9a3ac69",
                "Metal GPU"),
            _ => null
        };
        return package is { } value
            ? CreateRuntimePackage(value.packageId, value.archive, value.sha256, value.backend)
            : null;
    }

    private static RuntimePackage CreateRuntimePackage(
        string runtimeId,
        string archive,
        string sha256,
        string backend) =>
        new(
            runtimeId,
            archive,
            sha256,
            [
                new Uri($"https://sourceforge.net/projects/llama-cpp.mirror/files/b9637/{archive}/download"),
                new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/b9637/{archive}")
            ],
            backend);

    private static void ExtractRuntimeArchive(string archivePath, string destinationDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
            return;
        }

        using FileStream input = File.OpenRead(archivePath);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
    }

    private static void EnsureExecutableBit(string executablePath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(executablePath))
            return;
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    internal static LocalModelPackage ResolveLocalModel(MinecraftAiLocalModel model) =>
        LocalModels.FirstOrDefault(package => package.Model == model)
        ?? throw new ArgumentOutOfRangeException(nameof(model), model, "不支持的内置本地模型。");

    internal sealed record LocalModelPackage(
        MinecraftAiLocalModel Model,
        string DisplayName,
        string FileName,
        string Sha256,
        long ApproximateBytes,
        IReadOnlyList<Uri> Urls);

    private static string NormalizeSha256(string value)
    {
        string normalized = value.Trim();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new FormatException("模型 SHA-256 必须是 64 位十六进制字符串。");
        return normalized;
    }

    internal sealed record RuntimePackage(
        string RuntimeId,
        string ArchiveFileName,
        string Sha256,
        IReadOnlyList<Uri> Urls,
        string Backend);

    internal static string ResolveServerExecutable(string completionPath)
    {
        string fullPath = Path.GetFullPath(completionPath);
        string serverPath = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        if (!File.Exists(serverPath))
            throw new FileNotFoundException("llama.cpp 运行时目录中缺少 llama-server。", serverPath);
        return serverPath;
    }

    internal static ProcessStartInfo CreateServerStartInfo(
        string serverPath,
        string modelPath,
        int port,
        string apiKey,
        bool useGpu,
        int contextSize)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = serverPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(serverPath) ?? Environment.CurrentDirectory
        };
        void Add(string name, string value)
        {
            startInfo.ArgumentList.Add(name);
            startInfo.ArgumentList.Add(value);
        }
        Add("--model", modelPath);
        Add("--host", "127.0.0.1");
        Add("--port", port.ToString(CultureInfo.InvariantCulture));
        Add("--api-key", apiKey);
        Add("--ctx-size", contextSize.ToString(CultureInfo.InvariantCulture));
        Add("--parallel", "1");
        Add("--gpu-layers", useGpu ? "all" : "0");
        Add("--fit", "off");
        Add("--cache-ram", "2048");
        startInfo.ArgumentList.Add("--cache-prompt");
        startInfo.ArgumentList.Add("--no-ui");
        startInfo.ArgumentList.Add("--no-warmup");
        return startInfo;
    }

    internal static JsonObject CreateLocalServerCompletionPayload(string prompt, int tokenBudget) =>
        new()
        {
            ["prompt"] = prompt,
            ["n_predict"] = NormalizeTokenBudget(tokenBudget),
            ["stream"] = true,
            ["cache_prompt"] = true,
            ["temperature"] = 0.1,
            ["top_k"] = 20
        };

    private sealed record ResolvedRuntime(string ExecutablePath, bool UseGpu, string DeviceDescription);

    private sealed class MinecraftAiLocalServerSession : IDisposable, IAsyncDisposable
    {
        private readonly Process _process;
        private readonly HttpClient _client;
        private readonly StringBuilder _diagnostics = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _stdout;
        private readonly Task _stderr;
        private bool _disposed;

        private MinecraftAiLocalServerSession(
            Process process, HttpClient client, string serverPath, string modelPath,
            bool useGpu, string deviceDescription, int contextSize)
        {
            _process = process;
            _client = client;
            ServerPath = serverPath;
            ModelPath = modelPath;
            UseGpu = useGpu;
            DeviceDescription = deviceDescription;
            ContextSize = contextSize;
            _stdout = DrainDiagnosticsAsync(process.StandardOutput, _diagnostics, _lifetime.Token);
            _stderr = DrainDiagnosticsAsync(process.StandardError, _diagnostics, _lifetime.Token);
        }

        public string ServerPath { get; }
        public string ModelPath { get; }
        public bool UseGpu { get; }
        public string DeviceDescription { get; }
        public int ContextSize { get; }
        public int ProcessId => _process.Id;
        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public bool Matches(string serverPath, string modelPath, bool useGpu, int contextSize) =>
            string.Equals(ServerPath, serverPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
            UseGpu == useGpu && ContextSize == contextSize;

        public static async Task<MinecraftAiLocalServerSession> StartAsync(
            string serverPath, string modelPath, bool useGpu, string deviceDescription,
            int contextSize, CancellationToken cancellationToken)
        {
            int port = ReserveLoopbackPort();
            string apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            Process process = Process.Start(CreateServerStartInfo(
                                  serverPath, modelPath, port, apiKey, useGpu, contextSize))
                              ?? throw new InvalidOperationException("无法启动本地 llama-server。");
            HttpClient client = new()
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            MinecraftAiLocalServerSession session = new(
                process, client, serverPath, modelPath, useGpu, deviceDescription, contextSize);
            try
            {
                await session.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
                PortableLog.Info("MinecraftRepairAI",
                    $"本地模型服务已就绪；PID={process.Id}；Device={deviceDescription}；Context={contextSize}。");
                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<string> InferAsync(
            string prompt, int tokenBudget, Action<MinecraftAiRepairProgress>? progress,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HasExited)
                throw new InvalidOperationException("本地模型服务已退出：" + GetDiagnostics());
            JsonObject payload = CreateLocalServerCompletionPayload(prompt, tokenBudget);
            using HttpRequestMessage request = new(HttpMethod.Post, "completion")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            Stopwatch stopwatch = Stopwatch.StartNew();
            int receivedCharacters = 0;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            timeout.CancelAfter(UseGpu ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(8));
            using CancellationTokenSource heartbeatCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task heartbeat = ReportLocalServerHeartbeatAsync(
                DeviceDescription,
                ProcessId,
                () => Volatile.Read(ref receivedCharacters),
                progress,
                heartbeatCancellation.Token);
            try
            {
                using HttpResponseMessage response = await _client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"llama-server 返回 HTTP {(int)response.StatusCode}：{SanitizeAnalysis(error)}");
                }
                await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using StreamReader reader = new(stream, Encoding.UTF8);
                StringBuilder output = new();
                StringBuilder pendingLine = new();
                string? terminalMetadata = null;
                while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string data = line[5..].Trim();
                    if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                        continue;
                    using JsonDocument chunk = JsonDocument.Parse(data);
                    terminalMetadata = FormatLocalServerTerminalMetadata(chunk.RootElement) ?? terminalMetadata;
                    string content = ReadJsonText(chunk.RootElement, "content") ?? string.Empty;
                    if (content.Length == 0)
                        continue;
                    output.Append(content);
                    pendingLine.Append(content);
                    Interlocked.Add(ref receivedCharacters, content.Length);
                    if (output.Length > 64_000)
                        throw new InvalidDataException("本地模型单轮输出超过 64000 字符，已停止生成。");
                    ReportCompletedOutputLines(pendingLine, value =>
                    {
                        if (TryParseProgressEvent(value, out MinecraftAiRepairProgress? modelProgress) &&
                            modelProgress is not null)
                        {
                            progress?.Invoke(modelProgress with
                            {
                                Progress = 0.78d + Math.Clamp(modelProgress.Progress, 0d, 1d) * 0.14d,
                                Detail = DeviceDescription + " · cached server"
                            });
                        }
                    });
                    if (ContainsCompleteTerminalResult(output))
                    {
                        PortableLog.Info(
                            "MinecraftRepairAI",
                            $"本地模型服务已生成完整终态结果，提前结束当前请求；PID={ProcessId}。");
                        break;
                    }
                }
                PortableLog.Info("MinecraftRepairAI",
                    $"本地模型服务推理完成；PID={ProcessId}；ElapsedMs={stopwatch.ElapsedMilliseconds}；" +
                    $"Characters={output.Length}；PromptCache=enabled；" +
                    $"Terminal={terminalMetadata ?? "unavailable"}。");
                return output.ToString();
            }
            finally
            {
                heartbeatCancellation.Cancel();
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (HasExited)
                    throw new InvalidOperationException("llama-server 在模型加载期间退出：" + GetDiagnostics());
                try
                {
                    using HttpResponseMessage response = await _client.GetAsync("health", timeout.Token)
                        .ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return;
                    if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                    {
                        string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                        throw new HttpRequestException(
                            $"llama-server 健康检查返回 HTTP {(int)response.StatusCode}：{SanitizeAnalysis(body)}");
                    }
                }
                catch (HttpRequestException) when (!HasExited)
                {
                }
                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }
        }

        private static int ReserveLoopbackPort()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static async Task DrainDiagnosticsAsync(
            StreamReader reader, StringBuilder destination, CancellationToken cancellationToken)
        {
            char[] buffer = new char[512];
            try
            {
                while (true)
                {
                    int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        return;
                    lock (destination)
                    {
                        destination.Append(buffer, 0, read);
                        if (destination.Length > 12_000)
                            destination.Remove(0, destination.Length - 12_000);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private string GetDiagnostics()
        {
            lock (_diagnostics)
                return FormatRuntimeError(_diagnostics.ToString());
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            _client.Dispose();
            _process.Dispose();
            _lifetime.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
            _client.Dispose();
            _process.Dispose();
            _lifetime.Dispose();
        }
    }

    private static async Task ReportLocalServerHeartbeatAsync(
        string deviceDescription,
        int processId,
        Func<int> receivedCharacters,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            int received = receivedCharacters();
            progress?.Invoke(new MinecraftAiRepairProgress(
                received > 0 ? "本地模型正在生成链式修复计划" : "等待本地模型服务响应",
                Math.Min(0.92d, 0.75d + stopwatch.Elapsed.TotalSeconds / 600d * 0.17d),
                $"{deviceDescription} · PID {processId} · {stopwatch.Elapsed:mm\\:ss}" +
                (received > 0 ? $" · 已生成 {received} 字符" : string.Empty)));
        }
    }

    private static string? ReadSafeToken(JsonElement root, string propertyName, int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) || element.ValueKind != JsonValueKind.String)
            return null;
        string? value = element.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '+' or ':')))
        {
            return null;
        }
        return value;
    }

    private static int? ReadJavaMajor(JsonElement root)
    {
        if (!root.TryGetProperty("javaMajorVersion", out JsonElement element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out int value))
        {
            return null;
        }
        return value is >= 8 and <= 99 ? value : null;
    }

    private static string? ReadLoaderKind(JsonElement root)
    {
        string? value = ReadSafeToken(root, "loaderKind", 32);
        return value?.ToLowerInvariant() is "fabric" or "legacyfabric" or "quilt" or "forge" or "neoforge" or
            "cleanroom" ? value : null;
    }

    internal static string ResolveCompletionExecutable(string configuredPath)
    {
        string fullPath = Path.GetFullPath(configuredPath);
        string fileName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.Equals(fileName, "llama-completion", StringComparison.OrdinalIgnoreCase))
            return fullPath;
        if (string.Equals(fileName, "llama-cli", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "llama", StringComparison.OrdinalIgnoreCase))
        {
            string sibling = Path.Combine(
                Path.GetDirectoryName(fullPath)!,
                OperatingSystem.IsWindows() ? "llama-completion.exe" : "llama-completion");
            if (File.Exists(sibling))
                return sibling;
            throw new FileNotFoundException(
                "当前 llama.cpp 版本不再支持通过 llama-cli 执行单次补全，请在同一目录提供 llama-completion。",
                sibling);
        }
        throw new InvalidOperationException(
            "本地模型运行时必须指向 llama-completion，或指向旁边包含 llama-completion 的 llama-cli。");
    }

    private static string? FindExecutable(string name)
    {
        string executableName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    private sealed class TemporaryPromptFile : IDisposable
    {
        private TemporaryPromptFile(string path) => Path = path;

        public string Path { get; }

        public static TemporaryPromptFile Create(string prompt)
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCL-N", "AiPrompts");
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, prompt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new TemporaryPromptFile(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PortableLog.Warn(ex, "MinecraftRepairAI", "清理本地模型临时提示词失败。");
            }
        }
    }

    private static string ProjectKey(CommunityResourceSource source, string projectId) =>
        source + ":" + projectId;

    private static string FormatModSearchResults(CommunityResourceEntry[] entries)
    {
        if (entries.Length == 0)
            return "未找到候选。请更换搜索关键词，无法确认时调用 noability。";
        return string.Join('\n', entries.Select(entry =>
            $"- source={entry.Source}; projectId={entry.ProjectId}; slug={entry.Slug}; title={entry.Title}"));
    }

    private static string FormatModProjectDetails(
        CommunityResourceEntry entry,
        IReadOnlyList<CommunityResourceVersion> versions,
        CommunityResourceVersion[] compatible,
        MinecraftAiRepairContext context)
    {
        string loaders = string.Join(',', versions.SelectMany(version => version.Loaders)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(32));
        string gameVersions = string.Join(',', versions.SelectMany(version => version.GameVersions)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(64));
        string compatibleVersions = compatible.Length == 0
            ? "none"
            : string.Join(',', compatible.Select(version =>
                $"{version.VersionNumber}(versionId={version.VersionId})"));
        return $"source={entry.Source}; projectId={entry.ProjectId}; title={entry.Title}\n" +
               $"supportedLoaders={loaders}\nsupportedMinecraftVersions={gameVersions}\n" +
               $"compatibleFor={context.MinecraftVersion}/{context.Loader}\n" +
               $"compatibleProjectVersions={compatibleVersions}\n" +
               "只有 compatibleProjectVersions 非 none 时才能选择 DownloadMod。";
    }

    private static bool HasVerifiedDownloadProjects(
        MinecraftAiRepairSuggestion suggestion,
        IReadOnlyDictionary<string, CommunityResourceEntry> searchedProjects,
        Dictionary<string, HashSet<string>> verifiedProjectVersions)
    {
        foreach (MinecraftAiRepairStep step in suggestion.RepairSteps)
        {
            if (step.Action != MinecraftRepairActionKind.DownloadMod)
                continue;
            string? projectId = step.Parameters.ModId;
            if (string.IsNullOrWhiteSpace(projectId))
                return false;
            string? key = searchedProjects.Keys.FirstOrDefault(candidate =>
                candidate.EndsWith(":" + projectId, StringComparison.OrdinalIgnoreCase));
            if (key is null || !verifiedProjectVersions.TryGetValue(key, out HashSet<string>? versions) || versions.Count == 0)
                return false;
            if (!string.IsNullOrWhiteSpace(step.Parameters.ModVersion) &&
                !versions.Contains(step.Parameters.ModVersion))
            {
                return false;
            }
        }
        return true;
    }

    private static string? NormalizeProjectId(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 128 ||
            normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-')))
        {
            return null;
        }
        return normalized;
    }

    private static string BuildRepairValidationFeedback(
        IReadOnlyCollection<MinecraftRepairActionKind> allowedActions,
        string output,
        int round) =>
        $"第 {round} 轮修复计划未通过宿主安全校验。" +
        $"允许动作仅为：{string.Join(',', allowedActions)}。" +
        "result 必须包含 1 到 4 个 steps；每步必须包含字符串 action、字符串 stage、0 到 1 的数字 progress。" +
        "DisableMod 必须使用已安装模组列表中的 modId；UpdateMod 还必须提供目标 modVersion；" +
        "DownloadMod 必须提供项目 modId；InstallMissingModDependencies 不需要 modId，且只安装宿主已验证的缺失依赖。" +
        $"上一轮输出摘要：{FormatProcessOutputTail(output)}";

    private static string FormatProcessOutputTail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";
        string normalized = value.ReplaceLineEndings(" ").Trim();
        return SanitizeAnalysis(normalized.Length <= 600 ? normalized : normalized[^600..]);
    }

    private static string FormatRuntimeError(string error)
    {
        string[] lines = error.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return "运行时未提供错误信息。";

        string[] important = lines
            .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("unknown argument", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("grammar", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("schema", StringComparison.OrdinalIgnoreCase))
            .TakeLast(8)
            .ToArray();
        string[] selected = important.Length > 0 ? important : lines.TakeLast(12).ToArray();
        return SanitizeAnalysis(string.Join(" | ", selected));
    }

    private static string SanitizeAnalysis(string value)
    {
        string normalized = PortableLog.Redact(value)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 1_500 ? normalized : normalized[..1_500];
    }

    private static string RemoveThinkingBlocks(string value)
    {
        string result = value;
        while (true)
        {
            int start = result.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return result;
            int end = result.IndexOf("</think>", start + 7, StringComparison.OrdinalIgnoreCase);
            result = end < 0
                ? result[..start]
                : result.Remove(start, end + 8 - start);
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.0} GiB",
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.0} MiB",
        >= 1024L => $"{bytes / 1024d:0.0} KiB",
        _ => bytes + " B"
    };
}
