// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

internal static class ControlVisualHelpers
{
    internal static void AnimateListEntrance(Panel panel, string animationKey)
    {
        if (panel.Children.Count == 0)
            return;

        // Defer until attached so entrance is not silently skipped (common "missing animation" cause).
        if (!panel.IsAttachedToVisualTree())
        {
            EventHandler<VisualTreeAttachmentEventArgs>? attached = null;
            attached = (_, _) =>
            {
                panel.AttachedToVisualTree -= attached;
                AnimateListEntrance(panel, animationKey);
            };
            panel.AttachedToVisualTree += attached;
            return;
        }

        if (!ShouldAnimate(panel))
        {
            SnapListEntranceFinal(panel);
            return;
        }

        Control[] children = panel.Children.Take(MotionTokens.ListEnterMaxChildren).ToArray();
        foreach (Control child in children)
        {
            child.Opacity = 0d;
            if (child.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform();
                child.RenderTransform = translate;
            }
            translate.Y = MotionTokens.ListEnterOffsetY;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!panel.IsAttachedToVisualTree() || !panel.IsVisible)
            {
                SnapListEntranceFinal(panel);
                return;
            }

            if (ReduceMotionPreferred() || ModAnimation.AniControlEnabled != 0)
            {
                SnapListEntranceFinal(panel);
                return;
            }

            List<ModAnimation.AniData> animations = [];
            int index = 0;
            foreach (Control child in children.Where(panel.Children.Contains))
            {
                int delay = Math.Min(index * MotionTokens.ListStaggerMs, 200);
                // Critically damped rise + fade (opacity + transform only).
                animations.Add(ModAnimation.AaOpacity(
                    child,
                    1d,
                    MotionTokens.ListEnterOpacityMs,
                    delay,
                    new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
                animations.Add(ModAnimation.AaTranslateY(
                    child,
                    -MotionTokens.ListEnterOffsetY,
                    MotionTokens.ListEnterSlideMs,
                    delay,
                    new ModAnimation.AniEaseOutFluent()));
                index++;
            }

            // Guarantee final presentation even if the named group is interrupted later.
            animations.Add(ModAnimation.AaCode(() => SnapListEntranceFinal(panel), after: true));

            if (animations.Count > 0)
                ModAnimation.AniStart(animations, animationKey);
        }, DispatcherPriority.Loaded);
    }

    private static void SnapListEntranceFinal(Panel panel)
    {
        // Safety net only for stuck invisibles — absolute tweens already end at the target;
        // forcing Y=0 here caused a visible end-frame 拉回 when residual was non-zero.
        foreach (Control child in panel.Children)
        {
            if (child.Opacity < 0.02d)
                child.Opacity = 1d;
        }
    }

    internal static bool ShouldAnimate(Control control, object? animationOverride = null) =>
        control.IsAttachedToVisualTree() &&
        control.IsVisible &&
        ModAnimation.AniControlEnabled == 0 &&
        !ReduceMotionPreferred() &&
        !false.Equals(animationOverride);

    // SPI_GETCLIENTAREAANIMATION is a kernel round-trip; cache briefly (measured in UI samples).
    private static bool? _reduceMotionCached;
    private static long _reduceMotionCachedAtMs;
    private const int ReduceMotionCacheMs = 2000;

    /// <summary>
    /// Prefer reduced motion when the OS asks for it, or when debug animation speed is effectively instant.
    /// </summary>
    internal static bool ReduceMotionPreferred()
    {
        // Debug speed can change at runtime without an OS SPI change — always honor it.
        if (ModAnimation.aniSpeed >= 100d)
            return true;

        long now = Environment.TickCount64;
        if (_reduceMotionCached is bool cached &&
            now - _reduceMotionCachedAtMs >= 0 &&
            now - _reduceMotionCachedAtMs < ReduceMotionCacheMs)
        {
            return cached;
        }

        bool preferred = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // SPI_GETCLIENTAREAANIMATION = 0x1042; false means "turn off animations".
                if (NativeMethods.SystemParametersInfo(0x1042, 0, out bool clientAreaAnimation, 0) &&
                    !clientAreaAnimation)
                {
                    preferred = true;
                }
            }
        }
        catch
        {
            // Ignore platform probe failures.
        }

        _reduceMotionCached = preferred;
        _reduceMotionCachedAtMs = now;
        return preferred;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out bool pvParam, uint fWinIni);
    }

    internal static void SetCenterScale(Control control, double scale) =>
        SetCenterScale(control, scale, scale);

    internal static void SetCenterScale(Control control, double scaleX, double scaleY)
    {
        control.RenderTransformOrigin = new RelativePoint(0.5d, 0.5d, RelativeUnit.Relative);
        if (control.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform();
            control.RenderTransform = transform;
        }

        transform.ScaleX = scaleX;
        transform.ScaleY = scaleY;
    }

    internal static void AnimateColorOrSetResource(
        Control target,
        AvaloniaProperty property,
        string resourceKey,
        int duration,
        string animationKey,
        bool shouldAnimate)
    {
        if (shouldAnimate)
        {
            ModAnimation.AniStart(
                ModAnimation.AaColor(target, property, resourceKey, duration),
                animationKey);
            return;
        }

        ModAnimation.AniStop(animationKey);
        SetResourceBrush(target, property, resourceKey);
    }

    private static void SetResourceBrush(Control target, AvaloniaProperty property, string resourceKey)
    {
        IBrush brush = LegacyResourceResolver.Brush(target, resourceKey, "#00ffffff");

        if (property == Border.BackgroundProperty && target is Border backgroundBorder)
            backgroundBorder.Background = brush;
        else if (property == Border.BorderBrushProperty && target is Border borderBrushBorder)
            borderBrushBorder.BorderBrush = brush;
        else if (property == TextBlock.ForegroundProperty && target is TextBlock textBlock)
            textBlock.Foreground = brush;
        else if (property.Name == nameof(TemplatedControl.Foreground) && target is TemplatedControl templated)
            templated.Foreground = brush;
        else if (property == Shape.FillProperty && target is Shape fillShape)
            fillShape.Fill = brush;
        else if (property == Shape.StrokeProperty && target is Shape strokeShape)
            strokeShape.Stroke = brush;
        else if (property == SvgIcon.IconBrushProperty && target is SvgIcon svgIcon)
            svgIcon.IconBrush = brush;
        else if (property == MyDropShadow.ColorProperty && target is MyDropShadow shadow && brush is SolidColorBrush solid)
            shadow.Color = solid.Color;
    }
}
