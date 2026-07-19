// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL.Application.Downloads;
using PCL.Application.Launching;
using PCL.Core.Logging;
using PCL.Platform.Paths;

namespace PCL.Desktop.Features.Launching;

internal sealed record MinecraftAiRepairSuggestion(
    MinecraftRepairActionKind Action,
    string AnalysisMarkdown,
    double Confidence,
    string Stage,
    double Progress,
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
    string? RuntimePath = null);

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
        string languageCode,
        MinecraftAiModelOptions options,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(crashLines);
        ArgumentNullException.ThrowIfNull(installedMods);
        if (fault.AllowedActions.Length == 0)
            return null;

        Directory.CreateDirectory(_rootDirectory);
        string executable = await ResolveRuntimeAsync(options, progress, cancellationToken).ConfigureAwait(false);
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

        progress?.Invoke(new MinecraftAiRepairProgress("读取模组 metadata", 0.7d, $"{installedMods.Count} 个模组"));
        string prompt = BuildPrompt(fault, crashLines, installedMods, languageCode);
        progress?.Invoke(new MinecraftAiRepairProgress("模型分析 Minecraft 异常", 0.74d));
        string output = await RunInferenceAsync(executable, modelPath, prompt, cancellationToken)
            .ConfigureAwait(false);
        progress?.Invoke(new MinecraftAiRepairProgress("验证模型修复计划", 0.94d));
        MinecraftAiRepairSuggestion? suggestion = ParseSuggestion(output, fault.AllowedActions);
        if (suggestion is null)
            PortableLog.Warn("MinecraftRepairAI", "本地模型没有返回有效的白名单修复方案。");
        return suggestion;
    }

    internal static MinecraftAiRepairSuggestion? ParseSuggestion(
        string output,
        IReadOnlyCollection<MinecraftRepairActionKind> allowedActions)
    {
        if (string.IsNullOrWhiteSpace(output) || allowedActions.Count == 0)
            return null;
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(output[start..(end + 1)]);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("action", out JsonElement actionElement) ||
                !Enum.TryParse(actionElement.GetString(), ignoreCase: true, out MinecraftRepairActionKind action) ||
                !allowedActions.Contains(action))
            {
                return null;
            }

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
            MinecraftAiRepairParameters parameters = new(
                ReadSafeToken(root, "modId", 128),
                ReadSafeToken(root, "modVersion", 128),
                ReadJavaMajor(root),
                ReadLoaderKind(root),
                ReadSafeToken(root, "loaderVersion", 128));
            if (action is (MinecraftRepairActionKind.DownloadMod or MinecraftRepairActionKind.DisableMod or
                MinecraftRepairActionKind.UpdateMod) && string.IsNullOrWhiteSpace(parameters.ModId))
            {
                return null;
            }
            if (action == MinecraftRepairActionKind.UpdateMod && string.IsNullOrWhiteSpace(parameters.ModVersion))
                return null;
            if (!root.TryGetProperty("stage", out JsonElement stageElement) ||
                stageElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("progress", out JsonElement progressElement) ||
                !progressElement.TryGetDouble(out double progressValue))
            {
                return null;
            }
            string stage = SanitizeStage(stageElement.GetString());
            double modelProgress = Math.Clamp(progressValue, 0d, 1d);
            return new MinecraftAiRepairSuggestion(action, analysis, confidence, stage, modelProgress, parameters);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> ResolveRuntimeAsync(
        MinecraftAiModelOptions options,
        Action<MinecraftAiRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.RuntimePath))
        {
            string customRuntime = options.RuntimePath.Trim();
            if (!File.Exists(customRuntime))
                throw new FileNotFoundException("自定义 llama.cpp 可执行文件不存在。", customRuntime);
            return Path.GetFullPath(customRuntime);
        }
        string? configured = Environment.GetEnvironmentVariable("PCL_LLAMA_CLI");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
            return Path.GetFullPath(configured.Trim());

        RuntimePackage package = ResolveRuntimePackage();
        string runtimeDirectory = Path.Combine(_rootDirectory, "runtime", package.RuntimeId);
        string localExecutable = Path.Combine(
            runtimeDirectory,
            OperatingSystem.IsWindows() ? "llama-cli.exe" : "llama-cli");
        if (File.Exists(localExecutable))
            return localExecutable;

        string? pathExecutable = FindExecutable("llama-cli") ?? FindExecutable("llama");
        if (pathExecutable is not null)
            return pathExecutable;

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

    private static async Task<string> RunInferenceAsync(
        string executable,
        string modelPath,
        string prompt,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "llama", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add("cli");
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("384");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("8192");
        startInfo.ArgumentList.Add("--temp");
        startInfo.ArgumentList.Add("0.1");
        startInfo.ArgumentList.Add("--top-k");
        startInfo.ArgumentList.Add("20");
        startInfo.ArgumentList.Add("--no-display-prompt");

        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("无法启动本地 0.5B 模型运行时。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("本地模型推理失败：" + SanitizeAnalysis(error));
        return output;
    }

    private static string BuildPrompt(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<string> crashLines,
        IReadOnlyList<MinecraftModMetadata> installedMods,
        string languageCode)
    {
        string actions = string.Join(',', fault.AllowedActions.Select(action => action.ToString()));
        StringBuilder evidence = new();
        foreach (string line in crashLines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(80))
        {
            if (evidence.Length >= 6_000)
                break;
            evidence.AppendLine(line.Length > 512 ? line[..512] : line);
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
        return "你是 PCL N 的离线 Minecraft 崩溃分析器。常规分析器已经给出结构化证据；请生成清楚、克制的分析。" +
               "只能从允许动作中选择一个；不得生成命令或文件路径。模组操作只能使用下方 metadata 中存在的 modId，DownloadMod 除外。\n" +
               $"analysisMarkdown 必须使用 {outputLanguage}。\n" +
               "严格输出一行 JSON：{\"action\":\"允许动作之一\",\"analysisMarkdown\":\"含原因、证据、处理建议的 Markdown，不超过300字\",\"confidence\":0到1," +
               "\"stage\":\"当前修复阶段，最多32字\",\"progress\":0到1," +
               "\"modId\":null,\"modVersion\":null,\"javaMajorVersion\":null,\"loaderKind\":null,\"loaderVersion\":null}\n" +
               "DisableMod 必须给出已安装 modId；UpdateMod 必须给出已安装 modId 和目标 modVersion；DownloadMod 必须给出项目 modId。" +
               "Java 与加载器字段仅是建议，宿主会重新验证兼容性和可用版本。\n" +
               $"允许动作：{actions}\n错误代码：{fault.Code}\n子系统：{fault.Subsystem}\n节点：{fault.Stage}\n" +
               $"最后类：{fault.LastClassName ?? "未知"}\n异常：{fault.ExceptionType}: {fault.Message}\n" +
               $"已安装模组 metadata：\n{modMetadata}日志：\n{evidence}";
    }

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
        return new RuntimePackage(
            runtimeId,
            archive,
            sha256,
            [
                new Uri($"https://sourceforge.net/projects/llama-cpp.mirror/files/b9637/{archive}/download"),
                new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/b9637/{archive}")
            ]);
    }

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
        IReadOnlyList<Uri> Urls);

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
        string normalized = value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length <= 1_500 ? normalized : normalized[..1_500];
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.0} GiB",
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.0} MiB",
        >= 1024L => $"{bytes / 1024d:0.0} KiB",
        _ => bytes + " B"
    };
}
