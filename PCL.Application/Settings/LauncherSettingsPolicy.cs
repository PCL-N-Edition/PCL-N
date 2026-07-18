// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Core.App;

namespace PCL.Application.Settings;

public static class LauncherSettingsPolicy
{
    public static LauncherSettings Normalize(
        LauncherSettings settings,
        bool supportsSystemAccentTheme,
        bool allowsDomesticMirror,
        bool supportsCustomColorPalette = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ColorTheme lightColor = NormalizeColor(
            settings.LightColor,
            supportsSystemAccentTheme,
            supportsCustomColorPalette);
        ColorTheme darkColor = NormalizeColor(
            settings.DarkColor,
            supportsSystemAccentTheme,
            supportsCustomColorPalette);
        DownloadSourcePreference downloadSource =
            !allowsDomesticMirror &&
            settings.DownloadSource != DownloadSourcePreference.OfficialOnly
                ? DownloadSourcePreference.OfficialOnly
                : settings.DownloadSource;

        return settings with
        {
            SchemaVersion = LauncherSettings.CurrentSchemaVersion,
            LightColor = lightColor,
            DarkColor = darkColor,
            DownloadSource = downloadSource
        };
    }

    private static ColorTheme NormalizeColor(
        ColorTheme color,
        bool supportsSystemAccentTheme,
        bool supportsCustomColorPalette)
    {
        if (!supportsSystemAccentTheme && color == ColorTheme.SystemAccent)
            return ColorTheme.CatBlue;
        if (!supportsCustomColorPalette && color == ColorTheme.Custom)
            return ColorTheme.CatBlue;
        return color;
    }
}
