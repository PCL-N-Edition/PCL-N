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
    private readonly LauncherUpdateService _service = new();
    private readonly LauncherUpdateInstaller _installer = new();
    private Task<LauncherUpdateCheckResult?>? _automaticTask;
    private PreparedLauncherUpdate? _preparedUpdate;
    private PreparedLauncherUpdate? _installOnExit;
    private bool _disposed;

    private LauncherUpdateCoordinator()
    {
        _installer.ProgressChanged += (_, progress) => ProgressChanged?.Invoke(this, progress);
    }

    public static LauncherUpdateCoordinator Current { get; } = new();

    public event EventHandler<LauncherUpdateProgress>? ProgressChanged;

    public PreparedLauncherUpdate? PreparedUpdate
    {
        get
        {
            lock (_sync)
                return _preparedUpdate;
        }
    }

    public Task<LauncherUpdateCheckResult?> StartAutomaticUpdateOnceAsync()
    {
        lock (_sync)
            return _automaticTask ??= RunAutomaticUpdateAsync();
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
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void InstallAndRestart(PreparedLauncherUpdate update)
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
    }

    public async Task HandleAvailableUpdateAsync(
        LauncherUpdateCheckResult result,
        int mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsUpdateAvailable || result.Package is not { } package)
            return;

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
    }

    private async Task<LauncherUpdateCheckResult?> RunAutomaticUpdateAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_DISABLE_FIRST_RUN")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_DISABLE_DEBUG_HINT")))
            {
                PortableLog.Debug("Update", "自动化环境已跳过启动时更新检查。");
                return null;
            }

            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            int mode = settings.GetIntegerOption(
                "SystemUpdateMode",
                LauncherSettingDefaults.GetInteger("SystemUpdateMode", 1));
            if (mode == 3)
            {
                PortableLog.Info("Update", "启动时自动更新检查已关闭。");
                return null;
            }

            int channelIndex = settings.TryGetIntegerOption("SystemUpdateChannel", out int configuredChannel)
                ? configuredChannel
                : PclLauncherBuildIdentity.Current.Configuration switch
                {
                    "Beta" => 1,
                    "CI" => 2,
                    _ => 0
                };
            UpdateChannel channel = channelIndex switch
            {
                1 => UpdateChannel.Beta,
                2 => UpdateChannel.CI,
                _ => UpdateChannel.Release
            };
            PortableLog.Info("Update", $"启动时自动检查更新；通道={channel}；模式={mode}。");
            LauncherUpdateCheckResult result = await CheckAsync(channel).ConfigureAwait(false);
            if (!result.Success || !result.IsUpdateAvailable || result.Package is null)
                return result;

            if (IsSkippedVersion(result, settings))
            {
                PortableLog.Info("Update", $"已跳过启动器版本：{UpdateIdentity(result)}。");
                return result;
            }

            await HandleAvailableUpdateAsync(result, mode).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            PortableLog.Warn(ex, "Update", "启动时自动更新失败；可在设置页中重试。");
            return LauncherUpdateCheckResult.Failed(ex.Message);
        }
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
        lock (_sync)
        {
            if (ReferenceEquals(_preparedUpdate, prepared))
                _preparedUpdate = null;
            if (ReferenceEquals(_installOnExit, prepared))
                _installOnExit = null;
        }

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
