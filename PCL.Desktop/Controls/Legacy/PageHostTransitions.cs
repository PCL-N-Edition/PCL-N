// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Media;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>
/// Host-level page swaps: short cross-fade only (no long script drain on interrupt).
/// </summary>
internal static class PageHostTransitions
{
    public const string RightHostAnimationKey = "FrmMain PageChangeRight";

    /// <summary>
    /// Swaps <paramref name="rightHost"/>.Child to <paramref name="target"/>.
    /// Content enter runs at swap time (parallel with host fade) — never re-zero content
    /// after the host tween finishes (that was the end-frame 拉回).
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
            SnapHostVisible(rightHost);
            onHostUpdated?.Invoke();
            target.PageOnEnter();
            return;
        }

        // Drop any in-flight host script immediately — do not finish after-chains.
        ModAnimation.AniStop(RightHostAnimationKey, finish: false);

        if (!animate)
        {
            oldRight?.PageOnExit();
            rightHost.Child = target;
            SnapHostVisible(rightHost);
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
        rightHost.Child = target;
        onHostUpdated?.Invoke();

        // Content motion starts with the swap — not after the host fade (avoids end-frame blank/拉回).
        target.PageOnEnter();

        double fromOpacity = rightHost.Opacity;
        if (fromOpacity >= 0.98d)
            fromOpacity = 0d;
        rightHost.Opacity = fromOpacity;
        navTranslate.Y = MotionTokens.NavEnterOffsetY;

        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(
                    rightHost,
                    1d - fromOpacity,
                    MotionTokens.NavCrossfadeInMs,
                    ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaTranslateY(
                    rightHost,
                    -MotionTokens.NavEnterOffsetY,
                    MotionTokens.NavCrossfadeInMs,
                    ease: new ModAnimation.AniEaseOutFluent()),
                ModAnimation.AaCode(() => SnapHostVisible(rightHost), after: true)
            },
            RightHostAnimationKey,
            refreshTime: false,
            finishPrevious: false);
    }

    public static void SnapHostVisible(Control host)
    {
        if (host.Opacity < 0.999d)
            host.Opacity = 1d;
        if (host.RenderTransform is TranslateTransform t && Math.Abs(t.Y) > 0.01d)
            t.Y = 0d;
    }
}
