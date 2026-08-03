// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Downloads.Views;
using PCL.Desktop.Hosting;
using PCL.Desktop.Shell;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Features.Downloads;

internal sealed class DownloadFeatureModule : IDesktopFeatureModule
{
    public string Id => "download";

    public IReadOnlyList<NavigationRouteId> Routes { get; } =
    [
        DesktopNavigationRegistry.DownloadRoute
    ];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<DownloadFeatureSurface>();
    }

    public DesktopMainPage CreateMainPage(IServiceProvider services) =>
        throw new NotSupportedException(
            "Download main page requires host install wiring; use DownloadFeatureSurface via MainWindow.");

    public bool TryCreateSubPage(string subPageId, object? argument, IServiceProvider services, out Control? page)
    {
        page = null;
        return false;
    }
}

/// <summary>Owns download left rail + current right page (host-scoped page cache).</summary>
public sealed class DownloadFeatureSurface
{
    private readonly ExperimentalUiProfileSource _profileSource;
    private object? _hostToken;
    private PageDownloadLeft? _left;
    private Func<PageDownloadInstall>? _installFactory;
    private EventHandler<DownloadPageChangedEventArgs>? _pageChanged;

    public DownloadFeatureSurface(ExperimentalUiProfileSource profileSource)
    {
        _profileSource = profileSource;
    }

    public PageDownloadLeft? Left => _left;

    public void Configure(
        object hostToken,
        Func<PageDownloadInstall> installFactory,
        EventHandler<DownloadPageChangedEventArgs>? pageChanged = null)
    {
        ArgumentNullException.ThrowIfNull(hostToken);
        ArgumentNullException.ThrowIfNull(installFactory);
        if (!ReferenceEquals(_hostToken, hostToken))
        {
            _hostToken = hostToken;
            _left = null;
        }

        _installFactory = installFactory;
        _pageChanged = pageChanged;
    }

    public DesktopMainPage CreateMainPage()
    {
        if (_installFactory is null)
            throw new InvalidOperationException("DownloadFeatureSurface 尚未 Configure。");

        if (_left is null)
        {
            _left = new PageDownloadLeft(_installFactory);
            if (_pageChanged is not null)
                _left.PageChanged += _pageChanged;
        }

        MyPageRight rightPage = _left.GetOrCreateCurrentPage();
        PageDownloadLeft left = _left;
        bool experimental = _profileSource.RefreshFromSettings().Download ==
                            DownloadInstallLayout.FullPageSidebar;
        if (rightPage is PageDownloadInstall layoutPage)
            layoutPage.SetExperimentalLayout(experimental);
        return new DesktopMainPage(
            experimental ? null : left,
            rightPage,
            Activated: () =>
            {
                if (!experimental)
                    left.TriggerShowAnimation();
                if (rightPage is PageDownloadInstall installPage)
                {
                    if (!installPage.HasPendingFocusedNavigation)
                        installPage.ClearInstallTargetOverride();
                    installPage.PageOnEnter();
                }
                else
                {
                    rightPage.PageOnEnter();
                }
            });
    }
}
