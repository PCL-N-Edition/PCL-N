// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting;
using PCL.Application.Settings;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Features.Online;

internal sealed class DesktopOnlineHostModule : IPclHostModule
{
    public HostModuleId Id => new("pcl.online");

    public HostApiVersion MinimumHostApiVersion => new(0, 3);

    public HostApiVersion MaximumHostApiVersionExclusive => new(1, 0);

    public void Configure(IPclHostBuilder builder)
    {
        builder.AddSettingsPageGroup(new HostSettingsPageGroupDescriptor(
            "pcl.settings.online",
            "在线",
            "lucide/cloud",
            -100,
            "Microsoft 账户、N Cloud 同步与在线服务。")
        {
            LocalizedTitle = HostLocalizedText.FromResource("Setup.Online.Group", "在线"),
            LocalizedDescription = HostLocalizedText.FromResource(
                "Setup.Online.Group.Description",
                "Microsoft 账户、N Cloud 同步与在线服务。")
        });
        builder.AddSettingsPage(new HostSettingsPageDescriptor(
            "pcl.online.cloud-sync",
            "账户与同步",
            "lucide/cloud-cog",
            "账户与同步",
            "在设备之间同步启动器设置和社区收藏。",
            [])
        {
            GroupId = "pcl.settings.online",
            Order = 0,
            LocalizedTitle = HostLocalizedText.FromResource("Setup.Online.Title", "账户与同步"),
            LocalizedHeading = HostLocalizedText.FromResource("Setup.Online.Title", "账户与同步"),
            LocalizedDescription = HostLocalizedText.FromResource(
                "Setup.Online.Description",
                "在设备之间同步启动器设置和社区收藏。"),
            PageFactory = static () => new PageSetupOnline()
        });
    }
}
