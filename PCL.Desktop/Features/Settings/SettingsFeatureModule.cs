// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Hosting;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Features.Settings;

internal sealed class SettingsFeatureModule : IDesktopFeatureModule
{
    public string Id => "settings";

    public IReadOnlyList<NavigationRouteId> Routes { get; } =
    [
        DesktopNavigationRegistry.SettingsRoute
    ];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<SettingsFeatureSurface>();
    }

    public DesktopMainPage CreateMainPage(IServiceProvider services) =>
        throw new NotSupportedException(
            "Settings main page requires host dialogs; use SettingsFeatureSurface via MainWindow bindings.");

    public bool TryCreateSubPage(string subPageId, object? argument, IServiceProvider services, out Control? page)
    {
        page = null;
        return false;
    }
}

/// <summary>Owns settings left rail; host supplies interaction wiring (host-scoped).</summary>
public sealed class SettingsFeatureSurface
{
    private object? _hostToken;
    private PageSetupLeft? _left;
    private MyPageRight? _right;
    private SettingsFeatureBindings? _bindings;

    public PageSetupLeft? Left => _left;

    public MyPageRight? Right => _right;

    public void WireOnce(object hostToken, SettingsFeatureBindings bindings)
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
        _left ??= CreateLeft(bindings);
    }

    public DesktopMainPage CreateMainPage()
    {
        if (_bindings is null || _left is null)
            throw new InvalidOperationException("SettingsFeatureSurface 尚未 WireOnce。");

        MyPageRight rightPage = _left.GetOrCreateCurrentPage();
        _right = rightPage;
        PageSetupLeft left = _left;
        SettingsFeatureBindings b = _bindings;
        return new DesktopMainPage(
            left,
            rightPage,
            Activated: () =>
            {
                b.EnsureRightHostOpaque();
                left.TriggerShowAnimation();
                if (b.TryGetLiveRightPage() is { } liveRight)
                    liveRight.PageOnEnter();
                else
                    rightPage.PageOnEnter();
            });
    }

    public void SetCurrentRight(MyPageRight page) => _right = page;

    private static PageSetupLeft CreateLeft(SettingsFeatureBindings b)
    {
        PageSetupLeft page = new();
        page.PageCreated += (_, created) => b.WirePage(created);
        page.PageChanged += (_, args) => b.ApplyRightPage(args.Page);
        page.ResetRequested += (_, args) =>
            b.Confirm(
                args.Title,
                args.Message,
                args.Complete,
                args.PrimaryButton,
                args.SecondaryButton,
                args.IsWarn,
                args.PrimaryAction,
                args.SecondaryAction);
        return page;
    }
}

public sealed class SettingsFeatureBindings
{
    public required Action EnsureRightHostOpaque { get; init; }

    public required Func<MyPageRight?> TryGetLiveRightPage { get; init; }

    public required Action<MyPageRight> WirePage { get; init; }

    public required Action<MyPageRight> ApplyRightPage { get; init; }

    public required Action<string, string, Action<bool>, string?, string?, bool, Action?, Action?> Confirm { get; init; }
}
