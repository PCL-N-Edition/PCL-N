// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Hosting;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Features.Link;

internal sealed class LinkFeatureModule : IDesktopFeatureModule
{
    public string Id => "link";

    public IReadOnlyList<NavigationRouteId> Routes { get; } =
    [
        DesktopNavigationRegistry.LinkRoute
    ];

    public void Register(IServiceCollection services) =>
        services.AddSingleton<LinkFeatureSurface>();

    public DesktopMainPage CreateMainPage(IServiceProvider services) =>
        services.GetRequiredService<LinkFeatureSurface>().CreateMainPage();

    public bool TryCreateSubPage(
        string subPageId,
        object? argument,
        IServiceProvider services,
        out Control? page)
    {
        page = null;
        return false;
    }
}

public sealed class LinkFeatureSurface : IDisposable
{
    private PageGameLink? _page;
    private bool _disposed;

    public DesktopMainPage CreateMainPage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _page ??= new PageGameLink();
        return new DesktopMainPage(
            null,
            _page,
            Activated: () => _page.PageOnEnter());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _page?.Dispose();
        GC.SuppressFinalize(this);
    }
}
