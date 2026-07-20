// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.Desktop.Controls.Legacy;

namespace PCL.Desktop.Platform;

/// <summary>
/// Applies macOS-specific caption chrome to the borderless main window:
/// traffic lights on the left, content inset, and system-like window operations.
/// </summary>
internal static class MacOsWindowChrome
{
    /// <summary>Horizontal space reserved for traffic lights + padding.</summary>
    public const double TrafficLightInset = 78d;

    public static bool IsActivePlatform => OperatingSystem.IsMacOS();

    public static MacOsTrafficLights? Apply(Window window)
    {
        if (!IsActivePlatform)
            return null;

        // Keep a borderless client surface but host traffic lights ourselves so
        // the existing frosted title bar + custom shadow still work.
        window.WindowDecorations = WindowDecorations.None;
        window.ExtendClientAreaToDecorationsHint = false;

        HideWindowsCaptionButtons(window);
        MacOsTrafficLights lights = EnsureTrafficLights(window);
        ApplyTitleContentInset(window, TrafficLightInset);
        return lights;
    }

    public static void WireWindowEvents(Window window, MacOsTrafficLights lights)
    {
        lights.CloseRequested += (_, _) => window.Close();
        lights.MinimizeRequested += (_, _) => window.WindowState = WindowState.Minimized;
        lights.ZoomRequested += (_, _) =>
        {
            if (!window.CanResize)
                return;
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        };
        lights.FullScreenRequested += (_, _) =>
        {
            window.WindowState = window.WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
        };

        window.Activated += (_, _) => lights.SetWindowActive(true);
        window.Deactivated += (_, _) => lights.SetWindowActive(false);
        lights.SetWindowActive(window.IsActive);
    }

    private static void HideWindowsCaptionButtons(Window window)
    {
        foreach (string name in new[] { "BtnTitleClose", "BtnTitleMax", "BtnTitleMin" })
        {
            if (window.FindControl<Control>(name) is { } button)
                button.IsVisible = false;
        }
    }

    private static MacOsTrafficLights EnsureTrafficLights(Window window)
    {
        if (window.FindControl<MacOsTrafficLights>("PanMacTrafficLights") is { } existing)
            return existing;

        if (window.FindControl<Grid>("PanTitle") is not { } title)
            throw new InvalidOperationException("PanTitle is required for macOS chrome.");

        MacOsTrafficLights lights = new()
        {
            Name = "PanMacTrafficLights",
            ZIndex = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Insert above custom caption buttons so hit-testing wins.
        title.Children.Add(lights);
        return lights;
    }

    private static void ApplyTitleContentInset(Window window, double inset)
    {
        if (window.FindControl<Grid>("PanTitleLeft") is { } titleLeft)
        {
            titleLeft.Margin = new Thickness(inset, 0, 0, 0);
        }

        if (window.FindControl<Grid>("PanTitleInner") is { } titleInner)
        {
            // Back button + label must clear the traffic lights.
            titleInner.Margin = new Thickness(inset - 16d, 0, 0, 0);
            if (window.FindControl<Control>("BtnTitleInner") is { } back)
                back.Margin = new Thickness(12, 0, 0, 0);
            if (window.FindControl<TextBlock>("LabTitleInner") is { } label)
                label.Margin = new Thickness(47, 1, 60, 0);
        }

        // Slightly taller title feel on macOS; keep layout stable.
        if (window.FindControl<Control>("PanTitle") is { } title)
            title.Height = 52;
    }
}
