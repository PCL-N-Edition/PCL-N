// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Hosting;
using PCL.Desktop.Shell;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Launch feature module. Main page construction still runs in MainWindow (heavy host wiring);
/// this module owns profile lookup + DI registration for Phase 3 discovery.
/// </summary>
internal sealed class LaunchFeatureModule : IDesktopFeatureModule
{
    public string Id => "launch";

    public IReadOnlyList<NavigationRouteId> Routes { get; } =
    [
        DesktopNavigationRegistry.LaunchRoute
    ];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<LaunchHomeProfileResolver>();
        services.AddSingleton<LaunchHomeSurface>();
        services.AddSingleton<StartMinecraftUseCase>();
    }

    public DesktopMainPage CreateMainPage(IServiceProvider services) =>
        throw new NotSupportedException(
            "Launch main page requires host bindings; use LaunchHomeSurface via MainWindow.");

    public bool TryCreateSubPage(string subPageId, object? argument, IServiceProvider services, out Control? page)
    {
        page = null;
        return false;
    }
}

/// <summary>Resolves experimental vs classic launch layout from settings profile.</summary>
public sealed class LaunchHomeProfileResolver
{
    private readonly ExperimentalUiProfileSource _profileSource;

    public LaunchHomeProfileResolver(ExperimentalUiProfileSource profileSource)
    {
        _profileSource = profileSource;
    }

    public bool UseExperimentalFullPageHome()
    {
        ExperimentalUiProfile profile = _profileSource.RefreshFromSettings();
        return profile.LaunchHome == LaunchHomeLayout.FullPage;
    }
}
