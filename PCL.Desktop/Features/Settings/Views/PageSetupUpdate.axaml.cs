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

public partial class PageSetupUpdate : MyPageRight, IRefreshableSettingsPage, ISettingsPageInteractionSource
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
    private LauncherUpdateService _updateService = new();
    private CancellationTokenSource? _checkCts;

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
        _channelUserArmed = false;
        _autoCheckScheduled = false;
        // Defer until styles/DynamicResource + settings re-bind settle, then paint combos and auto-check.
        Dispatcher.UIThread.Post(OnPageReady, DispatcherPriority.Loaded);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        CancelInFlightCheck();
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
            if (channel.SelectedIndex < 0 && channel.ItemCount > 0)
                channel.SelectedIndex = LauncherSettingDefaults.GetInteger("SystemUpdateChannel", 0);
            channel.RefreshSelectionDisplay();
        }

        if (this.FindControl<MyComboBox>("ComboSystemUpdateMode") is { } mode)
        {
            ApplyItemLabel(mode, 0, "Setup.Update.Auto.DownloadAndInstall", "自动下载并安装");
            ApplyItemLabel(mode, 1, "Setup.Update.Auto.DownloadAndNotify", "自动下载并通知");
            ApplyItemLabel(mode, 2, "Setup.Update.Auto.NotifyOnly", "仅通知");
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

        await CheckForUpdatesAsync().ConfigureAwait(true);
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

    private void BtnDownloadNow_Click(object? sender, EventArgs e)
    {
        string target = _preferredAssetUrl ?? _latestReleaseUrl;
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(target));
    }

    private void BtnDownloadAndInstall_Click(object? sender, EventArgs e)
    {
        string target = _preferredAssetUrl ?? _latestReleaseUrl;
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(target));
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

    private void CancelInFlightCheck()
    {
        try
        {
            _checkCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }
    }

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

        CancelInFlightCheck();
        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();
        CancellationToken token = _checkCts.Token;

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

            bool preferPlugin = DesktopHost.Current.SettingsPages.Pages.Count > 0;
            string commit = !string.IsNullOrWhiteSpace(PclBuildInfo.SourceRevisionId)
                ? PclBuildInfo.SourceRevisionId
                : PclMetadata.Current.Commit;

            // Prefer base+suffix from metadata; CompareVersions normalizes "1.1.8 release" vs "1.1.8-release".
            string currentVersion = PclMetadata.Current.DisplayVersion;
            if (string.IsNullOrWhiteSpace(currentVersion))
                currentVersion = PclMetadata.Current.Version.Base;

            LauncherUpdateCheckResult result = await _updateService
                .CheckAsync(
                    channel,
                    currentVersion,
                    preferPlugin,
                    currentCommitSha: commit,
                    cancellationToken: token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested || !this.IsAttachedToVisualTree())
                return;

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

            _latestReleaseUrl = result.ReleaseUrl ?? ReleasesUrl;
            _preferredAssetUrl = result.PreferredAssetUrl;

            if (result.IsUpdateAvailable)
            {
                if (this.FindControl<TextBlock>("TextUpdateName") is { } updateName)
                    updateName.Text = "PCL N " + (result.LatestVersion ?? "");
                if (this.FindControl<TextBlock>("TextUpdateDesc") is { } updateDesc)
                {
                    updateDesc.Text = result.Channel is UpdateChannel.CI
                        ? (result.ReleaseName ?? "CI") + " · " + AvaloniaLocalizationManager.GetText("Setup.Update.Available", "有可用更新")
                        : AvaloniaLocalizationManager.GetText("Setup.Update.Available", "有可用更新");
                }

                // Fixed, plain copy; details live on GitHub.
                if (this.FindControl<TextBlock>("TextChangelog") is { } changelog)
                {
                    string guide = AvaloniaLocalizationManager.GetText(
                        "Setup.Update.Changelog.Placeholder",
                        "此更新包含问题修复与改进。\n\n部分内容可能因设备、系统版本或使用方式而略有不同。建议在网络状况良好时完成下载与安装。\n\n有关此更新的完整说明与变更列表，可在 GitHub 上查看。");
                    if (result.Channel is UpdateChannel.CI || !result.SupportsPatches)
                        guide += "\n\n此版本仅提供完整下载。";
                    changelog.Text = guide;
                }

                ShowUpdateAvailableUi();
            }
            else
            {
                _preferredAssetUrl = null;
                ShowCurrentVersionUi();
                if (this.FindControl<TextBlock>("TextCurrentDesc") is { } currentDesc)
                {
                    string latest = AvaloniaLocalizationManager.GetText("Setup.Update.Latest", "已是最新版本");
                    currentDesc.Text = string.IsNullOrWhiteSpace(result.CurrentVersion)
                        ? latest
                        : $"{latest}（{result.CurrentVersion}）";
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away or a newer check started.
        }
        catch (ObjectDisposedException)
        {
            _updateService = new LauncherUpdateService();
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs(
                    AvaloniaLocalizationManager.GetText("Setup.Update.CheckFailed", "无法检查更新"),
                    "请再试一次。",
                    AvaloniaLocalizationManager.GetText("Common.Action.Confirm", "好")));
            ShowCurrentVersionUi();
            if (this.FindControl<TextBlock>("TextCurrentDesc") is { } disposedDesc)
                disposedDesc.Text = AvaloniaLocalizationManager.GetText("Setup.Update.CheckFailed", "无法检查更新");
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || !this.IsAttachedToVisualTree())
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

    private void SetCurrentVersionText()
    {
        string version = "PCL N " + PclMetadata.Current.DisplayVersion;
        if (this.FindControl<TextBlock>("TextCurrentVersion") is { } currentVersion)
            currentVersion.Text = version;
        if (this.FindControl<TextBlock>("TextCurrentDesc") is { } currentDescription && !_isChecking && !_updateAvailableUi)
            currentDescription.Text = AvaloniaLocalizationManager.GetText("Setup.Update.Latest", "已是最新版本");
    }

}
