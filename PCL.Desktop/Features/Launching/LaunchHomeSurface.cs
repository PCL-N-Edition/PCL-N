// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using PCL.Application.Settings;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Hosting;
using PCL.Desktop.Shell;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Owns classic / experimental launch home pages and builds <see cref="DesktopMainPage"/>.
/// Singleton for DI, but page instances are scoped to a host window token so headless
/// tests (and multi-window) do not re-parent controls across MainWindow lifetimes.
/// </summary>
public sealed class LaunchHomeSurface
{
    private readonly LaunchHomeProfileResolver _profileResolver;
    private object? _hostToken;
    private LaunchHomeBindings? _bindings;
    private ILaunchHomeSurface? _home;
    private PageLaunchLeft? _classicLeft;
    private PageLaunchRight? _classicRight;
    private PageLaunchHomeExperimental? _experimentalHome;
    private bool _useExperimental;

    public LaunchHomeSurface(LaunchHomeProfileResolver profileResolver)
    {
        _profileResolver = profileResolver;
    }

    public ILaunchHomeSurface? Home => _home;

    public PageLaunchRight? ClassicRight => _classicRight;

    public PageLaunchHomeExperimental? ExperimentalHome => _experimentalHome;

    public bool UseExperimental => _useExperimental;

    /// <summary>
    /// Bind host callbacks. When <paramref name="hostToken"/> changes (new MainWindow),
    /// cached pages are dropped so they are not re-parented across windows.
    /// </summary>
    public void WireOnce(object hostToken, LaunchHomeBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(hostToken);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(_hostToken, hostToken))
        {
            _hostToken = hostToken;
            ClearPages();
        }

        _bindings = bindings;
    }

    public DesktopMainPage CreateMainPage(LauncherSettings launchSettings, bool? experimentalOverride = null)
    {
        if (_bindings is null)
            throw new InvalidOperationException("LaunchHomeSurface 尚未 WireOnce。");

        LaunchHomeBindings b = _bindings;
        bool experimental = experimentalOverride ?? _profileResolver.UseExperimentalFullPageHome();
        if (experimental)
        {
            EnsureExperimentalHome(launchSettings, b);
            _useExperimental = true;
            PageLaunchHomeExperimental experimentalHome = _experimentalHome!;
            return new DesktopMainPage(
                null,
                experimentalHome,
                Activated: () =>
                {
                    _home!.RefreshButtonsUI();
                    _ = _home.EnsureInstancesLoadedAsync();
                    // PageOnEnter respects ContentStay — do not re-zero the whole home (empty flash).
                    experimentalHome.PageOnEnter();
                    experimentalHome.RefreshShortcutDock();
                });
        }

        if (_useExperimental)
        {
            ClearPages();
            _useExperimental = false;
        }

        _classicLeft ??= CreateClassicLeft(b);
        if (_classicRight is null)
        {
            _classicRight = new PageLaunchRight();
            _classicRight.CommunityHintHideRequested += (_, _) => b.HideCommunityHint();
        }

        _classicRight.SetMaximumLogLines(b.ResolveMaximumLogLines());
        b.ApplyLaunchPageSettings();
        b.ApplyHomepageSettings();
        _home = _classicLeft;
        PageLaunchRight right = _classicRight;
        return new DesktopMainPage(
            (Control)_classicLeft,
            right,
            Activated: () =>
            {
                _ = _home!.EnsureInstancesLoadedAsync();
                _home.TriggerEnterAnimation();
                right.PageOnEnter();
            });
    }

    public void EnsureHomeForLogin()
    {
        if (_bindings is null)
            return;
        if (_home is not null)
            return;

        if (_profileResolver.UseExperimentalFullPageHome())
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            EnsureExperimentalHome(settings, _bindings);
        }
        else
        {
            _classicLeft ??= CreateClassicLeft(_bindings);
            _home = _classicLeft;
        }
    }

    private void ClearPages()
    {
        _classicLeft = null;
        _classicRight = null;
        _experimentalHome = null;
        _home = null;
        _useExperimental = false;
    }

    private void EnsureExperimentalHome(LauncherSettings launchSettings, LaunchHomeBindings b)
    {
        b.EnsureFoldersLoaded();

        if (_experimentalHome is not null && _home is PageLaunchHomeExperimental)
        {
            _experimentalHome.SetMaximumLogLines(b.ResolveMaximumLogLines());
            _experimentalHome.SetMinecraftRootDirectory(b.SelectedMinecraftRoot());
            _experimentalHome.SetPreferredInstanceDirectory(b.PreferredInstanceDirectory());
            b.ApplyLaunchPageSettings();
            return;
        }

        PageLaunchHomeExperimental page = new();
        WireHome(page, b);
        page.CommunityHintHideRequested += (_, _) => b.HideCommunityHint();
        page.SetMaximumLogLines(b.ResolveMaximumLogLines());
        page.SetPreferredInstanceDirectory(b.PreferredInstanceDirectory());
        page.SetMinecraftRootDirectory(b.SelectedMinecraftRoot());
        page.ConfigureLaunchingHint(b.ShowLaunchingHint());
        _experimentalHome = page;
        _home = page;
        _classicRight = null;
        b.ApplyLaunchPageSettings();
    }

    private static PageLaunchLeft CreateClassicLeft(LaunchHomeBindings b)
    {
        b.EnsureFoldersLoaded();
        PageLaunchLeft page = new();
        page.SetPreferredInstanceDirectory(b.PreferredInstanceDirectory());
        page.SetMinecraftRootDirectory(b.SelectedMinecraftRoot());
        WireHome(page, b);
        return page;
    }

    private static void WireHome(ILaunchHomeSurface page, LaunchHomeBindings b)
    {
        page.DownloadRequested += (_, _) => b.NavigateDownload();
        page.InstanceSelectRequested += (_, _) => b.NavigateInstanceSelect();
        page.InstanceSettingsRequested += (_, _) =>
        {
            if (page.SelectedInstance is not null)
                b.ManageInstance(page.SelectedInstance);
        };
        page.CancelLaunchRequested += (_, _) => b.CancelLaunch();
        page.StatusMessage += (_, message) => b.StatusMessage(message);
        page.LoginPageRequested += (_, type) => b.OpenLoginPage(page, type);
        page.LaunchRequested += (_, instance) =>
            _ = b.StartMinecraft(new StartMinecraftRequest(page, instance));
        if (page is PageLaunchHomeExperimental experimental)
            experimental.ShortcutActivated += (_, pin) => _ = b.ActivateShortcut(pin);
    }
}
