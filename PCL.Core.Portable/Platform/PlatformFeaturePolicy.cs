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

    /// <summary>
    /// Gets whether launching with the OS-provided GLFW library is available on the current platform.
    /// </summary>
    public static bool IsUseSystemGlfwSupported =>
        IsUseSystemGlfwSupportedOn(RuntimePlatformInfo.Current);

    /// <summary>
    /// Determines whether system GLFW substitution is available on a platform.
    /// Only Linux exposes a reliable system GLFW packaging story; other platforms fail closed.
    /// </summary>
    public static bool IsUseSystemGlfwSupportedOn(RuntimePlatform platform) =>
        platform is RuntimePlatform.Linux;

    /// <summary>
    /// Gets whether forcing X11 under Wayland is available on the current platform.
    /// </summary>
    public static bool IsForceX11OnWaylandSupported =>
        IsForceX11OnWaylandSupportedOn(RuntimePlatformInfo.Current);

    /// <summary>
    /// Determines whether forcing X11 under Wayland is available on a platform.
    /// Wayland/X11 display backends are Linux-only; other platforms fail closed.
    /// </summary>
    public static bool IsForceX11OnWaylandSupportedOn(RuntimePlatform platform) =>
        platform is RuntimePlatform.Linux;
}
