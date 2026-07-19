// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PCL.Application.Settings;
using PCL.Application.Updates;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Owns the process-wide launcher update check and download. Navigation pages are
/// observers of this session and must not create or cancel automatic downloads.
/// </summary>
internal sealed class LauncherUpdateCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _updateFlowGate = new(1, 1);
    private readonly LauncherUpdateService _service = new();
    private readonly LauncherUpdateInstaller _installer = new();
    private readonly TaskCompletionSource<LauncherUpdateCheckResult?> _automaticCheckResult =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<LauncherUpdateCheckResult?>? _automaticTask;
    private PreparedLauncherUpdate? _preparedUpdate;
    private PreparedLauncherUpdate? _installOnExit;
    private LauncherUpdateProgress? _latestProgress;
    private bool _updateOperationActive;
    private int _installScheduled;
    private bool _disposed;

    private LauncherUpdateCoordinator()
    {
        _installer.ProgressChanged += OnInstallerProgressChanged;
    }

    public static LauncherUpdateCoordinator Current { get; } = new();

    public event EventHandler<LauncherUpdateProgress>? ProgressChanged;

    public event Action<PreparedLauncherUpdate?>? PreparedUpdateChanged;

    public event Action<bool>? UpdateOperationActiveChanged;

    public PreparedLauncherUpdate? PreparedUpdate
    {
        get
        {
            lock (_sync)
                return _preparedUpdate;
        }
    }

    public bool IsUpdateOperationActive
    {
        get
        {
            lock (_sync)
                return _updateOperationActive;
        }
    }

    public bool IsUpdateTransferActive
    {
        get
        {
            lock (_sync)
            {
                return _updateOperationActive &&
                       _latestProgress is { Stage: not LauncherUpdateStage.Ready };
            }
        }
    }

    public Task<LauncherUpdateCheckResult?> StartAutomaticUpdateOnceAsync()
    {
        lock (_sync)
            return _automaticTask ??= RunAutomaticUpdateAsync();
    }

    public Task<LauncherUpdateCheckResult?> WaitForAutomaticCheckResultAsync()
    {
        _ = StartAutomaticUpdateOnceAsync();
        return _automaticCheckResult.Task;
    }

    public async Task<LauncherUpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _service.CheckAsync(
                    channel,
                    PclLauncherBuildIdentity.Current,
                    CurrentCommit,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<PreparedLauncherUpdate> PrepareAsync(
        LauncherUpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_preparedUpdate is { } existing &&
                    string.Equals(existing.Package.TargetTag, package.TargetTag, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(existing.StagedExecutablePath))
                {
                    return existing;
                }
            }

            string currentExecutable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定当前启动器文件位置。");
            string? hpatchz = await PclEmbeddedUpdateTool.GetHpatchzPathAsync(cancellationToken).ConfigureAwait(false);
            PreparedLauncherUpdate prepared = await _installer.PrepareAsync(
                    package,
                    currentExecutable,
                    hpatchz,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
                _preparedUpdate = prepared;
            PreparedUpdateChanged?.Invoke(prepared);
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public bool InstallAndRestart(PreparedLauncherUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (Interlocked.CompareExchange(ref _installScheduled, 1, 0) != 0)
        {
            PortableLog.Warn("Update", "更新安装已经安排，忽略重复的立即安装请求。");
            return false;
        }

        try
        {
            lock (_sync)
                _installOnExit = null;
            _installer.ScheduleInstallAndRestart(update, Environment.ProcessId);
            Dispatcher.UIThread.Post(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                else
                    Environment.Exit(0);
            });
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _installScheduled, 0);
            throw;
        }
    }

    public async Task HandleAvailableUpdateAsync(
        LauncherUpdateCheckResult result,
        int mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsUpdateAvailable || result.Package is not { } package)
            return;

        await _updateFlowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetUpdateOperationActive(true);
            switch (mode)
            {
                case 0:
                    ClearSkippedVersion(result);
                    InstallAndRestart(await PrepareAsync(package, cancellationToken).ConfigureAwait(false));
                    return;
                case 1:
                {
                    ClearSkippedVersion(result);
                    PreparedLauncherUpdate prepared = await PrepareAsync(package, cancellationToken).ConfigureAwait(false);
                    await PromptDownloadedUpdateAsync(result, prepared, cancellationToken).ConfigureAwait(false);
                    return;
                }
                case 2:
                {
                    int choice = await PromptAvailableUpdateAsync(result, cancellationToken).ConfigureAwait(false);
                    PortableLog.Info("Update", $"更新提示选择={choice}；目标={UpdateIdentity(result)}。");
                    if (choice == 3)
                    {
                        SkipVersion(result);
                        return;
                    }
                    if (choice is not (1 or 2))
                        return;

                    ClearSkippedVersion(result);
                    PreparedLauncherUpdate prepared = await PrepareAsync(package, cancellationToken).ConfigureAwait(false);
                    if (choice == 2)
                        InstallAndRestart(prepared);
                    else
                        await PromptDownloadedUpdateAsync(result, prepared, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }
        finally
        {
            SetUpdateOperationActive(false);
            _updateFlowGate.Release();
        }
    }

    public void SkipAvailableVersion(LauncherUpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        SkipVersion(result);
    }

    public void Dispose()
    {
        PreparedLauncherUpdate? installOnExit;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            installOnExit = _installOnExit;
            _installOnExit = null;
        }

        if (installOnExit is not null && File.Exists(installOnExit.StagedExecutablePath))
        {
            try
            {
                _installer.ScheduleInstallOnExit(installOnExit, Environment.ProcessId);
                PortableLog.Info("Update", $"启动器退出后将静默安装 {installOnExit.Package.TargetVersion}。");
            }
            catch (Exception ex)
            {
                PortableLog.Error(ex, "Update", "无法安排退出后安装启动器更新。");
            }
        }

        _installer.Dispose();
        _service.Dispose();
        _operationGate.Dispose();
        _updateFlowGate.Dispose();
    }

    private async Task<LauncherUpdateCheckResult?> RunAutomaticUpdateAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_DISABLE_FIRST_RUN")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_DISABLE_DEBUG_HINT")))
            {
                PortableLog.Debug("Update", "自动化环境已跳过启动时更新检查。");
                return PublishAutomaticCheckResult(null);
            }

            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            LauncherUpdatePolicy policy = LauncherUpdatePolicy.Resolve(
                settings,
                PclLauncherBuildIdentity.Current.Configuration);
            if (policy.Mode == 3)
            {
                PortableLog.Info("Update", "启动时自动更新检查已关闭。");
                return PublishAutomaticCheckResult(null);
            }

            PortableLog.Info(
                "Update",
                $"启动时自动检查更新；通道={policy.Channel}；模式={policy.Mode}；设置文件={LauncherSettingsPageBinder.CreateSettingsPath()}。");
            LauncherUpdateCheckResult result = await CheckAsync(policy.Channel).ConfigureAwait(false);
            PublishAutomaticCheckResult(result);
            if (!result.Success || !result.IsUpdateAvailable || result.Package is null)
                return result;

            if (IsSkippedVersion(result, settings))
            {
                PortableLog.Info("Update", $"已跳过启动器版本：{UpdateIdentity(result)}。");
                return result;
            }

            await HandleAvailableUpdateAsync(result, policy.Mode).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            PortableLog.Warn(ex, "Update", "启动时自动更新失败；可在设置页中重试。");
            LauncherUpdateCheckResult failed = LauncherUpdateCheckResult.Failed(ex.Message);
            PublishAutomaticCheckResult(failed);
            return failed;
        }
    }

    private LauncherUpdateCheckResult? PublishAutomaticCheckResult(LauncherUpdateCheckResult? result)
    {
        _automaticCheckResult.TrySetResult(result);
        return result;
    }

    private void SetUpdateOperationActive(bool active)
    {
        lock (_sync)
        {
            _updateOperationActive = active;
            if (active)
                _latestProgress = null;
        }
        UpdateOperationActiveChanged?.Invoke(active);
    }

    private void OnInstallerProgressChanged(object? sender, LauncherUpdateProgress progress)
    {
        lock (_sync)
            _latestProgress = progress;
        ProgressChanged?.Invoke(this, progress);
    }

    private static string CurrentCommit => !string.IsNullOrWhiteSpace(PclBuildInfo.SourceRevisionId)
        ? PclBuildInfo.SourceRevisionId
        : PclMetadata.Current.Commit;

    private static Task<int> PromptAvailableUpdateAsync(
        LauncherUpdateCheckResult result,
        CancellationToken cancellationToken) =>
        DesktopHostNotifications.Instance.ChoiceAsync(
            Text("Setup.Update.Prompt.Available.Title", "发现可用更新"),
            BuildChangelog(result),
            Text("Setup.Update.Prompt.Available.DownloadOnly", "仅下载"),
            Text("Setup.Update.Prompt.Available.DownloadAndInstall", "下载并安装"),
            Text("Setup.Update.Prompt.SkipVersion", "跳过版本"),
            isWarn: false,
            cancellationToken);

    private async Task PromptDownloadedUpdateAsync(
        LauncherUpdateCheckResult result,
        PreparedLauncherUpdate prepared,
        CancellationToken cancellationToken)
    {
        int choice = await DesktopHostNotifications.Instance.ChoiceAsync(
                Text("Setup.Update.Prompt.Downloaded.Title", "更新已下载"),
                BuildChangelog(result),
                Text("Setup.Update.Prompt.Downloaded.InstallNow", "安装并重启"),
                Text("Setup.Update.Prompt.Downloaded.Later", "稍后"),
                Text("Setup.Update.Prompt.SkipVersion", "跳过版本"),
                isWarn: false,
                cancellationToken)
            .ConfigureAwait(false);
        PortableLog.Info("Update", $"下载完成提示选择={choice}；目标={UpdateIdentity(result)}。");
        switch (choice)
        {
            case 1:
                InstallAndRestart(prepared);
                break;
            case 2:
                lock (_sync)
                    _installOnExit = prepared;
                PortableLog.Info("Update", "用户选择稍后安装；将在启动器关闭后静默替换且不重新启动。");
                break;
            case 3:
                SkipVersion(result);
                break;
        }
    }

    private static string BuildChangelog(LauncherUpdateCheckResult result)
    {
        string version = result.LatestVersion ?? result.Package?.TargetVersion ?? string.Empty;
        string title = !string.IsNullOrWhiteSpace(result.ReleaseName)
            ? result.ReleaseName.Trim()
            : "PCL N " + version;
        string notes = !string.IsNullOrWhiteSpace(result.ReleaseNotes)
            ? result.ReleaseNotes.Trim()
            : Text("Setup.Update.Prompt.NoChangelog", "此版本没有提供更新日志。");
        return $"# {title}\n\n{notes}";
    }

    private static bool IsSkippedVersion(LauncherUpdateCheckResult result, LauncherSettings settings) =>
        string.Equals(
            settings.GetTextOption(LauncherSettingKeys.SystemUpdateSkippedTarget),
            UpdateIdentity(result),
            StringComparison.OrdinalIgnoreCase);

    private void SkipVersion(LauncherUpdateCheckResult result)
    {
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        settings.SetTextOption(LauncherSettingKeys.SystemUpdateSkippedTarget, UpdateIdentity(result));
        LauncherSettingsPageBinder.SaveSettings(settings);

        PreparedLauncherUpdate? prepared;
        lock (_sync)
        {
            prepared = _preparedUpdate is { } candidate &&
                       string.Equals(
                           candidate.Package.TargetTag,
                           result.Package?.TargetTag,
                           StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }
        if (prepared is not null)
            DiscardPreparedUpdate(prepared);
    }

    private static void ClearSkippedVersion(LauncherUpdateCheckResult result)
    {
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        if (!IsSkippedVersion(result, settings))
            return;
        settings.RemoveTextOption(LauncherSettingKeys.SystemUpdateSkippedTarget);
        LauncherSettingsPageBinder.SaveSettings(settings);
    }

    private static string UpdateIdentity(LauncherUpdateCheckResult result) =>
        result.Channel is UpdateChannel.CI && !string.IsNullOrWhiteSpace(result.RemoteCommitSha)
            ? "ci:" + result.RemoteCommitSha.Trim()
            : result.Package?.TargetTag ?? result.LatestVersion ?? string.Empty;

    private void DiscardPreparedUpdate(PreparedLauncherUpdate prepared)
    {
        bool changed;
        lock (_sync)
        {
            changed = ReferenceEquals(_preparedUpdate, prepared);
            if (ReferenceEquals(_preparedUpdate, prepared))
                _preparedUpdate = null;
            if (ReferenceEquals(_installOnExit, prepared))
                _installOnExit = null;
        }
        if (changed)
            PreparedUpdateChanged?.Invoke(null);

        try
        {
            if (File.Exists(prepared.StagedExecutablePath))
                File.Delete(prepared.StagedExecutablePath);
            if (Directory.Exists(prepared.WorkDirectory))
                Directory.Delete(prepared.WorkDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            PortableLog.Warn(ex, "Update", "清理已跳过版本的暂存更新失败。");
        }
    }

    private static string Text(string key, string fallback) =>
        AvaloniaLocalizationManager.GetText(key, fallback);
}
