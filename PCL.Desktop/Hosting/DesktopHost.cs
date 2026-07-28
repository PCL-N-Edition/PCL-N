// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting;
using PCL.Core.Logging;
using PCL.UI.Abstractions.Navigation;
using PCL.UI.Abstractions.Pages;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Desktop host composition for the launcher shell.
/// Plugin platform runs out-of-process via <c>PluginSidecarSupervisor</c> (AOT-safe host).
/// </summary>
internal static partial class DesktopHost
{
    private static IPclHost? _current;

    public static IPclHost Current
    {
        get
        {
            Initialize();
            return _current ?? throw new InvalidOperationException("Desktop Host 尚未初始化。");
        }
    }

    public static void Initialize()
    {
        if (_current is not null)
            return;

        PortableLog.Info("DesktopHost", "开始初始化桌面宿主。");
        PclHostBuilder builder = new();
        DesktopNavigationRegistry.RegisterGeneratedHostModules(builder);
        RegisterOptionalModules(builder);
        _current = builder.Build();
        PortableLog.Info(
            "DesktopHost",
            $"桌面宿主构建完成；模块数={_current.ModuleIds.Count}；设置页数={_current.SettingsPages.Pages.Count}。");
        DesktopHostNavigation.Instance.Initialize(_current.Navigation);
        InitializeOptionalRuntime(_current);
    }

    /// <summary>Defining declaration; implementation supplied by overlay rewrite of DesktopHost.Optional.cs.</summary>
    static partial void RegisterOptionalModules(PclHostBuilder builder);

    /// <summary>Defining declaration; implementation supplied by overlay rewrite of DesktopHost.Optional.cs.</summary>
    static partial void InitializeOptionalRuntime(IPclHost host);
}

internal static class DesktopNavigationModule
{
    public static void AddPage(
        INavigationRegistry navigation,
        NavigationRouteId route,
        string title,
        string icon,
        int order,
        Func<DesktopPageContext, DesktopMainPage> pageFactory)
    {
        ArgumentNullException.ThrowIfNull(pageFactory);
        navigation.AddPage(new NavigationPageDescriptor
        {
            Route = route,
            Title = title,
            Icon = icon,
            Order = order,
            Provider = new DelegatePageProvider((context, _) =>
            {
                if (context.Parameter is not DesktopPageContext desktopContext)
                {
                    throw new InvalidOperationException(
                        $"Desktop 页面 '{context.Route}' 需要 {nameof(DesktopPageContext)} 运行时上下文。");
                }

                return new ValueTask<object>(pageFactory(desktopContext));
            })
        });
    }
}
