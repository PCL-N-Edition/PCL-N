// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Application.Downloads;
using PCL.Application.Launching;
using PCL.Core.Logging;
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
    IReadOnlyList<MinecraftAiRepairStep>? Steps = null)
{
    public IReadOnlyList<MinecraftAiRepairStep> RepairSteps => Steps is { Count: > 0 }
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

internal sealed record MinecraftAiModelOptions(
    string? ModelPath = null,
    string? ModelSha256 = null,
    string? RuntimePath = null,
    MinecraftAiProvider Provider = MinecraftAiProvider.Local,
    string? ApiBaseUrl = null,
    string? ApiModel = null,
    string? ApiKey = null,
    MinecraftAiReasoningEffort ReasoningEffort = MinecraftAiReasoningEffort.Medium);

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
    int CrashLogLineCount);

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

/// <summary>
/// Runs a small local model as an advisor for the deterministic repair pipeline. The model never
/// receives a shell/tool surface: its only accepted output is one action already allowlisted by
/// <see cref="MinecraftLaunchFaultReport.AllowedActions"/>.
/// </summary>
internal sealed class MinecraftAiRepairAdvisor
{
    internal const string ModelName = "Qwen2.5-Coder-0.5B-Instruct Q4_K_M";
    internal const long ApproximateModelBytes = 491_000_000;
    internal const string ModelSha256 = "1d9614638d18024d0fbb36575a15f1302a3adf044df10345688ec4f6e1c4ff32";
    private const string ModelFileName = "qwen2.5-coder-0.5b-instruct-q4_k_m.gguf";
    private static readonly Uri[] ModelUrls =
    [
        new("https://modelscope.cn/models/Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF/resolve/master/" + ModelFileName),
        new("https://hf-mirror.com/Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF/resolve/main/" + ModelFileName),
        new("https://huggingface.co/Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF/resolve/main/" + ModelFileName)
    ];

    private readonly string _rootDirectory;
    private readonly HttpClient _httpClient;

    public MinecraftAiRepairAdvisor(HttpClient? httpClient = null, string? rootDirectory = null)
    {
        DefaultPlatformPathProvider paths = new();
        _rootDirectory = rootDirectory ?? Path.Combine(
            paths.ApplicationDataDirectory,
            "PCL-N",
            "AI",
            "MinecraftRepair-0.5B");
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(crashLines);
        ArgumentNullException.ThrowIfNull(installedMods);
        ArgumentNullException.ThrowIfNull(repairContext);
        if (fault.AllowedActions.Length == 0)
            return null;

        string contextDetail = BuildContextDetail(repairContext);
        progress?.Invoke(new MinecraftAiRepairProgress("整理当前游戏信息", 0.7d, contextDetail));
        string prompt = BuildPrompt(fault, crashLines, installedMods, repairContext, languageCode);
        Func<string, CancellationToken, Task<string>> inference;
        if (options.Provider == MinecraftAiProvider.OpenAiCompatible)
        {
            progress?.Invoke(new MinecraftAiRepairProgress(
                "正在连接 OpenAI 兼容 API",
                0.72d,
                contextDetail));
            inference = (input, token) => RunOpenAiCompatibleInferenceAsync(options, input, progress, token);
        }
        else
        {
            Directory.CreateDirectory(_rootDirectory);
            ResolvedRuntime runtime = await ResolveRuntimeAsync(options, progress, cancellationToken).ConfigureAwait(false);
            string modelPath;
            if (!string.IsNullOrWhiteSpace(options.ModelPath))
            {
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
                modelPath = Path.Combine(_rootDirectory, ModelFileName);
                await EnsureDownloadedFileAsync(
                        ModelUrls,
                        modelPath,
                        ModelSha256,
                        "0.5B 模型",
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
        string output = await inference(prompt, cancellationToken).ConfigureAwait(false);
        MinecraftAiContextRequest? contextRequest = ParseContextRequest(output);
        if (contextRequest is not null)
        {
            if (contextProvider is null)
                throw new InvalidOperationException("模型请求了详细上下文，但当前启动会话无法提供。");
            progress?.Invoke(new MinecraftAiRepairProgress(
                contextRequest.Stage,
                0.9d,
                "读取：" + string.Join(", ", contextRequest.Scopes)));
            string detailedContext = await contextProvider(contextRequest.Scopes, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(detailedContext))
                throw new InvalidDataException("模型请求的上下文为空，无法继续推理。");
            int contextLimit = options.Provider == MinecraftAiProvider.OpenAiCompatible ? 48_000 : 14_000;
            detailedContext = BoundDetailedContext(detailedContext, contextLimit);
            string followUpPrompt = prompt +
                                    "\n\n宿主根据你的白名单请求返回以下脱敏只读上下文。" +
                                    "现在必须输出最终 progress JSON 行和 result JSON；不得再次请求上下文。\n" +
                                    detailedContext;
            progress?.Invoke(new MinecraftAiRepairProgress("模型正在结合详细上下文复核", 0.91d, contextDetail));
            output = await inference(followUpPrompt, cancellationToken).ConfigureAwait(false);
        }
        progress?.Invoke(new MinecraftAiRepairProgress("验证模型修复计划", 0.94d));
        MinecraftAiRepairSuggestion? suggestion = ParseSuggestion(output, fault.AllowedActions);
        if (suggestion is null)
            PortableLog.Warn("MinecraftRepairAI", "模型没有返回有效的白名单修复方案。");
        return suggestion;
    }

    internal static MinecraftAiRepairSuggestion? ParseSuggestion(
        string output,
        IReadOnlyCollection<MinecraftRepairActionKind> allowedActions)
    {
        if (string.IsNullOrWhiteSpace(output) || allowedActions.Count == 0)
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
                string stage = root.TryGetProperty("stage", out JsonElement stageElement)
                    ? SanitizeStage(stageElement.GetString())
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

    private static bool TryReadRepairStep(
        JsonElement element,
        IReadOnlyCollection<MinecraftRepairActionKind> allowedActions,
        out MinecraftAiRepairStep? step)
    {
        step = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("action", out JsonElement actionElement) ||
            !Enum.TryParse(actionElement.GetString(), ignoreCase: true, out MinecraftRepairActionKind action) ||
            !allowedActions.Contains(action) ||
            !element.TryGetProperty("stage", out JsonElement stageElement) ||
            stageElement.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("progress", out JsonElement progressElement) ||
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
            string fullPath = Path.GetFullPath(customRuntime);
            bool customGpu = await ProbeGpuAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return new ResolvedRuntime(fullPath, customGpu, customGpu ? "自定义 GPU" : "自定义 CPU");
        }
        string? configured = Environment.GetEnvironmentVariable("PCL_LLAMA_CLI");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            string fullPath = Path.GetFullPath(configured.Trim());
            bool configuredGpu = await ProbeGpuAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return new ResolvedRuntime(fullPath, configuredGpu, configuredGpu ? "环境变量 GPU" : "环境变量 CPU");
        }
        string? pathExecutable = FindExecutable("llama-cli") ?? FindExecutable("llama");
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
            return new ResolvedRuntime(Path.GetFullPath(options.RuntimePath.Trim()), false, "自定义 CPU");
        string? configured = Environment.GetEnvironmentVariable("PCL_LLAMA_CLI");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
            return new ResolvedRuntime(Path.GetFullPath(configured.Trim()), false, "环境变量 CPU");
        string? pathExecutable = FindExecutable("llama-cli") ?? FindExecutable("llama");
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
            OperatingSystem.IsWindows() ? "llama-cli.exe" : "llama-cli");
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
                    OperatingSystem.IsWindows() ? "llama-cli.exe" : "llama-cli",
                    SearchOption.AllDirectories)
                .FirstOrDefault();
            if (extractedExecutable is null)
                throw new InvalidDataException("llama.cpp 运行时包缺少 llama-cli.exe。");
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
        Exception? lastFailure = null;
        foreach (Uri uri in sources)
        {
            string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long? length = response.Content.Headers.ContentLength;
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream target = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[128 * 1024];
                long received = 0;
                int lastPercent = -1;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (length is > 0)
                    {
                        int percent = (int)Math.Clamp(received * 100 / length.Value, 0, 100);
                        if (percent >= lastPercent + 5)
                        {
                            lastPercent = percent;
                            double normalized = progressStart + ((progressEnd - progressStart) * percent / 100d);
                            progress?.Invoke(new MinecraftAiRepairProgress(
                                "下载" + displayName,
                                normalized,
                                $"{percent}%（{FormatBytes(received)}/{FormatBytes(length.Value)}）· {uri.Host}"));
                        }
                    }
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Close();
                if (!await HasExpectedHashAsync(temporaryPath, expectedSha256, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException(displayName + " SHA-256 校验失败，文件已丢弃。");
                File.Move(temporaryPath, targetPath, overwrite: true);
                progress?.Invoke(new MinecraftAiRepairProgress(displayName + "校验完成", progressEnd, uri.Host));
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastFailure = exception;
                progress?.Invoke(new MinecraftAiRepairProgress(
                    displayName + "下载源不可用，正在切换",
                    progressStart,
                    uri.Host));
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        throw new HttpRequestException(displayName + "的所有下载源均不可用。", lastFailure);
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
            return await RunInferenceAsync(runtime, modelPath, prompt, progress, cancellationToken)
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
            return await RunInferenceAsync(cpuRuntime, modelPath, prompt, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> RunInferenceAsync(
        ResolvedRuntime runtime,
        string modelPath,
        string prompt,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateInferenceStartInfo(
            runtime.ExecutablePath,
            modelPath,
            prompt,
            runtime.UseGpu);

        using Process process = Process.Start(startInfo)
                                 ?? throw new InvalidOperationException("无法启动本地 0.5B 模型运行时。");
        PortableLog.Info(
            "MinecraftRepairAI",
            $"本地模型推理进程已启动；PID={process.Id}；Device={runtime.DeviceDescription}；GPU={runtime.UseGpu}。");
        StringBuilder outputBuilder = new();
        StringBuilder errorBuilder = new();
        int receivedCharacters = 0;
        Task stdout = ReadProcessLinesAsync(
            process.StandardOutput,
            outputBuilder,
            line =>
            {
                Interlocked.Add(ref receivedCharacters, line.Length);
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
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException("本地模型推理失败：" + SanitizeAnalysis(error));
        progress?.Invoke(new MinecraftAiRepairProgress("模型已完成推理", 0.93d, runtime.DeviceDescription));
        return output;
    }

    private async Task<string> RunOpenAiCompatibleInferenceAsync(
        MinecraftAiModelOptions options,
        string prompt,
        Action<MinecraftAiRepairProgress>? progress,
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
        JsonObject payload = new()
        {
            ["model"] = model,
            ["stream"] = true,
            ["temperature"] = 0.1,
            ["max_tokens"] = 900,
            ["messages"] = messages
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
                string content = ExtractChatCompletionContent(json);
                Interlocked.Add(ref answerCharacters, content.Length);
                return content;
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using StreamReader reader = new(responseStream, Encoding.UTF8);
            StringBuilder answer = new();
            StringBuilder pendingLine = new();
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

    private static string ExtractChatCompletionContent(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out JsonElement message) ||
            ReadJsonText(message, "content") is not { } content)
        {
            throw new InvalidDataException("OpenAI 兼容 API 响应缺少 choices[0].message.content。");
        }
        return content;
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
        string prompt,
        bool useGpu)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executablePath), "llama", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add("cli");
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("640");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("4096");
        startInfo.ArgumentList.Add("-ngl");
        startInfo.ArgumentList.Add(useGpu ? "all" : "0");
        startInfo.ArgumentList.Add("--temp");
        startInfo.ArgumentList.Add("0.1");
        startInfo.ArgumentList.Add("--top-k");
        startInfo.ArgumentList.Add("20");
        startInfo.ArgumentList.Add("--no-display-prompt");
        // Newer llama.cpp releases automatically enter interactive conversation mode for models
        // with a chat template. Explicitly disable it so a subprocess run exits after one answer.
        startInfo.ArgumentList.Add("--no-conversation");
        startInfo.ArgumentList.Add("--simple-io");
        return startInfo;
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
                !root.TryGetProperty("stage", out JsonElement stage) ||
                stage.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("progress", out JsonElement progressElement) ||
                !progressElement.TryGetDouble(out double progressValue))
            {
                return false;
            }
            progress = new MinecraftAiRepairProgress(
                SanitizeStage(stage.GetString()),
                Math.Clamp(progressValue, 0d, 1d));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildPrompt(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<string> crashLines,
        IReadOnlyList<MinecraftModMetadata> installedMods,
        MinecraftAiRepairContext repairContext,
        string languageCode)
    {
        string actions = string.Join(',', fault.AllowedActions.Select(action => action.ToString()));
        StringBuilder evidence = new();
        foreach (string line in fault.Evidence.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(24))
        {
            if (evidence.Length >= 2_000)
                break;
            string safeLine = PortableLog.Redact(line);
            evidence.Append("[structured] ")
                .AppendLine(safeLine.Length > 512 ? safeLine[..512] : safeLine);
        }
        foreach (string line in (fault.StackTrace ?? string.Empty).Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(16))
        {
            if (evidence.Length >= 3_500)
                break;
            string safeLine = PortableLog.Redact(line);
            evidence.Append("[stack] ")
                .AppendLine(safeLine.Length > 512 ? safeLine[..512] : safeLine);
        }
        foreach (string line in crashLines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(80))
        {
            if (evidence.Length >= 6_000)
                break;
            string safeLine = PortableLog.Redact(line);
            evidence.AppendLine(safeLine.Length > 512 ? safeLine[..512] : safeLine);
        }
        string outputLanguage = languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "English"
            : languageCode.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
              languageCode.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                ? "繁體中文"
                : "简体中文";
        StringBuilder modMetadata = new();
        foreach (MinecraftModMetadata mod in installedMods.Take(60))
        {
            modMetadata.Append("- id=").Append(mod.Id)
                .Append("; name=").Append(mod.Name)
                .Append("; version=").Append(mod.Version)
                .Append("; loader=").Append(mod.Loader)
                .Append("; depends=").AppendJoin(',', mod.Dependencies.Take(12))
                .AppendLine();
        }
        string lastClass = PortableLog.Redact(fault.LastClassName);
        if (string.IsNullOrWhiteSpace(lastClass))
            lastClass = "未知";
        return "你是 PCL N 的 Minecraft 崩溃分析器。常规分析器已经给出结构化证据；请生成清楚、克制的分析。" +
               "你可以给出 1 到 4 个有先后依赖的修复步骤；每一步只能从允许动作中选择，不得生成命令或文件路径。" +
               "不要输出隐藏思维过程，只输出可审计的步骤依据。模组操作只能使用下方 metadata 中存在的 modId，DownloadMod 除外。\n" +
               $"analysisMarkdown 必须使用 {outputLanguage}。\n" +
               "先分别输出两行进度 JSON，每行必须立即换行：" +
               "{\"type\":\"progress\",\"stage\":\"正在归纳异常\",\"progress\":0.25} 和 " +
               "{\"type\":\"progress\",\"stage\":\"正在匹配安全修复动作\",\"progress\":0.7}。\n" +
               "如果摘要不足以判断，在进度行之后可以改为只输出一行上下文请求 JSON：" +
               "{\"type\":\"context_request\",\"scopes\":[\"environment\",\"instance\",\"crash_reports\",\"runtime_logs\",\"launch_method\",\"login_method\"]," +
               "\"stage\":\"正在读取必要信息\",\"progress\":0.8}。scopes 只选择确实需要的类别；宿主只会返回脱敏的只读信息，且只允许请求一次。\n" +
               "信息足够时，最后严格输出一行结果 JSON：{\"type\":\"result\",\"analysisMarkdown\":\"含原因、证据、处理建议的 Markdown，不超过300字\",\"confidence\":0到1," +
               "\"steps\":[{\"action\":\"允许动作之一\",\"stage\":\"当前步骤，最多32字\",\"progress\":0到1," +
               "\"rationale\":\"该步骤的简短可审计依据\",\"modId\":null,\"modVersion\":null,\"javaMajorVersion\":null," +
               "\"loaderKind\":null,\"loaderVersion\":null}]}\n" +
               "DisableMod 必须给出已安装 modId；UpdateMod 必须给出已安装 modId 和目标 modVersion；DownloadMod 必须给出项目 modId。" +
               "Java 与加载器字段仅是建议，宿主会重新验证兼容性和可用版本。\n" +
               $"当前环境：Minecraft={repairContext.MinecraftVersion}；Loader={repairContext.Loader}；" +
               $"Java={repairContext.JavaMajorVersion?.ToString(CultureInfo.InvariantCulture) ?? "未知"}；" +
               $"内存MiB={repairContext.MemoryMegabytes?.ToString(CultureInfo.InvariantCulture) ?? "未知"}；" +
               $"系统={repairContext.OperatingSystem}/{repairContext.Architecture}；" +
               $"模组数={repairContext.InstalledModCount}；错误日志行数={repairContext.CrashLogLineCount}\n" +
               $"允许动作：{actions}\n错误代码：{fault.Code}\n子系统：{fault.Subsystem}\n节点：{fault.Stage}\n" +
               $"最后类：{lastClass}\n" +
               $"异常：{PortableLog.Redact(fault.ExceptionType)}: {PortableLog.Redact(fault.Message)}\n" +
               $"已安装模组 metadata：\n{modMetadata}日志：\n{evidence}";
    }

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

    private sealed record ResolvedRuntime(string ExecutablePath, bool UseGpu, string DeviceDescription);

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
        if (!root.TryGetProperty("javaMajorVersion", out JsonElement element) || !element.TryGetInt32(out int value))
            return null;
        return value is >= 8 and <= 99 ? value : null;
    }

    private static string? ReadLoaderKind(JsonElement root)
    {
        string? value = ReadSafeToken(root, "loaderKind", 32);
        return value?.ToLowerInvariant() is "fabric" or "legacyfabric" or "quilt" or "forge" or "neoforge" or
            "cleanroom" ? value : null;
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
