// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using PCL.Application.Updates;
using PCL.Core.App;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Hosting;

#pragma warning disable CA1822, CS0067

namespace PCL.Desktop.Features.Settings.Views;

public partial class PageSetupUpdate : MyPageRight, IRefreshableSettingsPage, ISettingsPageInteractionSource
{
    private const string ReleasesUrl = "https://github.com/MuXue1230-owo/PCL-N/releases";
    private string _latestReleaseUrl = ReleasesUrl;
    private string? _preferredAssetUrl;
    private bool _isInitializing = true;
    private bool _isRevertingChannel;
    private bool _isChecking;
    private int _lastUpdateChannel;
    private LauncherUpdateService _updateService = new();
    private CancellationTokenSource? _checkCts;

    public PageSetupUpdate()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = PanBack;
        LauncherSettingsPageBinder.Attach(this, _ =>
            _lastUpdateChannel = Math.Max(0, UpdateChannelCombo.SelectedIndex));
        _isInitializing = false;
        AttachedToVisualTree += (_, _) => RefreshPage();
        // Page instances are cached by Setup navigation — do NOT dispose the
        // HttpClient on detach, or the next "检查更新" hits ObjectDisposedException.
        DetachedFromVisualTree += (_, _) => CancelInFlightCheck();
        RefreshPage();
    }

    public event EventHandler<SettingsPathRequestedEventArgs>? OpenPathRequested;

    public event EventHandler<SettingsUrlRequestedEventArgs>? OpenUrlRequested;

    public event EventHandler<SettingsMessageRequestedEventArgs>? MessageRequested;

    public event EventHandler<SettingsConfirmRequestedEventArgs>? ConfirmRequested;

    public void RefreshPage()
    {
        SetCurrentVersionText();
        if (this.FindControl<MyCard>("CardUpdate") is { } updateCard)
            updateCard.IsVisible = false;
        if (this.FindControl<MyCard>("CardCheck") is { } checkCard)
            checkCard.IsVisible = true;
        if (this.FindControl<MyButton>("BtnCheckAgain") is { } checkAgain)
            checkAgain.IsEnabled = !_isChecking;
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

    private void BtnUpdate_Click(object? sender, EventArgs e)
    {
        string target = _preferredAssetUrl ?? _latestReleaseUrl;
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(target));
    }

    private void ComboSystemUpdateBranch_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Use sender — FindControl is unavailable while XAML is still populating SelectedIndex.
        if (sender is not MyComboBox combo)
            return;
        if (_isInitializing || _isRevertingChannel || combo.SelectedIndex < 0)
            return;

        int selectedIndex = combo.SelectedIndex;
        if (selectedIndex == 0)
        {
            _lastUpdateChannel = 0;
            RefreshPage();
            return;
        }

        int previousIndex = _lastUpdateChannel;
        void Complete(bool confirmed)
        {
            if (confirmed)
            {
                _lastUpdateChannel = selectedIndex;
                RefreshPage();
                return;
            }

            _isRevertingChannel = true;
            try
            {
                combo.SelectedIndex = Math.Clamp(previousIndex, 0, Math.Max(0, combo.ItemCount - 1));
            }
            finally
            {
                _isRevertingChannel = false;
            }
        }

        string channel = selectedIndex == 1 ? "测试版" : "CI 通道";
        string extra = selectedIndex == 2
            ? "\n\nCI 通道从 dev 分支每次 CI 构建拉取全量包，不提供版本间 Patch。"
            : string.Empty;
        SettingsConfirmRequestedEventArgs args = new(
            "切换更新通道",
            $"{channel}可能包含尚未充分验证的功能和兼容性问题。确定切换到{channel}吗？{extra}",
            Complete,
            primaryButton: "仍然切换",
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
        if (this.FindControl<MyButton>("BtnCheckAgain") is { } checkAgain)
            checkAgain.IsEnabled = false;
        if (this.FindControl<TextBlock>("TextCurrentDesc") is { } desc)
            desc.Text = "正在检查更新…";

        CancelInFlightCheck();
        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();
        CancellationToken token = _checkCts.Token;

        try
        {
            UpdateChannel channel = UpdateChannelCombo.SelectedIndex switch
            {
                1 => UpdateChannel.Beta,
                2 => UpdateChannel.CI,
                _ => UpdateChannel.Release
            };

            // Prefer WithPlugin assets when any HostModule settings pages were injected.
            bool preferPlugin = DesktopHost.Current.SettingsPages.Pages.Count > 0;
            string commit = !string.IsNullOrWhiteSpace(PclBuildInfo.SourceRevisionId)
                ? PclBuildInfo.SourceRevisionId
                : PclMetadata.Current.Commit;
            LauncherUpdateCheckResult result = await _updateService
                .CheckAsync(
                    channel,
                    PclMetadata.Current.DisplayVersion,
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
                        "检查更新失败",
                        result.ErrorMessage ?? "未知错误",
                        "知道了"));
                if (this.FindControl<TextBlock>("TextCurrentDesc") is { } failedDesc)
                    failedDesc.Text = "检查更新失败，可前往 GitHub Releases 手动查看";
                return;
            }

            _latestReleaseUrl = result.ReleaseUrl ?? ReleasesUrl;
            _preferredAssetUrl = result.PreferredAssetUrl;

            if (result.IsUpdateAvailable)
            {
                if (this.FindControl<MyCard>("CardUpdate") is { } updateCard)
                    updateCard.IsVisible = true;
                if (this.FindControl<MyCard>("CardCheck") is { } checkCard)
                    checkCard.IsVisible = true;
                if (this.FindControl<TextBlock>("TextUpdateName") is { } updateName)
                    updateName.Text = "PCL N " + (result.LatestVersion ?? "");
                if (this.FindControl<TextBlock>("TextUpdateDesc") is { } updateDesc)
                {
                    updateDesc.Text = result.Channel is UpdateChannel.CI
                        ? (result.ReleaseName ?? "CI 滚动构建") + " · 仅全量包"
                        : result.ReleaseName ?? "发现新版本";
                }
                if (this.FindControl<TextBlock>("TextChangelog") is { } changelog)
                {
                    string notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                        ? "前往发布页查看完整更新说明。"
                        : Truncate(result.ReleaseNotes, 1200);
                    if (result.Channel is UpdateChannel.CI || !result.SupportsPatches)
                        notes = "【CI 通道：不使用 Patch，仅全量下载】\n\n" + notes;
                    changelog.Text = notes;
                }
                if (this.FindControl<TextBlock>("TextCurrentDesc") is { } currentDesc)
                    currentDesc.Text = $"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）";
                if (this.FindControl<MyButton>("BtnUpdate") is { } updateButton)
                    updateButton.Text = "打开下载";
            }
            else
            {
                if (this.FindControl<MyCard>("CardUpdate") is { } updateCard)
                    updateCard.IsVisible = false;
                if (this.FindControl<TextBlock>("TextCurrentDesc") is { } currentDesc)
                    currentDesc.Text = $"已是最新版本（{result.CurrentVersion}）";
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away or a newer check started.
        }
        catch (ObjectDisposedException)
        {
            // Recreate service if a previous path disposed the client; retry once next click.
            _updateService = new LauncherUpdateService();
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs(
                    "检查更新失败",
                    "更新服务已重置，请再点一次「重新检查」。",
                    "知道了"));
            if (this.FindControl<TextBlock>("TextCurrentDesc") is { } disposedDesc)
                disposedDesc.Text = "检查更新失败，请再试一次";
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || !this.IsAttachedToVisualTree())
                return;
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs("检查更新失败", ex.Message, "知道了"));
            if (this.FindControl<TextBlock>("TextCurrentDesc") is { } errorDesc)
                errorDesc.Text = "检查更新失败，可前往 GitHub Releases 手动查看";
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
        if (this.FindControl<TextBlock>("TextUpdateName") is { } updateName)
            updateName.Text = version;
        if (this.FindControl<TextBlock>("TextCurrentDesc") is { } currentDescription)
            currentDescription.Text = "当前版本 · 点击「重新检查」查询 GitHub 发布";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private MyComboBox UpdateChannelCombo => this.FindControl<MyComboBox>("ComboSystemUpdateChannel")
        ?? throw new InvalidOperationException("PageSetupUpdate 缺少 ComboSystemUpdateChannel。");
}
