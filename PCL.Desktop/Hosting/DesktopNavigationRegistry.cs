// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Hosting;

internal static partial class DesktopNavigationRegistry
{
    public const string LaunchRouteValue = "pcl.launch";
    public const string DownloadRouteValue = "pcl.download";
    public const string CommunityRouteValue = "pcl.community";
    public const string LinkRouteValue = "pcl.link";
    public const string SettingsRouteValue = "pcl.settings";

    public static NavigationRouteId LaunchRoute => new(LaunchRouteValue);
    public static NavigationRouteId DownloadRoute => new(DownloadRouteValue);
    public static NavigationRouteId CommunityRoute => new(CommunityRouteValue);
    public static NavigationRouteId LinkRoute => new(LinkRouteValue);
    public static NavigationRouteId SettingsRoute => new(SettingsRouteValue);

    public static partial void RegisterGeneratedHostModules(PclHostBuilder builder);

    [DesktopNavigationPage("pcl.builtin.launch", LaunchRouteValue, "启动", "lucide/play", 0)]
    private static DesktopMainPage CreateLaunchPage(DesktopPageContext context) =>
        context.CreateLaunchPage();

    [DesktopNavigationPage("pcl.builtin.download", DownloadRouteValue, "安装", "lucide/package-plus", 10)]
    private static DesktopMainPage CreateDownloadPage(DesktopPageContext context) =>
        context.CreateDownloadPage();

    [DesktopNavigationPage("pcl.builtin.community", CommunityRouteValue, "资源", "lucide/blocks", 20)]
    private static DesktopMainPage CreateCommunityPage(DesktopPageContext context) =>
        context.CreateCommunityPage();

    [DesktopNavigationPage("pcl.builtin.link", LinkRouteValue, "联机", "lucide/network", 30)]
    private static DesktopMainPage CreateLinkPage(DesktopPageContext context) =>
        context.CreateLinkPage();

    [DesktopNavigationPage("pcl.builtin.settings", SettingsRouteValue, "设置", "lucide/settings", 40)]
    private static DesktopMainPage CreateSettingsPage(DesktopPageContext context) =>
        context.CreateSettingsPage();
}
