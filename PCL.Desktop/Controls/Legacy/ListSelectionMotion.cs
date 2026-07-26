// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>
/// Shared, interruptible motion for list multi-selection affordances.
/// Selection is a deliberate state change, so it settles without overshoot.
/// </summary>
internal static class ListSelectionMotion
{
    internal const double IndicatorHeight = 32d;

    internal static void AnimateRow(
        Control owner,
        Border indicator,
        bool selected,
        string animationKey,
        Border? selectionBackground = null)
    {
        double targetHeight = selected ? IndicatorHeight : 0d;
        double targetOpacity = selected ? 1d : 0d;
        if (!ControlVisualHelpers.ShouldAnimate(owner))
        {
            ModAnimation.AniStop(animationKey);
            indicator.Height = targetHeight;
            indicator.Opacity = targetOpacity;
            if (selectionBackground is not null)
                selectionBackground.Opacity = targetOpacity;
            return;
        }

        List<ModAnimation.AniData> animations =
        [
            ModAnimation.AaHeight(
                indicator,
                targetHeight - CurrentHeight(indicator),
                MotionTokens.ListSelectionIndicatorMs,
                ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
            ModAnimation.AaOpacity(
                indicator,
                targetOpacity - indicator.Opacity,
                selected ? 110 : 90,
                ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Strong))
        ];
        if (selectionBackground is not null)
        {
            animations.Add(ModAnimation.AaOpacity(
                selectionBackground,
                targetOpacity - selectionBackground.Opacity,
                selected ? 160 : 120,
                ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Strong)));
        }

        ModAnimation.AniStart(animations, animationKey);
    }

    internal static void AnimateActionBar(
        Control owner,
        Control actionBar,
        Control spacingTarget,
        bool visible,
        double visibleBottomMargin,
        double hiddenBottomMargin,
        string animationKey)
    {
        double targetBottomMargin = visible ? visibleBottomMargin : hiddenBottomMargin;
        TranslateTransform translate = EnsureTranslate(actionBar);

        // An already-hidden bar has no exit transition to show. This also keeps
        // initial page layout from briefly flashing the action bar.
        if (!visible && !actionBar.IsVisible)
        {
            SetActionBarState(
                actionBar,
                spacingTarget,
                translate,
                visible: false,
                targetBottomMargin);
            return;
        }

        if (!ControlVisualHelpers.ShouldAnimate(owner))
        {
            ModAnimation.AniStop(animationKey);
            SetActionBarState(
                actionBar,
                spacingTarget,
                translate,
                visible,
                targetBottomMargin);
            return;
        }

        if (visible && !actionBar.IsVisible)
        {
            actionBar.IsVisible = true;
            actionBar.Opacity = 0d;
            translate.Y = MotionTokens.ListSelectionBarOffsetY;
        }

        int duration = visible
            ? MotionTokens.ListSelectionBarEnterMs
            : MotionTokens.ListSelectionBarExitMs;
        double targetOpacity = visible ? 1d : 0d;
        double targetTranslate = visible ? 0d : MotionTokens.ListSelectionBarOffsetY;
        double marginDelta = targetBottomMargin - spacingTarget.Margin.Bottom;
        ModAnimation.AniEase ease =
            new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong);
        List<ModAnimation.AniData> animations =
        [
            ModAnimation.AaOpacity(
                actionBar,
                targetOpacity - actionBar.Opacity,
                duration,
                ease: ease),
            ModAnimation.AaTranslateY(
                translate,
                targetTranslate - translate.Y,
                duration,
                ease: ease),
            ModAnimation.AaDouble(
                delta => SetBottomMargin(spacingTarget, spacingTarget.Margin.Bottom + delta),
                marginDelta,
                duration,
                ease: ease)
        ];
        if (!visible)
        {
            animations.Add(ModAnimation.AaCode(
                () =>
                {
                    if (actionBar.Opacity <= 0.001d)
                        actionBar.IsVisible = false;
                },
                after: true));
        }

        ModAnimation.AniStart(animations, animationKey);
    }

    private static double CurrentHeight(Control control) =>
        double.IsNaN(control.Height) ? Math.Max(0d, control.Bounds.Height) : control.Height;

    private static TranslateTransform EnsureTranslate(Control control)
    {
        if (control.RenderTransform is TranslateTransform translate)
            return translate;

        translate = new TranslateTransform();
        control.RenderTransform = translate;
        return translate;
    }

    private static void SetActionBarState(
        Control actionBar,
        Control spacingTarget,
        TranslateTransform translate,
        bool visible,
        double targetBottomMargin)
    {
        actionBar.IsVisible = visible;
        actionBar.Opacity = visible ? 1d : 0d;
        translate.Y = visible ? 0d : MotionTokens.ListSelectionBarOffsetY;
        SetBottomMargin(spacingTarget, targetBottomMargin);
    }

    private static void SetBottomMargin(Control control, double bottom)
    {
        Thickness margin = control.Margin;
        control.Margin = new Thickness(margin.Left, margin.Top, margin.Right, Math.Max(0d, bottom));
    }
}
