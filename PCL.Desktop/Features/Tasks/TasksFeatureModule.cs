// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Features.Tasks.Views;
using PCL.Desktop.Hosting;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Features.Tasks;

/// <summary>
/// Task manager is not a primary nav route; it is a host-owned overlay (title sub-page).
/// Module registers the surface for DI discovery.
/// </summary>
internal sealed class TasksFeatureModule : IDesktopFeatureModule
{
    public string Id => "tasks";

    public IReadOnlyList<NavigationRouteId> Routes { get; } = [];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<TaskManagerSurface>();
    }

    public DesktopMainPage CreateMainPage(IServiceProvider services) =>
        throw new NotSupportedException(
            "Task manager is a host overlay; use TaskManagerSurface via MainWindow.");

    public bool TryCreateSubPage(string subPageId, object? argument, IServiceProvider services, out Control? page)
    {
        page = null;
        return false;
    }
}

/// <summary>Owns task-manager left/right pages (host-scoped).</summary>
public sealed class TaskManagerSurface
{
    private object? _hostToken;
    private TaskManagerBindings? _bindings;
    private PageSpeedLeft? _left;
    private PageSpeedRight? _right;

    public PageSpeedLeft? Left => _left;

    public PageSpeedRight? Right => _right;

    public void WireOnce(object hostToken, TaskManagerBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(hostToken);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(_hostToken, hostToken))
        {
            _hostToken = hostToken;
            _left = null;
            _right = null;
        }

        _bindings = bindings;
    }

    public PageSpeedLeft EnsureLeft()
    {
        _ = RequireBindings();
        return _left ??= new PageSpeedLeft();
    }

    public PageSpeedRight EnsureRight()
    {
        TaskManagerBindings b = RequireBindings();
        if (_right is not null)
            return _right;

        PageSpeedRight page = new();
        page.CancelRequested += (_, args) => b.CancelTask(args.TaskId);
        page.DismissRequested += (_, args) => b.DismissTask(args.TaskId);
        _right = page;
        return page;
    }

    private TaskManagerBindings RequireBindings() =>
        _bindings ?? throw new InvalidOperationException("TaskManagerSurface 尚未 WireOnce。");
}

public sealed class TaskManagerBindings
{
    public required Action<string> CancelTask { get; init; }

    public required Action<string> DismissTask { get; init; }
}
