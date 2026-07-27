// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>
/// Applies Apple-style experimental form chrome to a control tree.
/// Driven by the experimental UI switch; classic pages pass <c>false</c>.
/// </summary>
public static class ExperimentalControlChrome
{
    public static void Apply(Control? root, bool enabled)
    {
        if (root is null)
            return;

        ApplyTo(root, enabled);
        foreach (Visual visual in root.GetVisualDescendants())
        {
            if (visual is Control control)
                ApplyTo(control, enabled);
        }
    }

    /// <summary>
    /// Apply now and again after layout so lazily-built descendants pick up the style.
    /// </summary>
    public static void ApplyDeferred(Control? root, bool enabled)
    {
        Apply(root, enabled);
        if (root is null)
            return;

        Dispatcher.UIThread.Post(() => Apply(root, enabled), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => Apply(root, enabled), DispatcherPriority.Background);
    }

    private static void ApplyTo(Control control, bool enabled)
    {
        switch (control)
        {
            case MyButton button:
                button.UseExperimentalStyle = enabled;
                if (enabled)
                {
                    // Prefer content-driven height so short form rows stay intact.
                    if (!double.IsNaN(button.Height) && button.Height is >= 34 and <= 40)
                        button.Height = 34;
                    button.CornerRadius = new CornerRadius(10);
                }
                break;
            case MyComboBox combo:
                combo.UseExperimentalStyle = enabled;
                break;
            case MyTextBox textBox:
                // Nested fields keep their host chrome (combo editable slot / search box).
                if (textBox.TemplatedParent is MyComboBox ||
                    textBox.FindAncestorOfType<MySearchBox>() is not null ||
                    textBox.FindAncestorOfType<MyComboBox>() is not null)
                {
                    textBox.UseExperimentalStyle = false;
                    break;
                }

                textBox.UseExperimentalStyle = enabled;
                break;
            case MySearchBox:
                // Search box owns its own card chrome; do not retarget internals here.
                break;
            case MyCheckBox checkBox:
                checkBox.UseExperimentalStyle = enabled;
                break;
            case MyRadioBox radioBox:
                radioBox.UseExperimentalStyle = enabled;
                break;
        }
    }

    /// <summary>Shared iOS-like surface/stroke colors used by experimental form controls.</summary>
    public static class Palette
    {
        public static Color Surface(bool dark, bool hover, bool focused) =>
            dark
                ? Color.Parse(focused ? "#F04A4A50" : hover ? "#E8444450" : "#D83A3A3E")
                : Color.Parse(focused ? "#FFFFFFFF" : hover ? "#FFF7F7FA" : "#EEF2F2F7");

        public static Color Stroke(bool dark, bool focused) =>
            dark
                ? Color.Parse(focused ? "#55FFFFFF" : "#32FFFFFF")
                : Color.Parse(focused ? "#401370F3" : "#18000000");

        public static Color Text(bool dark, bool enabled) =>
            !enabled
                ? (dark ? Color.Parse("#829999A1") : Color.Parse("#7A6E6E73"))
                : (dark ? Color.Parse("#FFF2F2F7") : Color.Parse("#FF1C1C1E"));

        public static Color Accent(IBrush? brush) =>
            brush is SolidColorBrush solid ? solid.Color : Color.Parse("#1370f3");

        public static Color DisabledSurface(bool dark) =>
            dark ? Color.Parse("#663A3A3E") : Color.Parse("#99E5E5EA");

        public static Color DisabledStroke(bool dark) =>
            dark ? Color.Parse("#24FFFFFF") : Color.Parse("#14000000");
    }
}
