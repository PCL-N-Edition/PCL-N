// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using PCL.Core.Logging;

namespace PCL.Application.Updates;

/// <summary>Downloads, verifies and stages a launcher binary, then replaces it after exit.</summary>
public sealed class LauncherUpdateInstaller : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly ILauncherGpgVerifier _gpgVerifier;
    private bool _disposed;

    public LauncherUpdateInstaller(HttpClient? httpClient = null)
        : this(httpClient, LauncherGpgVerifier.Instance)
    {
    }

    internal LauncherUpdateInstaller(HttpClient? httpClient, ILauncherGpgVerifier gpgVerifier)
    {
        ArgumentNullException.ThrowIfNull(gpgVerifier);
        _gpgVerifier = gpgVerifier;
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            {
                Timeout = TimeSpan.FromMinutes(20)
            };
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCL-N-Updater", "1.0"));
    }

    public event EventHandler<LauncherUpdateProgress>? ProgressChanged;

    public async Task<PreparedLauncherUpdate> PrepareAsync(
        LauncherUpdatePackage package,
        string currentExecutablePath,
        string? hpatchzPath = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentExecutablePath);
        string currentPath = Path.GetFullPath(currentExecutablePath);
        if (!File.Exists(currentPath))
            throw new FileNotFoundException("找不到当前启动器文件，无法执行自动更新。", currentPath);

        string workDirectory = Path.Combine(
            Path.GetTempPath(),
            "PCL-N",
            "updates",
            SanitizeFileName(package.TargetVersion) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        string? preparedBinary = null;
        bool usedPatch = false;

        if (package.PatchSteps.Count > 0 && !string.IsNullOrWhiteSpace(hpatchzPath) && File.Exists(hpatchzPath))
        {
            try
            {
                preparedBinary = await ApplyPatchChainAsync(
                        package,
                        currentPath,
                        hpatchzPath,
                        workDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                await VerifyDetachedSignatureAsync(
                        preparedBinary,
                        package.TargetBinarySignatureUrl,
                        required: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                usedPatch = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                preparedBinary = null;
                PortableLog.Warn(ex, "Update", "补丁应用或校验失败，将自动回退完整包。");
                Report(LauncherUpdateStage.FallingBack, 0, "补丁不可用，正在改用完整包…");
            }
        }
        else if (package.PatchSteps.Count > 0)
        {
            PortableLog.Warn("Update", "当前构建未内置 hpatchz，将自动回退完整包。");
            Report(LauncherUpdateStage.FallingBack, 0, "补丁工具不可用，正在改用完整包…");
        }

        if (preparedBinary is null)
        {
            preparedBinary = await DownloadAndExtractFullPackageAsync(package, workDirectory, cancellationToken)
                .ConfigureAwait(false);
            await VerifyDetachedSignatureAsync(
                    preparedBinary,
                    package.TargetBinarySignatureUrl,
                    required: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await VerifyTargetAsync(preparedBinary, package.TargetSha256, cancellationToken).ConfigureAwait(false);
        string verifiedBinarySha256 = await CalculateSha256Async(preparedBinary, cancellationToken).ConfigureAwait(false);
        string stagedPath = BuildStagedPath(currentPath, package.TargetVersion);
        File.Copy(preparedBinary, stagedPath, overwrite: true);
        PreserveExecutableMode(currentPath, stagedPath);
        await VerifyFileAsync(
                stagedPath,
                verifiedBinarySha256,
                "暂存后的启动器与 GPG 已校验内容不一致",
                cancellationToken)
            .ConfigureAwait(false);
        Report(LauncherUpdateStage.Ready, 1, "更新已下载并通过校验。");
        PortableLog.Info(
            "Update",
            $"启动器更新已就绪；目标={package.TargetVersion}；方式={(usedPatch ? "Patch" : "Full")}；暂存={stagedPath}。");
        return new PreparedLauncherUpdate(package, currentPath, stagedPath, workDirectory, usedPatch);
    }

    public void ScheduleInstallAndRestart(PreparedLauncherUpdate update, int processId)
    {
        ScheduleInstall(update, processId, restartAfterInstall: true);
    }

    public void ScheduleInstallOnExit(PreparedLauncherUpdate update, int processId)
    {
        ScheduleInstall(update, processId, restartAfterInstall: false);
    }

    private void ScheduleInstall(PreparedLauncherUpdate update, int processId, bool restartAfterInstall)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);
        if (!File.Exists(update.StagedExecutablePath))
            throw new FileNotFoundException("已下载的启动器更新不存在。", update.StagedExecutablePath);

        Directory.CreateDirectory(update.WorkDirectory);
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? CreateWindowsReplacementProcess(update, processId, restartAfterInstall)
            : CreateUnixReplacementProcess(update, processId, restartAfterInstall);
        Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("无法启动更新替换进程。");
        process.Dispose();
        PortableLog.Info(
            "Update",
            $"更新替换进程已启动；等待 PID={processId} 退出后覆盖；重新启动={restartAfterInstall}。");
    }

    private async Task<string> ApplyPatchChainAsync(
        LauncherUpdatePackage package,
        string currentPath,
        string hpatchzPath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        string input = currentPath;
        for (int i = 0; i < package.PatchSteps.Count; i++)
        {
            LauncherUpdatePatchStep step = package.PatchSteps[i];
            Report(
                LauncherUpdateStage.Verifying,
                (double)i / package.PatchSteps.Count,
                $"正在校验补丁源文件（{i + 1}/{package.PatchSteps.Count}）…");
            await VerifyFileAsync(input, step.FromSha256, "当前启动器与补丁源版本不一致", cancellationToken)
                .ConfigureAwait(false);

            string patchPath = Path.Combine(workDirectory, $"patch-{i + 1}.hdiff");
            await DownloadFileAsync(
                    step.DownloadUrl,
                    patchPath,
                    LauncherUpdateStage.DownloadingPatch,
                    i,
                    package.PatchSteps.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyFileAsync(patchPath, step.Sha256, "补丁文件校验失败", cancellationToken)
                .ConfigureAwait(false);

            string output = Path.Combine(workDirectory, $"patched-{i + 1}-{package.TargetBinaryName}");
            Report(
                LauncherUpdateStage.ApplyingPatch,
                (double)i / package.PatchSteps.Count,
                $"正在应用补丁（{i + 1}/{package.PatchSteps.Count}）…");
            await RunPatchToolAsync(hpatchzPath, input, patchPath, output, cancellationToken).ConfigureAwait(false);
            await VerifyFileAsync(output, step.TargetSha256, "补丁生成的启动器校验失败", cancellationToken)
                .ConfigureAwait(false);
            input = output;
        }

        return input;
    }

    private async Task<string> DownloadAndExtractFullPackageAsync(
        LauncherUpdatePackage package,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        string archivePath = Path.Combine(workDirectory, package.TargetAssetName);
        await DownloadFileAsync(
                package.FullPackageUrl,
                archivePath,
                LauncherUpdateStage.DownloadingFullPackage,
                0,
                1,
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyDetachedSignatureAsync(
                archivePath,
                package.FullPackageSignatureUrl,
                required: true,
                cancellationToken)
            .ConfigureAwait(false);
        Report(LauncherUpdateStage.Extracting, 0, "正在解压完整更新包…");
        string output = Path.Combine(workDirectory, "full-" + package.TargetBinaryName);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            await ExtractZipBinaryAsync(archivePath, package.TargetBinaryName, output, cancellationToken).ConfigureAwait(false);
        else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                 archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            await ExtractTarBinaryAsync(archivePath, package.TargetBinaryName, output, cancellationToken).ConfigureAwait(false);
        else
            throw new InvalidDataException($"不支持的启动器更新包格式：{package.TargetAssetName}");
        return output;
    }

    private async Task<bool> VerifyDetachedSignatureAsync(
        string contentPath,
        string? signatureUrl,
        bool required,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(signatureUrl))
        {
            if (required)
                throw new InvalidDataException("更新没有提供 GPG 签名，已拒绝自动替换。");
            return false;
        }

        using HttpResponseMessage response = await _httpClient.GetAsync(
                signatureUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && !required)
        {
            PortableLog.Debug("Update", $"目标程序没有独立 GPG 签名，沿用已校验的完整包签名：{signatureUrl}");
            return false;
        }
        response.EnsureSuccessStatusCode();
        Report(LauncherUpdateStage.VerifyingSignature, 0, "正在验证发布者 GPG 签名…");
        await using Stream content = File.OpenRead(contentPath);
        await using Stream signature = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await _gpgVerifier.VerifyAsync(content, signature, cancellationToken).ConfigureAwait(false);
        PortableLog.Info("Update", $"GPG 签名校验通过：{Path.GetFileName(contentPath)}。");
        return true;
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        LauncherUpdateStage stage,
        int itemIndex,
        int itemCount,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
        byte[] buffer = new byte[1024 * 128];
        long received = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            double itemProgress = total is > 0 ? Math.Clamp((double)received / total.Value, 0, 1) : 0;
            double progress = (itemIndex + itemProgress) / Math.Max(1, itemCount);
            Report(stage, progress, $"正在下载更新（{FormatBytes(received)} / {FormatBytes(total)}）…");
        }
    }

    private static async Task RunPatchToolAsync(
        string hpatchzPath,
        string input,
        string patch,
        string output,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(hpatchzPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(input);
        startInfo.ArgumentList.Add(patch);
        startInfo.ArgumentList.Add(output);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 hpatchz。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string outputText = await stdout.ConfigureAwait(false);
        string errorText = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            throw new InvalidOperationException(
                $"hpatchz 返回 {process.ExitCode}：{(string.IsNullOrWhiteSpace(errorText) ? outputText : errorText).Trim()}");
        }
    }

    private static async Task ExtractZipBinaryAsync(
        string archivePath,
        string binaryName,
        string output,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(item =>
            string.Equals(Path.GetFileName(item.FullName), binaryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidDataException($"更新包中没有 {binaryName}。");
        await using Stream source = entry.Open();
        await using FileStream target = new(output, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractTarBinaryAsync(
        string archivePath,
        string binaryName,
        string output,
        CancellationToken cancellationToken)
    {
        await using FileStream archive = File.OpenRead(archivePath);
        await using GZipStream gzip = new(archive, CompressionMode.Decompress);
        using TarReader reader = new(gzip, leaveOpen: false);
        while (reader.GetNextEntry() is { } entry)
        {
            if (!string.Equals(Path.GetFileName(entry.Name), binaryName, StringComparison.OrdinalIgnoreCase) ||
                entry.DataStream is null)
            {
                continue;
            }

            await using FileStream target = new(output, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
            await entry.DataStream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidDataException($"更新包中没有 {binaryName}。");
    }

    private static async Task VerifyTargetAsync(
        string path,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(expectedSha256))
            await VerifyFileAsync(path, expectedSha256, "完整更新包校验失败", cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyFileAsync(
        string path,
        string expectedSha256,
        string message,
        CancellationToken cancellationToken)
    {
        string actual = await CalculateSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{message}（预期 {expectedSha256}，实际 {actual}）。");
    }

    private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static ProcessStartInfo CreateWindowsReplacementProcess(
        PreparedLauncherUpdate update,
        int processId,
        bool restartAfterInstall)
    {
        string script = Path.Combine(update.WorkDirectory, "install-update.ps1");
        File.WriteAllText(script, WindowsReplacementScript);
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(update.CurrentExecutablePath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(update.CurrentExecutablePath);
        startInfo.ArgumentList.Add(update.StagedExecutablePath);
        startInfo.ArgumentList.Add(restartAfterInstall ? "1" : "0");
        return startInfo;
    }

    private static ProcessStartInfo CreateUnixReplacementProcess(
        PreparedLauncherUpdate update,
        int processId,
        bool restartAfterInstall)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix 更新替换器不能在 Windows 上运行。");
        string script = Path.Combine(update.WorkDirectory, "install-update.sh");
        File.WriteAllText(script, UnixReplacementScript);
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        ProcessStartInfo startInfo = new("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(update.CurrentExecutablePath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(update.CurrentExecutablePath);
        startInfo.ArgumentList.Add(update.StagedExecutablePath);
        startInfo.ArgumentList.Add(restartAfterInstall ? "1" : "0");
        return startInfo;
    }

    private static void PreserveExecutableMode(string currentPath, string stagedPath)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(stagedPath, File.GetUnixFileMode(currentPath));
        }
        catch (IOException)
        {
            File.SetUnixFileMode(
                stagedPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static string BuildStagedPath(string currentPath, string version)
    {
        string directory = Path.GetDirectoryName(currentPath) ?? Environment.CurrentDirectory;
        string name = Path.GetFileName(currentPath);
        return Path.Combine(directory, $".{name}.{SanitizeFileName(version)}.update");
    }

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
            return "未知";
        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private void Report(LauncherUpdateStage stage, double progress, string message) =>
        ProgressChanged?.Invoke(this, new LauncherUpdateProgress(stage, Math.Clamp(progress, 0, 1), message));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private const string WindowsReplacementScript = """
        param([int]$ProcessIdToWait, [string]$Current, [string]$Staged, [int]$RestartAfterInstall)
        $ErrorActionPreference = 'Stop'
        Wait-Process -Id $ProcessIdToWait -ErrorAction SilentlyContinue
        $Backup = "$Current.pcln-old"
        for ($Attempt = 0; $Attempt -lt 40; $Attempt++) {
            try {
                if (Test-Path -LiteralPath $Backup) { Remove-Item -LiteralPath $Backup -Force }
                Move-Item -LiteralPath $Current -Destination $Backup -Force
                Move-Item -LiteralPath $Staged -Destination $Current -Force
                if ($RestartAfterInstall -eq 1) {
                    Start-Process -FilePath $Current -WorkingDirectory (Split-Path -Parent $Current)
                    Start-Sleep -Seconds 2
                }
                Remove-Item -LiteralPath $Backup -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $PSScriptRoot -Recurse -Force -ErrorAction SilentlyContinue
                exit 0
            } catch {
                if (-not (Test-Path -LiteralPath $Current) -and (Test-Path -LiteralPath $Backup)) {
                    Move-Item -LiteralPath $Backup -Destination $Current -Force -ErrorAction SilentlyContinue
                }
                Start-Sleep -Milliseconds 500
            }
        }
        exit 1
        """;

    private const string UnixReplacementScript = """
        pid="$1"
        current="$2"
        staged="$3"
        restart_after_install="$4"
        while kill -0 "$pid" 2>/dev/null; do sleep 0.2; done
        backup="${current}.pcln-old"
        rm -f "$backup"
        if mv "$current" "$backup" && mv "$staged" "$current"; then
          chmod +x "$current"
          if [ "$restart_after_install" = "1" ]; then
            (cd "$(dirname "$current")" && "$current" >/dev/null 2>&1 &)
            sleep 2
          fi
          rm -f "$backup"
          rm -rf "$(dirname "$0")"
          exit 0
        fi
        if [ ! -f "$current" ] && [ -f "$backup" ]; then mv "$backup" "$current"; fi
        exit 1
        """;
}

public sealed record PreparedLauncherUpdate(
    LauncherUpdatePackage Package,
    string CurrentExecutablePath,
    string StagedExecutablePath,
    string WorkDirectory,
    bool UsedPatch);

public sealed record LauncherUpdateProgress(
    LauncherUpdateStage Stage,
    double Progress,
    string Message);

public enum LauncherUpdateStage
{
    DownloadingPatch,
    ApplyingPatch,
    FallingBack,
    DownloadingFullPackage,
    Extracting,
    Verifying,
    VerifyingSignature,
    Ready
}
