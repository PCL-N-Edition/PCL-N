// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Hosting;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Features.Instances;

/// <summary>
/// Instances feature: version select is a sub-page (not a primary nav route).
/// </summary>
internal sealed class InstancesFeatureModule : IDesktopFeatureModule
{
    public string Id => "instances";

    public IReadOnlyList<NavigationRouteId> Routes { get; } = [];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<InstancesSelectSurface>();
    }

    public DesktopMainPage CreateMainPage(IServiceProvider services) =>
        throw new NotSupportedException("Instances feature is exposed as sub-pages only.");

    public bool TryCreateSubPage(string subPageId, object? argument, IServiceProvider services, out Control? page)
    {
        page = null;
        if (!string.Equals(subPageId, InstancesSelectSurface.SubPageId, StringComparison.Ordinal))
            return false;

        // Host mounts the dual-pane layout; surface alone is not a single Control.
        // Callers use InstancesSelectSurface.Apply instead.
        return false;
    }
}
