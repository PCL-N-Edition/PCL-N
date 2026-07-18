// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Core.App;
using PCL.Core.Platform;

namespace PCL.Desktop.Theme;

internal static class ThemeAvailabilityPolicy
{
    private static bool _aprilThemeOverridden;

    internal static Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.Now;

    internal static bool IsAprilFoolsDay(DateTimeOffset value) => value.Month == 4 && value.Day == 1;

    internal static bool IsAprilFoolsDayNow => IsAprilFoolsDay(Clock());

    internal static IReadOnlyList<ColorTheme> GetAvailableThemes()
    {
        List<ColorTheme> themes = [ColorTheme.SkyBlue, ColorTheme.CatBlue, ColorTheme.DeathBlue];
        if (PlatformFeaturePolicy.IsSystemAccentThemeSupported)
            themes.Insert(0, ColorTheme.SystemAccent);
        if (PlatformFeaturePolicy.IsCustomColorPaletteSupported)
            themes.Add(ColorTheme.Custom);
        if (IsAprilFoolsDayNow)
            themes.Add(ColorTheme.HmclBlue);
        return themes;
    }

    internal static ColorTheme ResolveRuntimeTheme(ColorTheme configuredTheme)
    {
        if (configuredTheme == ColorTheme.SystemAccent && !PlatformFeaturePolicy.IsSystemAccentThemeSupported)
            return ColorTheme.CatBlue;
        if (configuredTheme == ColorTheme.Custom && !PlatformFeaturePolicy.IsCustomColorPaletteSupported)
            return ColorTheme.CatBlue;
        if (!IsAprilFoolsDayNow)
            return configuredTheme == ColorTheme.HmclBlue ? ColorTheme.CatBlue : configuredTheme;
        return _aprilThemeOverridden ? configuredTheme : ColorTheme.HmclBlue;
    }

    internal static void MarkManualThemeSelection() => _aprilThemeOverridden = true;

    internal static void ResetSessionForTests()
    {
        _aprilThemeOverridden = false;
        Clock = static () => DateTimeOffset.Now;
    }
}
