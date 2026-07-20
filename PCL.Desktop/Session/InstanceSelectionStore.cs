// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Session;

/// <summary>Preferred / last-selected game instance directory.</summary>
public sealed class InstanceSelectionStore
{
    private string? _preferredInstanceDirectory;

    public string? PreferredInstanceDirectory => _preferredInstanceDirectory;

    public string? LoadPreferred()
    {
        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            string directory = settings.GetTextOption(LauncherSettingKeys.LaunchSelectedInstanceDirectory);
            _preferredInstanceDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _preferredInstanceDirectory = null;
        }

        return _preferredInstanceDirectory;
    }

    public void PersistPreferred(string? instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            return;

        _preferredInstanceDirectory = instanceDirectory;
        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            settings.SetTextOption(LauncherSettingKeys.LaunchSelectedInstanceDirectory, instanceDirectory);
            LauncherSettingsPageBinder.SaveSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("InstanceSelectionStore.Persist failed: " + ex.Message);
        }
    }
}
