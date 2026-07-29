// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using PCL.Desktop.Diagnostics;

namespace PCL.Desktop.Platform;

/// <summary>
/// Applies macOS-specific caption chrome to the borderless main window:
/// native traffic lights on the left and content insets for the extended title area.
/// </summary>
internal static class MacOsWindowChrome
{
    /// <summary>Horizontal space reserved for the native traffic lights and their hit targets.</summary>
    public const double TrafficLightInset = 88d;

    /// <summary>Back navigation starts after the native traffic-light hit region.</summary>
    public const double BackButtonInset = 92d;

    public const double TitleBarHeight = 52d;

    public static bool IsActivePlatform => OperatingSystem.IsMacOS();

    public static void Apply(Window window)
    {
        if (!IsActivePlatform)
            return;

        // Let AppKit own the traffic lights. Custom replicas cannot reproduce
        // native minimize/zoom/full-screen behavior and their hit regions vary
        // with scale. Extending the client area preserves PCL's colored title
        // material underneath the system controls.
        window.WindowDecorations = WindowDecorations.Full;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = TitleBarHeight;

        HideWindowsCaptionButtons(window);
        ApplyTitleContentInset(window, TrafficLightInset);
        window.Opened += (_, _) => DesktopFileLog.Info(
            "Window",
            $"macOS 原生窗口装饰已启用；Extended={window.IsExtendedIntoWindowDecorations}；" +
            $"Transparency={window.ActualTransparencyLevel}。");
    }

    private static void HideWindowsCaptionButtons(Window window)
    {
        foreach (string name in new[] { "BtnTitleClose", "BtnTitleMax", "BtnTitleMin" })
        {
            if (window.FindControl<Control>(name) is { } button)
                button.IsVisible = false;
        }
    }

    private static void ApplyTitleContentInset(Window window, double inset)
    {
        if (window.FindControl<Grid>("PanTitleLeft") is { } titleLeft)
        {
            titleLeft.Margin = new Thickness(inset, 0, 0, 0);
        }

        if (window.FindControl<Grid>("PanTitleInner") is { } titleInner)
        {
            // Native traffic lights have a larger hit region than their 12 px
            // discs. Keep the back affordance wholly outside that region.
            titleInner.Margin = new Thickness(0);
            if (window.FindControl<Control>("BtnTitleInner") is { } back)
                back.Margin = new Thickness(BackButtonInset, 0, 0, 0);
            if (window.FindControl<TextBlock>("LabTitleInner") is { } label)
                label.Margin = new Thickness(BackButtonInset + 35d, 1, 60, 0);
        }

        // Match the native macOS title-bar vertical rhythm.
        if (window.FindControl<Control>("PanTitle") is { } title)
            title.Height = TitleBarHeight;
    }
}
