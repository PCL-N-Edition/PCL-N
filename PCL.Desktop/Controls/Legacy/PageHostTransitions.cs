// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Media;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>
/// Shared host-level page swaps with Apple-style fade + slight rise.
/// Keeps motion when feature surfaces swap right-pane content without going through main nav.
/// </summary>
internal static class PageHostTransitions
{
    public const string RightHostAnimationKey = "FrmMain PageChangeRight";

    /// <summary>
    /// Swaps <paramref name="rightHost"/>.Child to <paramref name="target"/> with optional crossfade.
    /// Always ends with <see cref="MyPageRight.PageOnEnter"/> so content stagger is not skipped.
    /// </summary>
    public static void TransitionRightPage(
        Border rightHost,
        MyPageRight target,
        bool animate,
        Action? onHostUpdated = null)
    {
        ArgumentNullException.ThrowIfNull(rightHost);
        ArgumentNullException.ThrowIfNull(target);

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (ReferenceEquals(oldRight, target))
        {
            rightHost.Opacity = 1d;
            ResetHostTranslate(rightHost);
            onHostUpdated?.Invoke();
            target.PageOnEnter();
            return;
        }

        if (!animate)
        {
            oldRight?.PageOnExit();
            rightHost.Child = target;
            rightHost.Opacity = 1d;
            ResetHostTranslate(rightHost);
            onHostUpdated?.Invoke();
            target.PageOnEnter();
            return;
        }

        if (rightHost.RenderTransform is not TranslateTransform navTranslate)
        {
            navTranslate = new TranslateTransform();
            rightHost.RenderTransform = navTranslate;
        }

        oldRight?.PageOnExit();
        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(
                    rightHost,
                    -rightHost.Opacity,
                    MotionTokens.NavCrossfadeOutMs,
                    ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaCode(() =>
                {
                    oldRight?.PageOnForceExit();
                    rightHost.Child = target;
                    rightHost.Opacity = 0d;
                    navTranslate.Y = MotionTokens.NavEnterOffsetY;
                    onHostUpdated?.Invoke();
                }, after: true),
                ModAnimation.AaOpacity(
                    rightHost,
                    1d,
                    MotionTokens.NavCrossfadeInMs,
                    ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaTranslateY(
                    rightHost,
                    -MotionTokens.NavEnterOffsetY,
                    MotionTokens.NavCrossfadeInMs,
                    ease: new ModAnimation.AniEaseOutFluent()),
                ModAnimation.AaCode(() =>
                {
                    rightHost.Opacity = 1d;
                    navTranslate.Y = 0d;
                    target.PageOnEnter();
                }, after: true)
            },
            RightHostAnimationKey);
    }

    private static void ResetHostTranslate(Control host)
    {
        if (host.RenderTransform is TranslateTransform t)
            t.Y = 0d;
    }
}
