// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Hosting;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Features;

/// <summary>
/// Desktop feature module contract (architecture Phase 0). Each domain (Launch, Instances, …)
/// registers services and creates main/sub pages without growing <c>MainWindow</c>.
/// Internal until <see cref="DesktopMainPage"/> is promoted or replaced by a public contract.
/// </summary>
internal interface IDesktopFeatureModule
{
    string Id { get; }

    IReadOnlyList<NavigationRouteId> Routes { get; }

    void Register(IServiceCollection services);

    DesktopMainPage CreateMainPage(IServiceProvider services);

    bool TryCreateSubPage(string subPageId, object? argument, IServiceProvider services, out Control? page);
}
