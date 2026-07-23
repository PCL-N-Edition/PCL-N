// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using Avalonia.Threading;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Localization;
using PCL.Online;

namespace PCL.Desktop.Views;

#pragma warning disable CA1863

public partial class MainWindow
{
    private void OpenMicrosoftLoginForOnline()
    {
        SelectNavRoute(LaunchRoute, animate: true);
        _launchLeft ??= CreateLaunchLeftPage();
        _launchLeft.RefreshPage(anim: true, PageLaunchLeft.LaunchLoginPageType.Ms);
        ShowHint(AvaloniaLocalizationManager.GetText(
            "Online.Account.LoginRedirected",
            "请完成 Microsoft 登录；成功后会自动连接 N Cloud。"));
    }

    private void OnCloudSyncNotice(CloudSyncService.NoticeType type, int retry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (type)
            {
                case CloudSyncService.NoticeType.Starting:
                    ShowHint(AvaloniaLocalizationManager.GetText(
                        "Online.CloudSync.Starting",
                        "正在同步 N Cloud 数据……"));
                    break;
                case CloudSyncService.NoticeType.Retry:
                    ShowHint(string.Format(
                        CultureInfo.CurrentCulture,
                        AvaloniaLocalizationManager.GetText(
                            "Online.CloudSync.Retrying",
                            "N Cloud 同步暂时失败，正在进行第 {0} 次重试……"),
                        retry));
                    break;
                case CloudSyncService.NoticeType.Success:
                    ShowHint(AvaloniaLocalizationManager.GetText(
                        "Online.CloudSync.Success",
                        "N Cloud 同步完成。"));
                    break;
                case CloudSyncService.NoticeType.Failed:
                    ShowHint(AvaloniaLocalizationManager.GetText(
                        "Online.CloudSync.Failed",
                        "N Cloud 同步失败，可在“设置 → 在线”中重试。"), critical: true);
                    break;
            }
        });
    }
}
