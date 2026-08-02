// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Hosting;
using PCL.Desktop.Legal;
using PCL.Desktop.Localization;
using PCL.Desktop.Telemetry;
using PCL.Core.Logging;

#pragma warning disable CA1822, CS0067

namespace PCL.Desktop.Features.Settings.Views;

public partial class PageSetupAbout : MyPageRight, ISettingsPageInteractionSource
{
    private static readonly LauncherSponsorService DefaultSponsorService = new();

    private readonly LauncherSponsorService _sponsorService;
    private int _logoClickCount;
    private bool _sponsorsLoaded;
    private Task? _sponsorLoadTask;

    public PageSetupAbout() : this(DefaultSponsorService, autoLoadSponsors: true)
    {
    }

    internal PageSetupAbout(LauncherSponsorService sponsorService, bool autoLoadSponsors)
    {
        _sponsorService = sponsorService;
        AvaloniaXamlLoader.Load(this);
        PanScroll = PanBack;
        LauncherSettingsPageBinder.Attach(this);
        ApplyMetadata();
        AttachedToVisualTree += (_, _) =>
        {
            ApplyMetadata();
            if (autoLoadSponsors)
                _ = EnsureSponsorsLoadedAsync(forceRefresh: false);
        };
    }

    public event EventHandler<SettingsPathRequestedEventArgs>? OpenPathRequested;

    public event EventHandler<SettingsUrlRequestedEventArgs>? OpenUrlRequested;

    public event EventHandler<SettingsMessageRequestedEventArgs>? MessageRequested;

    public event EventHandler<SettingsConfirmRequestedEventArgs>? ConfirmRequested;

    private void ImgPCLCommunity_Click(object? sender, PointerPressedEventArgs e)
    {
        MessageRequested?.Invoke(this, new SettingsMessageRequestedEventArgs("PCL N Edition", "这是由社区维护的 PCL N Edition。"));
    }

    private void ImgPCLLogo_Click(object? sender, PointerPressedEventArgs e)
    {
        _logoClickCount++;
        if (_logoClickCount == 5)
            MessageRequested?.Invoke(this, new SettingsMessageRequestedEventArgs("还挺执着", "你发现了一个还在迁移中的小彩蛋。"));
    }

    private void BtnSponsorOriginal_Click(object? sender, EventArgs e)
    {
        MessageRequested?.Invoke(
            this,
            new SettingsMessageRequestedEventArgs(
                "赞助说明",
                "PCL N 保留对上游作者与社区贡献者的致谢。具体赞助入口请以对应上游项目页面为准。"));
    }

    private void BtnCommunityHome_Click(object? sender, EventArgs e)
    {
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(PclMetadata.Current.Sponsor));
    }

    private async void BtnSponsorsRefresh_Click(object? sender, EventArgs e) =>
        await EnsureSponsorsLoadedAsync(forceRefresh: true).ConfigureAwait(true);

    private void BtnSourceCode_Click(object? sender, EventArgs e)
    {
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(PclMetadata.Current.Repository));
    }

    private void BtnSponsorMirror_Click(object? sender, EventArgs e)
    {
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs("https://bmclapidoc.bangbang93.com/"));
    }

    private void BtnMcmod_Click(object? sender, EventArgs e)
    {
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs("https://www.mcmod.cn/"));
    }

    private void BtnUpstreamLicense_Click(object? sender, EventArgs e)
    {
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs("https://github.com/Meloong-Git/PCL/blob/main/LICENCE"));
    }

    private void BtnUpstreamSource_Click(object? sender, EventArgs e)
    {
        OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs("https://github.com/PCL-Community/PCL-CE"));
    }

    private void BtnTerms_Click(object? sender, EventArgs e) =>
        MessageRequested?.Invoke(
            this,
            new SettingsMessageRequestedEventArgs(
                Text("Setup.About.Telemetry.Terms", "用户服务协议"),
                EmbeddedLegalDocuments.LoadTermsMarkdown(),
                Text("Common.Action.Close", "关闭")));

    private void BtnPrivacy_Click(object? sender, EventArgs e) =>
        MessageRequested?.Invoke(
            this,
            new SettingsMessageRequestedEventArgs(
                Text("Setup.About.Telemetry.Privacy", "隐私保护协议"),
                EmbeddedLegalDocuments.LoadPrivacyMarkdown(),
                Text("Common.Action.Close", "关闭")));

    private void BtnTelemetryClear_Click(object? sender, EventArgs e)
    {
        LauncherTelemetry.ClearPendingExperienceData();
        MessageRequested?.Invoke(
            this,
            new SettingsMessageRequestedEventArgs(
                Text("Setup.About.Telemetry.Cleared.Title", "已清除"),
                Text("Setup.About.Telemetry.Cleared.Message", "待上传的体验计划数据已清除。")));
    }

    private void BtnTelemetryResetId_Click(object? sender, EventArgs e)
    {
        LauncherTelemetry.ResetAnonymousId();
        MessageRequested?.Invoke(
            this,
            new SettingsMessageRequestedEventArgs(
                Text("Setup.About.Telemetry.Reset.Title", "匿名标识已重置"),
                Text("Setup.About.Telemetry.Reset.Message", "旧匿名标识已删除；若体验计划仍开启，已生成不关联旧记录的新标识。")));
    }

    private static string Text(string key, string fallback) =>
        AvaloniaLocalizationManager.GetText(key, fallback);

    internal Task RefreshSponsorsAsync() => EnsureSponsorsLoadedAsync(forceRefresh: true);

    private Task EnsureSponsorsLoadedAsync(bool forceRefresh)
    {
        if (!forceRefresh && _sponsorsLoaded)
            return Task.CompletedTask;
        return _sponsorLoadTask ??= LoadSponsorsCoreAsync();
    }

    private async Task LoadSponsorsCoreAsync()
    {
        MyLoading? loading = this.FindControl<MyLoading>("LoadSponsors");
        StackPanel? resultPanel = this.FindControl<StackPanel>("PanSponsorsResult");
        StackPanel? errorPanel = this.FindControl<StackPanel>("PanSponsorsError");
        if (loading is not null)
        {
            loading.IsVisible = true;
            loading.State.LoadingState = MyLoading.MyLoadingState.Run;
        }
        if (resultPanel is not null)
            resultPanel.IsVisible = false;
        if (errorPanel is not null)
            errorPanel.IsVisible = false;

        try
        {
            LauncherSponsorSnapshot snapshot = await _sponsorService.FetchAsync().ConfigureAwait(true);
            _sponsorsLoaded = true;
            if (this.FindControl<ItemsControl>("SponsorList") is { } list)
                list.ItemsSource = snapshot.Sponsors;
            if (this.FindControl<TextBlock>("LabSponsorsEmpty") is { } empty)
                empty.IsVisible = snapshot.Sponsors.Count == 0;
            if (this.FindControl<TextBlock>("LabSponsorsSummary") is { } summary)
            {
                string template = Text(
                    "Setup.About.Sponsors.Summary",
                    "感谢来自爱发电的 {0} 位赞助者，名单实时更新。");
                summary.Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    template,
                    snapshot.TotalCount);
                if (snapshot.IsStale)
                {
                    summary.Text += " " + Text(
                        "Setup.About.Sponsors.Stale",
                        "当前显示最近一次成功获取的名单。");
                }
            }
            if (resultPanel is not null)
                resultPanel.IsVisible = true;
        }
        catch (Exception ex)
        {
            _sponsorsLoaded = false;
            PortableLog.Warn(ex, "Sponsors", "无法从在线服务加载爱发电赞助者名单。");
            if (errorPanel is not null)
                errorPanel.IsVisible = true;
        }
        finally
        {
            if (loading is not null)
            {
                loading.State.LoadingState = MyLoading.MyLoadingState.Stop;
                loading.IsVisible = false;
            }
            _sponsorLoadTask = null;
        }
    }

    private void BtnMetadataUrl_Click(object? sender, EventArgs e)
    {
        if (sender is MyButton { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out _))
            OpenUrlRequested?.Invoke(this, new SettingsUrlRequestedEventArgs(url));
    }

    private void ApplyMetadata()
    {
        PclMetadata metadata = PclMetadata.Current;
        if (this.FindControl<MyListItem>("ItemAboutPcl") is { } about)
        {
            string commit = string.IsNullOrWhiteSpace(PclBuildInfo.SourceRevisionId)
                ? metadata.Commit
                : PclBuildInfo.SourceRevisionId;
            if (commit.Length > 8)
                commit = commit[..8];
            string template = about.Info;
            about.Title = metadata.Name.Replace("Plain Craft Launcher ", "PCL ", StringComparison.Ordinal);
            about.Info = template
                .Replace("%VERSION%", metadata.DisplayVersion, StringComparison.Ordinal)
                .Replace("%VERSIONCODE%", metadata.Version.Code.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("%BRANCH%", metadata.Branch, StringComparison.Ordinal)
                .Replace("%COMMIT_HASH%", commit, StringComparison.Ordinal);
        }

        if (this.FindControl<ItemsControl>("LicenseList") is { } licenses)
            licenses.ItemsSource = metadata.Licenses;
    }
}
