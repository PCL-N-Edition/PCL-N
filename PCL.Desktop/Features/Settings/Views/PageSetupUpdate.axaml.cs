// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCL.Application.Settings;
using PCL.Application.Updates;
using PCL.Core.App;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Hosting;
using PCL.Desktop.Localization;

#pragma warning disable CA1822, CS0067

namespace PCL.Desktop.Features.Settings.Views;

public partial class PageSetupUpdate : MyPageRight, IRefreshableSettingsPage, ISettingsPageInteractionSource,
    IDeferredSettingsPersistence
{
    private const string ReleasesUrl = "https://github.com/MuXue1230-owo/PCL-N/releases";
    private string _latestReleaseUrl = ReleasesUrl;
    private string? _preferredAssetUrl;
    private bool _isInitializing = true;
    private bool _isRevertingChannel;
    private bool _channelUserArmed;
    private bool _isChecking;
    private bool _updateAvailableUi;
    private bool _autoCheckScheduled;
    private int _lastUpdateChannel;
    private readonly LauncherUpdateCoordinator _updateCoordinator = LauncherUpdateCoordinator.Current;
    private readonly LauncherInstallationContext _installation = LauncherInstallationContext.Detect();
    private LauncherUpdateCheckResult? _availableUpdate;
    private PreparedLauncherUpdate? _preparedUpdate;
    private bool _isPreparingUpdate;

    public PageSetupUpdate()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = PanBack;
        LauncherSettingsPageBinder.Attach(this, OnSettingsApplied);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        RefreshPage();
    }

    public event EventHandler<SettingsPathRequestedEventArgs>? OpenPathRequested;

    public event EventHandler<SettingsUrlRequestedEventArgs>? OpenUrlRequested;

    public event EventHandler<SettingsMessageRequestedEventArgs>? MessageRequested;

    public event EventHandler<SettingsConfirmRequestedEventArgs>? ConfirmRequested;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _updateCoordinator.ProgressChanged += OnUpdateProgressChanged;
        _updateCoordinator.PreparedUpdateChanged += OnPreparedUpdateChanged;
        _updateCoordinator.UpdateOperationActiveChanged += OnUpdateOperationActiveChanged;
        _channelUserArmed = false;
        _autoCheckScheduled = false;
        // Defer until styles/DynamicResource + settings re-bind settle, then paint combos and auto-check.
        Dispatcher.UIThread.Post(OnPageReady, DispatcherPriority.Loaded);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _updateCoordinator.ProgressChanged -= OnUpdateProgressChanged;
        _updateCoordinator.PreparedUpdateChanged -= OnPreparedUpdateChanged;
        _updateCoordinator.UpdateOperationActiveChanged -= OnUpdateOperationActiveChanged;
        _channelUserArmed = false;
        _autoCheckScheduled = false;
    }

    private void OnPageReady()
    {
        if (!this.IsAttachedToVisualTree())
            return;

        EnsureComboLabels();
        SyncChannelBaselineFromUi();
        _isInitializing = false;
        RefreshPage();

        if (!_autoCheckScheduled)
        {
            _autoCheckScheduled = true;
            _ = MaybeAutoCheckAsync();
        }
    }

    private void OnSettingsApplied(LauncherSettings settings)
    {
        EnsureComboLabels();
        if (!settings.TryGetIntegerOption("SystemUpdateChannel", out _) &&
            this.FindControl<MyComboBox>("ComboSystemUpdateChannel") is { } channel)
        {
            channel.SelectedIndex = PclLauncherBuildIdentity.Current.Configuration switch
            {
                "Beta" => 1,
                "CI" => 2,
                _ => 0
            };
            channel.RefreshSelectionDisplay();
        }
        if (!_installation.SupportsCiChannel &&
            this.FindControl<MyComboBox>("ComboSystemUpdateChannel") is { SelectedIndex: 2 } scatterChannel)
        {
            int fallback = PclLauncherBuildIdentity.Current.Configuration is "Beta" or "CI" ? 1 : 0;
            scatterChannel.SelectedIndex = fallback;
            scatterChannel.RefreshSelectionDisplay();
            LauncherSettingsPageBinder.SaveIntegerOption(
                LauncherUpdatePolicy.ChannelSettingKey,
                fallback);
        }
        SyncChannelBaselineFromUi();
        _channelUserArmed = false;
        _isInitializing = false;
    }

    private void SyncChannelBaselineFromUi()
    {
        if (this.FindControl<MyComboBox>("ComboSystemUpdateChannel") is { } channel && channel.SelectedIndex >= 0)
            _lastUpdateChannel = channel.SelectedIndex;
    }

    /// <summary>
    /// DynamicResource item Content often resolves late; set explicit captions and refresh closed-state text.
    /// </summary>
    private void EnsureComboLabels()
    {
        if (this.FindControl<MyComboBox>("ComboSystemUpdateChannel") is { } channel)
        {
            ApplyItemLabel(channel, 0, "Setup.Update.Channel.Release", "正式版");
            ApplyItemLabel(channel, 1, "Setup.Update.Channel.Beta", "测试版");
            ApplyItemLabel(channel, 2, "Setup.Update.Channel.CI", "CI 通道");
            if (channel.Items[2] is MyComboBoxItem ciItem)
            {
                ciItem.IsEnabled = _installation.SupportsCiChannel;
                if (!_installation.SupportsCiChannel)
                {
                    ciItem.ToolTip = AvaloniaLocalizationManager.GetText(
                        "Setup.Update.Channel.CI.Package.Disabled",
                        "当前安装类型（安装包/系统包）不支持 CI 通道；请使用便携版或散包，或选择正式版/测试版。");
                }
            }
            if (channel.SelectedIndex < 0 && channel.ItemCount > 0)
                channel.SelectedIndex = LauncherSettingDefaults.GetInteger("SystemUpdateChannel", 0);
            channel.RefreshSelectionDisplay();
        }

        if (this.FindControl<MyComboBox>("ComboSystemUpdateMode") is { } mode)
        {
            ApplyItemLabel(mode, 0, "Setup.Update.Auto.DownloadAndInstall", "自动下载并安装");
            ApplyItemLabel(mode, 1, "Setup.Update.Auto.DownloadAndNotify", "仅下载");
            ApplyItemLabel(mode, 2, "Setup.Update.Auto.NotifyOnly", "仅提示");
            ApplyItemLabel(mode, 3, "Setup.Update.Auto.Disabled", "关闭");
            if (mode.SelectedIndex < 0 && mode.ItemCount > 0)
                mode.SelectedIndex = LauncherSettingDefaults.GetInteger("SystemUpdateMode", 1);
            mode.RefreshSelectionDisplay();
        }
    }

    private static void ApplyItemLabel(MyComboBox combo, int index, string resourceKey, string fallback)
    {
        if (index < 0 || index >= combo.ItemCount)
            return;
        if (combo.Items[index] is not MyComboBoxItem item)
            return;
        string text = AvaloniaLocalizationManager.GetText(resourceKey, fallback);
        if (string.IsNullOrWhiteSpace(text))
            text = fallback;
        // Always set plain string so SelectionText is available immediately.
        item.Content = text;
    }

    public void RefreshPage()
    {
        EnsureComboLabels();
        SetCurrentVersionText();
        if (_updateAvailableUi && !string.IsNullOrWhiteSpace(_preferredAssetUrl))
            ShowUpdateAvailableUi();
        else
            ShowCurrentVersionUi();
    }

    private void ShowCurrentVersionUi()
    {
        _updateAvailableUi = false;
        if (this.FindControl<MyCard>("CardUpdate") is { } updateCard)
            updateCard.IsVisible = false;

        if (this.FindControl<MyCard>("CardCheck") is { } checkCard)
        {
            ForceOpaqueVisible(checkCard);
            checkCard.IsVisible = true;
        }

        if (this.FindControl<MyButton>("BtnCheckAgain") is { } checkAgain)
            checkAgain.IsEnabled = !_isChecking;
    }

    private void ShowUpdateAvailableUi()
    {
        _updateAvailableUi = true;
        if (this.FindControl<MyCard>("CardCheck") is { } checkCard)
            checkCard.IsVisible = false;

        if (this.FindControl<MyCard>("CardUpdate") is { } updateCard)
        {
            ForceOpaqueVisible(updateCard);
            updateCard.IsVisible = true;
        }
    }

    private static void ForceOpaqueVisible(Control root)
    {
        root.Opacity = 1d;
        root.RenderTransform = null;
        root.IsHitTestVisible = true;
        foreach (Visual visual in root.GetVisualDescendants())
        {
            if (visual is not Control control)
                continue;
            control.Opacity = 1d;
            control.RenderTransform = null;
            control.IsHitTestVisible = true;
        }
    }

    private async Task MaybeAutoCheckAsync()
    {
        if (!this.IsAttachedToVisualTree() || _isChecking)
            return;

        int mode = LauncherSettingDefaults.GetInteger("SystemUpdateMode", 1);
        if (this.FindControl<MyComboBox>("ComboSystemUpdateMode") is { SelectedIndex: >= 0 } modeCombo)
            mode = modeCombo.SelectedIndex;

        // 3 = Disabled
        if (mode == 3)
        {
            if (this.FindControl<TextBlock>("TextCurrentDesc") is { } desc)
                desc.Text = "自动检查已关闭。你可以手动再次检查。";
            return;
        }

        _isChecking = true;
        if (this.FindControl<MyButton>("BtnCheckAgain") is { } checkAgain)
            checkAgain.IsEnabled = false;
        if (this.FindControl<TextBlock>("TextCurrentDesc") is { } checking)
            checking.Text = AvaloniaLocalizationManager.GetText("Setup.Update.Checking", "正在检查更新…");
        try
        {
            _ = _updateCoordinator.StartAutomaticUpdateOnceAsync();
            LauncherUpdateCheckResult? result = await _updateCoordinator.WaitForAutomaticCheckResultAsync()
                .ConfigureAwait(true);
            _preparedUpdate = _updateCoordinator.PreparedUpdate;
            if (result is not null && this.IsAttachedToVisualTree())
                await ApplyCheckResultAsync(result, automaticallyPrepare: false).ConfigureAwait(true);
        }
        finally
        {
            _isChecking = false;
            if (this.IsAttachedToVisualTree() &&
                this.FindControl<MyButton>("BtnCheckAgain") is { } button)
            {
                button.IsEnabled = true;
            }
        }
    }

    private void BtnChangelogDetail_Click(object? sender, RoutedEventArgs e)
    {
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(_latestReleaseUrl));
    }

    private void BtnChangelog_Click(object? sender, EventArgs e)
    {
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(_latestReleaseUrl));
    }

    private async void BtnCheckAgain_OnClick(object? sender, EventArgs e)
    {
        await CheckForUpdatesAsync().ConfigureAwait(true);
    }

    private async void BtnDownloadNow_Click(object? sender, EventArgs e)
    {
        if (_preparedUpdate is not null && _availableUpdate is not null)
        {
            _updateCoordinator.SkipAvailableVersion(_availableUpdate);
            _preparedUpdate = null;
            _availableUpdate = null;
            _preferredAssetUrl = null;
            ShowCurrentVersionUi();
            if (this.FindControl<TextBlock>("TextCurrentDesc") is { } description)
            {
                description.Text = AvaloniaLocalizationManager.GetText(
                    "Setup.Update.Skipped",
                    "已跳过此版本更新。");
            }
            return;
        }

        await ProcessAvailableUpdateAsync(mode: 1).ConfigureAwait(true);
    }

    private void BtnDownloadAndInstall_Click(object? sender, EventArgs e)
    {
        if (_isPreparingUpdate)
            return;
        _ = InstallOrPrepareUpdateAsync();
    }

    private async Task InstallOrPrepareUpdateAsync()
    {
        _isPreparingUpdate = true;
        SetUpdateButtonsEnabled(false);
        try
        {
            if (_preparedUpdate is { } prepared && File.Exists(prepared.StagedExecutablePath))
            {
                _updateCoordinator.InstallAndRestart(prepared);
                return;
            }

            await ProcessAvailableUpdateCoreAsync(mode: 0).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs(
                    "自动更新失败",
                    ex.Message + "\n\n你仍可在发布页手动下载。",
                    AvaloniaLocalizationManager.GetText("Common.Action.Confirm", "好")));
            _isPreparingUpdate = false;
            SetUpdateButtonsEnabled(true);
        }
    }

    private void ComboSystemUpdateChannel_DropDownOpened(object? sender, EventArgs e)
    {
        _channelUserArmed = true;
    }

    private void ComboSystemUpdateBranch_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not MyComboBox combo)
            return;
        if (_isInitializing || _isRevertingChannel || combo.SelectedIndex < 0)
            return;

        int selectedIndex = combo.SelectedIndex;

        if (selectedIndex == 2 && !_installation.SupportsCiChannel)
        {
            _channelUserArmed = false;
            _isRevertingChannel = true;
            try
            {
                combo.SelectedIndex = Math.Clamp(_lastUpdateChannel, 0, 1);
            }
            finally
            {
                _isRevertingChannel = false;
            }
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs(
                    AvaloniaLocalizationManager.GetText("Setup.Update.Channel.Title", "更新通道"),
                    AvaloniaLocalizationManager.GetText(
                        "Setup.Update.Channel.CI.Scatter.Disabled",
                        "散包版不能更新到 CI 版本；请使用正式版或测试版通道。"),
                    AvaloniaLocalizationManager.GetText("Common.Action.Confirm", "好")));
            return;
        }

        // Programmatic re-bind from settings binder — not a user action.
        if (!_channelUserArmed)
        {
            _lastUpdateChannel = selectedIndex;
            return;
        }

        _channelUserArmed = false;

        if (selectedIndex == _lastUpdateChannel)
            return;

        if (selectedIndex == 0)
        {
            _lastUpdateChannel = 0;
            LauncherSettingsPageBinder.SaveIntegerOption(
                LauncherUpdatePolicy.ChannelSettingKey,
                selectedIndex);
            _updateAvailableUi = false;
            ShowCurrentVersionUi();
            return;
        }

        int previousIndex = _lastUpdateChannel;
        void Complete(bool confirmed)
        {
            if (confirmed)
            {
                _lastUpdateChannel = selectedIndex;
                LauncherSettingsPageBinder.SaveIntegerOption(
                    LauncherUpdatePolicy.ChannelSettingKey,
                    selectedIndex);
                _updateAvailableUi = false;
                ShowCurrentVersionUi();
                _ = CheckForUpdatesAsync();
                return;
            }

            _isRevertingChannel = true;
            try
            {
                combo.SelectedIndex = Math.Clamp(previousIndex, 0, Math.Max(0, combo.ItemCount - 1));
                _lastUpdateChannel = combo.SelectedIndex;
            }
            finally
            {
                _isRevertingChannel = false;
            }
        }

        string channel = selectedIndex == 1
            ? AvaloniaLocalizationManager.GetText("Setup.Update.Channel.Beta", "测试版")
            : AvaloniaLocalizationManager.GetText("Setup.Update.Channel.CI", "CI 通道");
        string body = selectedIndex == 1
            ? AvaloniaLocalizationManager.GetText(
                "Setup.Update.Channel.Beta.Warning.Message",
                "即将切换到测试版。\n\n测试版可能包含未完成的功能，稳定性可能较低。更新后如需返回正式版，可能需要等待下一正式版或手动安装。")
            : AvaloniaLocalizationManager.GetText(
                "Setup.Update.Channel.Dev.Warning.Message",
                "即将切换到 CI 通道。\n\n这些构建可能非常不稳定。更新后如需返回正式版或测试版，可能需要手动安装。");
        SettingsConfirmRequestedEventArgs args = new(
            AvaloniaLocalizationManager.GetText("Setup.Update.Channel.Common.Warning.Title", "继续之前"),
            body,
            Complete,
            primaryButton: AvaloniaLocalizationManager.GetText("Setup.Update.Channel.Common.Warning.Confirm", "继续"),
            isWarn: true);
        if (ConfirmRequested is { } confirmRequested)
            confirmRequested.Invoke(this, args);
        else
            Complete(false);
    }

    private void ComboSystemUpdateMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Persistence is handled by LauncherSettingsPageBinder (Tag=SystemUpdateMode).
    }

    bool IDeferredSettingsPersistence.IsPersistenceDeferred(string settingKey) =>
        string.Equals(
            settingKey,
            LauncherUpdatePolicy.ChannelSettingKey,
            StringComparison.OrdinalIgnoreCase);

    private async Task CheckForUpdatesAsync()
    {
        if (_isChecking)
            return;
        _isChecking = true;

        ShowCurrentVersionUi();
        if (this.FindControl<MyButton>("BtnCheckAgain") is { } checkAgain)
            checkAgain.IsEnabled = false;
        if (this.FindControl<TextBlock>("TextCurrentDesc") is { } desc)
            desc.Text = AvaloniaLocalizationManager.GetText("Setup.Update.Checking", "正在检查更新…");

        try
        {
            int channelIndex = 0;
            if (this.FindControl<MyComboBox>("ComboSystemUpdateChannel") is { } channelCombo)
                channelIndex = Math.Max(0, channelCombo.SelectedIndex);

            UpdateChannel channel = channelIndex switch
            {
                1 => UpdateChannel.Beta,
                2 => UpdateChannel.CI,
                _ => UpdateChannel.Release
            };

            LauncherUpdateCheckResult result = await _updateCoordinator
                .CheckAsync(channel)
                .ConfigureAwait(true);
            if (this.IsAttachedToVisualTree())
                await ApplyCheckResultAsync(result, automaticallyPrepare: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
        catch (Exception ex)
        {
            if (!this.IsAttachedToVisualTree())
                return;
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs(
                    AvaloniaLocalizationManager.GetText("Setup.Update.CheckFailed", "无法检查更新"),
                    ex.Message,
                    AvaloniaLocalizationManager.GetText("Common.Action.Confirm", "好")));
            ShowCurrentVersionUi();
            if (this.FindControl<TextBlock>("TextCurrentDesc") is { } errorDesc)
                errorDesc.Text = AvaloniaLocalizationManager.GetText("Setup.Update.CheckFailed", "无法检查更新");
        }
        finally
        {
            _isChecking = false;
            if (this.IsAttachedToVisualTree() &&
                this.FindControl<MyButton>("BtnCheckAgain") is { } button)
            {
                button.IsEnabled = true;
            }
        }
    }

    private async Task ApplyCheckResultAsync(LauncherUpdateCheckResult result, bool automaticallyPrepare)
    {
        if (!result.Success)
        {
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs(
                    AvaloniaLocalizationManager.GetText("Setup.Update.CheckFailed", "无法检查更新"),
                    result.ErrorMessage ?? AvaloniaLocalizationManager.GetText("Setup.Update.Error.NetworkFailed", "请检查网络连接后重试。"),
                    AvaloniaLocalizationManager.GetText("Common.Action.Confirm", "好")));
            ShowCurrentVersionUi();
            if (this.FindControl<TextBlock>("TextCurrentDesc") is { } failedDesc)
                failedDesc.Text = AvaloniaLocalizationManager.GetText("Setup.Update.CheckFailed", "无法检查更新");
            return;
        }

        _availableUpdate = result.IsUpdateAvailable ? result : null;
        _preparedUpdate = _updateCoordinator.PreparedUpdate;
        if (_preparedUpdate?.Package.TargetTag != result.Package?.TargetTag)
            _preparedUpdate = null;
        SetUpdateActionButtons(_preparedUpdate is not null);
        SetUpdateButtonsEnabled(!_updateCoordinator.IsUpdateOperationActive);
        SetUpdateButtonsVisible(!_updateCoordinator.IsUpdateTransferActive);
        _latestReleaseUrl = result.ReleaseUrl ?? ReleasesUrl;
        _preferredAssetUrl = result.PreferredAssetUrl;

        if (!result.IsUpdateAvailable)
        {
            _availableUpdate = null;
            _preferredAssetUrl = null;
            ShowCurrentVersionUi();
            if (this.FindControl<TextBlock>("TextCurrentDesc") is { } currentDesc)
            {
                string latest = AvaloniaLocalizationManager.GetText("Setup.Update.Latest", "已是最新版本");
                currentDesc.Text = string.IsNullOrWhiteSpace(result.CurrentVersion)
                    ? latest + " · " + DescribeCurrentBuild()
                    : $"{latest}（{result.CurrentVersion}） · {DescribeCurrentBuild()}";
            }
            return;
        }

        if (this.FindControl<TextBlock>("TextUpdateName") is { } updateName)
            updateName.Text = "PCL N " + (result.LatestVersion ?? "");
        if (this.FindControl<TextBlock>("TextUpdateDesc") is { } updateDesc)
        {
            updateDesc.Text = _preparedUpdate is not null
                ? "更新已下载并通过校验"
                : result.Channel is UpdateChannel.CI
                    ? (result.ReleaseName ?? "CI") + " · " + AvaloniaLocalizationManager.GetText("Setup.Update.Available", "有可用更新")
                    : AvaloniaLocalizationManager.GetText("Setup.Update.Available", "有可用更新");
        }

        if (this.FindControl<TextBlock>("TextChangelog") is { } changelog)
        {
            string guide = AvaloniaLocalizationManager.GetText(
                "Setup.Update.Changelog.Placeholder",
                "此更新包含问题修复与改进。\n\n部分内容可能因设备、系统版本或使用方式而略有不同。建议在网络状况良好时完成下载与安装。\n\n有关此更新的完整说明与变更列表，可在 GitHub 上查看。");
            if (result.Channel is UpdateChannel.CI)
            {
                guide += "\n\n" + AvaloniaLocalizationManager.GetText(
                    "Setup.Update.FullOnly.CI",
                    "CI 通道使用内容寻址分块更新，只会下载本地缺少的分块。");
            }
            else if (!result.SupportsPatches)
            {
                guide += "\n\n" + AvaloniaLocalizationManager.GetText(
                    "Setup.Update.FullOnly.NoApplicablePatch",
                    "当前安装版本没有适用的旧式补丁，将改用内容寻址分块更新。");
            }
            changelog.Text = guide;
        }

        ShowUpdateAvailableUi();
        if (!automaticallyPrepare)
            return;

        int updateMode = this.FindControl<MyComboBox>("ComboSystemUpdateMode") is { SelectedIndex: >= 0 } modeCombo
            ? modeCombo.SelectedIndex
            : LauncherSettingDefaults.GetInteger("SystemUpdateMode", 1);
        if (updateMode is 0 or 1 or 2)
            await ProcessAvailableUpdateAsync(updateMode).ConfigureAwait(true);
    }

    private void SetCurrentVersionText()
    {
        string version = "PCL N " + PclMetadata.Current.DisplayVersion;
        if (this.FindControl<TextBlock>("TextCurrentVersion") is { } currentVersion)
            currentVersion.Text = version;
        if (this.FindControl<TextBlock>("TextCurrentDesc") is { } currentDescription && !_isChecking && !_updateAvailableUi)
            currentDescription.Text = AvaloniaLocalizationManager.GetText("Setup.Update.Latest", "已是最新版本") +
                                      " · " + DescribeCurrentBuild();
    }

    private async Task ProcessAvailableUpdateAsync(
        int mode,
        CancellationToken cancellationToken = default)
    {
        if (_isPreparingUpdate)
            return;
        if (_availableUpdate?.Package is null)
        {
            OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(_preferredAssetUrl ?? _latestReleaseUrl));
            return;
        }

        _isPreparingUpdate = true;
        SetUpdateButtonsEnabled(false);
        try
        {
            await ProcessAvailableUpdateCoreAsync(mode, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Page navigation or a newer update check cancelled the transfer.
        }
        catch (Exception ex)
        {
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs(
                    "自动更新失败",
                    ex.Message + "\n\n你仍可在发布页手动下载。",
                    AvaloniaLocalizationManager.GetText("Common.Action.Confirm", "好")));
        }
        finally
        {
            _isPreparingUpdate = false;
            SetUpdateButtonsEnabled(true);
        }
    }

    private async Task ProcessAvailableUpdateCoreAsync(
        int mode,
        CancellationToken cancellationToken = default)
    {
        if (_availableUpdate is null)
            return;
        await _updateCoordinator.HandleAvailableUpdateAsync(_availableUpdate, mode, cancellationToken)
            .ConfigureAwait(true);
        _preparedUpdate = _updateCoordinator.PreparedUpdate;
    }

    private void OnUpdateProgressChanged(object? sender, LauncherUpdateProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetUpdateButtonsVisible(progress.Stage is LauncherUpdateStage.Ready);
            if (this.FindControl<TextBlock>("TextUpdateDesc") is { } description)
                description.Text = progress.Message;
        });
    }

    private void OnPreparedUpdateChanged(PreparedLauncherUpdate? prepared)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!this.IsAttachedToVisualTree())
                return;
            if (prepared is not null &&
                !string.Equals(
                    prepared.Package.TargetTag,
                    _availableUpdate?.Package?.TargetTag,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _preparedUpdate = prepared;
            SetUpdateActionButtons(prepared is not null);
            SetUpdateButtonsEnabled(!_updateCoordinator.IsUpdateOperationActive);
            SetUpdateButtonsVisible(true);
            if (prepared is not null && this.FindControl<TextBlock>("TextUpdateDesc") is { } description)
                description.Text = "更新已下载并通过校验";
        });
    }

    private void OnUpdateOperationActiveChanged(bool active)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.IsAttachedToVisualTree())
            {
                SetUpdateButtonsEnabled(!active);
                if (!active)
                    SetUpdateButtonsVisible(true);
            }
        });
    }

    private void SetUpdateActionButtons(bool prepared)
    {
        if (this.FindControl<MyButton>("BtnDownloadNow") is { } download)
        {
            download.Text = prepared
                ? AvaloniaLocalizationManager.GetText("Setup.Update.Prompt.SkipVersion", "跳过版本")
                : AvaloniaLocalizationManager.GetText("Setup.Update.DownloadNow", "下载");
        }
        if (this.FindControl<MyButton>("BtnDownloadAndInstall") is { } install)
        {
            install.Text = prepared
                ? AvaloniaLocalizationManager.GetText("Setup.Update.RestartAndInstall", "重启并安装")
                : AvaloniaLocalizationManager.GetText("Setup.Update.DownloadAndInstall", "下载并安装");
        }
    }

    private void SetUpdateButtonsEnabled(bool enabled)
    {
        if (this.FindControl<MyButton>("BtnDownloadNow") is { } download)
            download.IsEnabled = enabled;
        if (this.FindControl<MyButton>("BtnDownloadAndInstall") is { } install)
            install.IsEnabled = enabled;
    }

    private void SetUpdateButtonsVisible(bool visible)
    {
        if (this.FindControl<MyButton>("BtnDownloadNow") is { } download)
            download.IsVisible = visible;
        if (this.FindControl<MyButton>("BtnDownloadAndInstall") is { } install)
            install.IsVisible = visible;
    }

    private static string DescribeCurrentBuild()
    {
        LauncherBuildIdentity identity = PclLauncherBuildIdentity.Current;
        string channel = identity.Configuration switch
        {
            "Release" => "正式版",
            "Beta" => "测试版",
            _ => "CI 版"
        };
        string runtime = identity.NormalizedRuntimeVariant.StartsWith("NoRuntime", StringComparison.Ordinal)
            ? "依赖 .NET 运行时"
            : "自包含";
        return $"{channel} / {runtime} / {identity.RuntimeId}";
    }

}
