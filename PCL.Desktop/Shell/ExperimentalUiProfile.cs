// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Messaging;
using CommunityToolkit.Mvvm.Messaging;

namespace PCL.Desktop.Shell;

public enum ChromeStyle
{
    Classic = 0,
    Glass = 1
}

public enum LaunchHomeLayout
{
    Split = 0,
    FullPage = 1
}

public enum InstanceSelectLayout
{
    LeftRight = 0,
    FullPageSidebar = 1
}

public enum InstanceManageLayout
{
    ClassicSplit = 0,
    FullPageSidebar = 1
}

public enum DownloadInstallLayout
{
    ClassicSplit = 0,
    FullPageSidebar = 1
}

/// <summary>
/// Presentation-only profile. Business stores must not branch on View types.
/// </summary>
public sealed record ExperimentalUiProfile(
    bool HomepageUi,
    ChromeStyle Chrome,
    LaunchHomeLayout LaunchHome,
    InstanceSelectLayout Select,
    InstanceManageLayout Manage,
    DownloadInstallLayout Download)
{
    public static ExperimentalUiProfile Classic { get; } = new(
        HomepageUi: false,
        Chrome: ChromeStyle.Classic,
        LaunchHome: LaunchHomeLayout.Split,
        Select: InstanceSelectLayout.LeftRight,
        Manage: InstanceManageLayout.ClassicSplit,
        Download: DownloadInstallLayout.ClassicSplit);

    public static ExperimentalUiProfile FromHomepageFlag(bool homepageUi) =>
        homepageUi
            ? new ExperimentalUiProfile(
                HomepageUi: true,
                Chrome: ChromeStyle.Glass,
                LaunchHome: LaunchHomeLayout.FullPage,
                Select: InstanceSelectLayout.FullPageSidebar,
                Manage: InstanceManageLayout.FullPageSidebar,
                Download: DownloadInstallLayout.FullPageSidebar)
            : Classic;
}

/// <summary>Reads settings and broadcasts profile changes.</summary>
public sealed class ExperimentalUiProfileSource
{
    private readonly IMessenger _messenger;
    private ExperimentalUiProfile _current = ExperimentalUiProfile.Classic;

    public ExperimentalUiProfileSource(IMessenger messenger)
    {
        _messenger = messenger;
        RefreshFromSettings();
    }

    public ExperimentalUiProfile Current => _current;

    public ExperimentalUiProfile RefreshFromSettings()
    {
        bool homepageUi = false;
        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            homepageUi = settings.GetBooleanOption(
                LauncherSettingKeys.ExperimentalHomepageUi,
                LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalHomepageUi.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            homepageUi = LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalHomepageUi.Value);
        }

        ExperimentalUiProfile next = ExperimentalUiProfile.FromHomepageFlag(homepageUi);
        if (next != _current)
        {
            _current = next;
            _messenger.Send(new ExperimentalProfileChangedMessage(next.HomepageUi));
        }
        else
        {
            _current = next;
        }

        return _current;
    }

    public ExperimentalUiProfile RefreshFromSettings(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool homepageUi = settings.GetBooleanOption(
            LauncherSettingKeys.ExperimentalHomepageUi,
            LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalHomepageUi.Value));
        ExperimentalUiProfile next = ExperimentalUiProfile.FromHomepageFlag(homepageUi);
        bool changed = next != _current;
        _current = next;
        if (changed)
            _messenger.Send(new ExperimentalProfileChangedMessage(next.HomepageUi));
        return _current;
    }
}
