// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Online;
using PCL.Desktop.Localization;
using PCL.Online;

namespace PCL.Desktop.Features.Settings.Views;

#pragma warning disable CA1863, CS0067

public partial class PageSetupOnline : MyPageRight, ISettingsPageInteractionSource
{
    private bool _loading;
    private bool _subscribed;

    public PageSetupOnline()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        AttachedToVisualTree += (_, _) =>
        {
            SubscribeNotices();
            RefreshOnlineState();
        };
        DetachedFromVisualTree += (_, _) => UnsubscribeNotices();
    }

    public event EventHandler? MicrosoftLoginRequested;

    public event EventHandler<SettingsPathRequestedEventArgs>? OpenPathRequested;

    public event EventHandler<SettingsUrlRequestedEventArgs>? OpenUrlRequested;

    public event EventHandler<SettingsMessageRequestedEventArgs>? MessageRequested;

    public event EventHandler<SettingsConfirmRequestedEventArgs>? ConfirmRequested;

    private void BtnLogin_Click(object? sender, IconTextButtonClickEventArgs e) =>
        MicrosoftLoginRequested?.Invoke(this, EventArgs.Empty);

    private void BtnLogout_Click(object? sender, IconTextButtonClickEventArgs e)
    {
        ConfirmRequested?.Invoke(this, new SettingsConfirmRequestedEventArgs(
            AvaloniaLocalizationManager.GetText("Online.Account.Disconnect.Title", "断开云端账户"),
            AvaloniaLocalizationManager.GetText(
                "Online.Account.Disconnect.Warning",
                "这只会断开 N Cloud，不会删除启动器中的 Microsoft 游戏档案。下次可重新连接。"),
            confirmed =>
            {
                if (!confirmed)
                    return;
                DesktopOnlineRuntime.Host.DisconnectAccount();
                RefreshOnlineState();
            },
            AvaloniaLocalizationManager.GetText("Online.Account.Disconnect", "断开连接"),
            AvaloniaLocalizationManager.GetText("Common.Action.Cancel", "取消"),
            isWarn: true));
    }

    private void BtnDeleteCloudProfile_Click(object? sender, IconTextButtonClickEventArgs e)
    {
        ConfirmRequested?.Invoke(this, new SettingsConfirmRequestedEventArgs(
            AvaloniaLocalizationManager.GetText("Online.Account.DeleteCloudAndLogout.Title", "删除云端数据"),
            AvaloniaLocalizationManager.GetText(
                "Online.Account.DeleteCloudAndLogout.Warning",
                "将永久删除当前账户保存在 N Cloud 的同步数据，并断开连接。此操作不可撤销。"),
            confirmed =>
            {
                if (confirmed)
                    _ = DeleteCloudProfileAsync();
            },
            AvaloniaLocalizationManager.GetText("Online.Account.DeleteCloudAndLogout", "删除云端数据"),
            AvaloniaLocalizationManager.GetText("Common.Action.Cancel", "取消"),
            isWarn: true));
    }

    private async Task DeleteCloudProfileAsync()
    {
        DeleteCloudProfileButton.IsEnabled = false;
        try
        {
            await CloudSyncService.DeleteCloudProfileAsync().ConfigureAwait(true);
            DesktopOnlineRuntime.Host.DisconnectAccount();
            RefreshOnlineState();
            MessageRequested?.Invoke(this, new SettingsMessageRequestedEventArgs(
                AvaloniaLocalizationManager.GetText("Online.Account.DeleteCloudAndLogout.Success.Title", "删除完成"),
                AvaloniaLocalizationManager.GetText(
                    "Online.Account.DeleteCloudAndLogout.Success",
                    "云端同步数据已删除，N Cloud 已断开。")));
        }
        catch (Exception exception)
        {
            MessageRequested?.Invoke(this, new SettingsMessageRequestedEventArgs(
                AvaloniaLocalizationManager.GetText("Online.Account.DeleteCloudAndLogout.Failed.Title", "删除失败"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    AvaloniaLocalizationManager.GetText(
                        "Online.Account.DeleteCloudAndLogout.Failed",
                        "未能删除云端同步数据：{0}"),
                    exception.Message)));
        }
        finally
        {
            DeleteCloudProfileButton.IsEnabled = true;
        }
    }

    private void BtnRetrySync_Click(object? sender, IconTextButtonClickEventArgs e)
    {
        RetrySyncButton.IsEnabled = false;
        if (!CloudSyncService.RetryLastFailed())
            RetrySyncButton.IsEnabled = true;
    }

    private void BtnSyncDisable_Click(object? sender, IconTextButtonClickEventArgs e)
    {
        SetSyncBoolean("CloudSyncEnabled", false);
        ReloadSyncSettings();
    }

    private void SyncCheckBoxChange(object? sender, bool user)
    {
        if (_loading || sender is not MyCheckBox checkBox || checkBox.Tag is not string key)
            return;

        SetSyncBoolean(key, checkBox.Checked == true);
        UpdateSyncSettingsState();
        if (user && DesktopOnlineRuntime.Host.IsEnabled)
        {
            CloudSyncService.TrySyncInBackground(
                "settings",
                CloudSyncService.SyncMode.LocalOverwrite);
        }
    }

    public void RefreshOnlineState()
    {
        bool loggedIn = OnlineAccountService.IsLoggedIn;
        NotLoggedInPanel.IsVisible = !loggedIn;
        LoggedInPanel.IsVisible = loggedIn;
        SyncCard.IsVisible = loggedIn;
        if (loggedIn)
        {
            UserNameText.Text = OnlineAccountService.UserName ??
                               AvaloniaLocalizationManager.GetText("Common.State.Unknown", "未知");
            AccountTypeText.Text = OnlineAccountService.OwnsMinecraft
                ? AvaloniaLocalizationManager.GetText("Online.Account.OwnsMinecraft", "Microsoft 正版档案")
                : AvaloniaLocalizationManager.GetText("Online.Account.DoesNotOwnMinecraft", "Microsoft 账户");
            ReloadSyncSettings();
            SetCloudSyncUnavailable(!CloudSyncService.IsAvailable);
        }
        else
        {
            SetCloudSyncUnavailable(false);
        }
    }

    private void ReloadSyncSettings()
    {
        _loading = true;
        try
        {
            SyncEnabledCheckBox.Checked = ReadSyncBoolean("CloudSyncEnabled");
            SyncAccountCheckBox.Checked = ReadSyncBoolean("CloudSyncAccount");
            SyncFavoritesCheckBox.Checked = ReadSyncBoolean("CloudSyncFavorites");
            SyncUiCheckBox.Checked = ReadSyncBoolean("CloudSyncUiPreferences");
            SyncHintsCheckBox.Checked = ReadSyncBoolean("CloudSyncHintPreferences");
            SyncDownloadsCheckBox.Checked = ReadSyncBoolean("CloudSyncDownloadPreferences");
            SyncLaunchCheckBox.Checked = ReadSyncBoolean("CloudSyncLaunchPreferences");
            SyncHomepageCheckBox.Checked = ReadSyncBoolean("CloudSyncHomepagePreferences");
            SyncMusicCheckBox.Checked = ReadSyncBoolean("CloudSyncMusicPreferences");
            SyncUpdatesCheckBox.Checked = ReadSyncBoolean("CloudSyncUpdatePreferences");
            SyncVariablesCheckBox.Checked = ReadSyncBoolean("CloudSyncCustomVariables");
        }
        finally
        {
            _loading = false;
        }

        UpdateSyncSettingsState();
    }

    private void UpdateSyncSettingsState()
    {
        bool enabled = ReadSyncBoolean("CloudSyncEnabled");
        SyncSectionsPanel.IsEnabled = enabled;
        SyncSectionsPanel.Opacity = enabled ? 1d : 0.55d;
        SyncDisabledHint.IsVisible = !enabled;
        DisableSyncButton.IsEnabled = enabled;
    }

    private void SetCloudSyncUnavailable(bool unavailable)
    {
        SyncContentPanel.IsEnabled = !unavailable;
        SyncUnavailablePanel.IsVisible = unavailable;
        RetrySyncButton.IsEnabled = unavailable;
    }

    private static bool ReadSyncBoolean(string key) =>
        OnlineRuntime.Host.GetBoolean("Online." + key);

    private static void SetSyncBoolean(string key, bool value)
    {
        OnlineRuntime.Host.SetBoolean("Online." + key, value);
        OnlineRuntime.Host.Flush();
    }

    private void SubscribeNotices()
    {
        if (_subscribed)
            return;
        CloudSyncService.Notice += OnCloudSyncNotice;
        _subscribed = true;
    }

    private void UnsubscribeNotices()
    {
        if (!_subscribed)
            return;
        CloudSyncService.Notice -= OnCloudSyncNotice;
        _subscribed = false;
    }

    private void OnCloudSyncNotice(CloudSyncService.NoticeType type, int retry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (type)
            {
                case CloudSyncService.NoticeType.Starting:
                case CloudSyncService.NoticeType.Retry:
                    SetCloudSyncUnavailable(false);
                    RetrySyncButton.IsEnabled = false;
                    break;
                case CloudSyncService.NoticeType.Success:
                    SetCloudSyncUnavailable(false);
                    RefreshOnlineState();
                    break;
                case CloudSyncService.NoticeType.Failed:
                    SetCloudSyncUnavailable(true);
                    break;
            }
        });
    }

    private StackPanel NotLoggedInPanel => this.FindControl<StackPanel>("PanNotLoggedIn")!;
    private Grid LoggedInPanel => this.FindControl<Grid>("PanLoggedIn")!;
    private MyCard SyncCard => this.FindControl<MyCard>("CardSync")!;
    private TextBlock UserNameText => this.FindControl<TextBlock>("LabUserName")!;
    private TextBlock AccountTypeText => this.FindControl<TextBlock>("LabAccountType")!;
    private MyIconTextButton DeleteCloudProfileButton => this.FindControl<MyIconTextButton>("BtnDeleteCloudProfile")!;
    private MyIconTextButton RetrySyncButton => this.FindControl<MyIconTextButton>("BtnRetrySync")!;
    private MyIconTextButton DisableSyncButton => this.FindControl<MyIconTextButton>("BtnSyncDisable")!;
    private MyCheckBox SyncEnabledCheckBox => this.FindControl<MyCheckBox>("CheckCloudSyncEnabled")!;
    private MyCheckBox SyncAccountCheckBox => this.FindControl<MyCheckBox>("CheckSyncAccount")!;
    private MyCheckBox SyncFavoritesCheckBox => this.FindControl<MyCheckBox>("CheckSyncFavorites")!;
    private MyCheckBox SyncUiCheckBox => this.FindControl<MyCheckBox>("CheckSyncUiPreferences")!;
    private MyCheckBox SyncHintsCheckBox => this.FindControl<MyCheckBox>("CheckSyncHintPreferences")!;
    private MyCheckBox SyncDownloadsCheckBox => this.FindControl<MyCheckBox>("CheckSyncDownloadPreferences")!;
    private MyCheckBox SyncLaunchCheckBox => this.FindControl<MyCheckBox>("CheckSyncLaunchPreferences")!;
    private MyCheckBox SyncHomepageCheckBox => this.FindControl<MyCheckBox>("CheckSyncHomepagePreferences")!;
    private MyCheckBox SyncMusicCheckBox => this.FindControl<MyCheckBox>("CheckSyncMusicPreferences")!;
    private MyCheckBox SyncUpdatesCheckBox => this.FindControl<MyCheckBox>("CheckSyncUpdatePreferences")!;
    private MyCheckBox SyncVariablesCheckBox => this.FindControl<MyCheckBox>("CheckSyncCustomVariables")!;
    private WrapPanel SyncSectionsPanel => this.FindControl<WrapPanel>("PanSyncSections")!;
    private TextBlock SyncDisabledHint => this.FindControl<TextBlock>("LabSyncDisabledHint")!;
    private StackPanel SyncContentPanel => this.FindControl<StackPanel>("PanSyncContent")!;
    private Grid SyncUnavailablePanel => this.FindControl<Grid>("PanSyncUnavailable")!;
}
