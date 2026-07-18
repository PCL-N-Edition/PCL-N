// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Core.Platform;

/// <summary>
/// Centralizes platform-dependent product capabilities without referencing UI frameworks.
/// </summary>
public static class PlatformFeaturePolicy
{
    /// <summary>
    /// Gets whether following the system accent color is available on the current platform.
    /// </summary>
    public static bool IsSystemAccentThemeSupported =>
        IsSystemAccentThemeSupportedOn(RuntimePlatformInfo.Current);

    /// <summary>
    /// Determines whether following the system accent color is available on a platform.
    /// Unknown platforms fail closed until their behavior is explicitly designed and tested.
    /// </summary>
    public static bool IsSystemAccentThemeSupportedOn(RuntimePlatform platform) =>
        platform is RuntimePlatform.Linux or RuntimePlatform.MacOS;

    /// <summary>
    /// Gets whether users may create an arbitrary launcher color palette.
    /// </summary>
    public static bool IsCustomColorPaletteSupported =>
        IsCustomColorPaletteSupportedOn(RuntimePlatformInfo.Current);

    /// <summary>
    /// Determines whether an arbitrary launcher color palette is available on a platform.
    /// Windows and unknown platforms fail closed for product-policy compliance.
    /// </summary>
    public static bool IsCustomColorPaletteSupportedOn(RuntimePlatform platform) =>
        platform is RuntimePlatform.Linux or RuntimePlatform.MacOS;
}
