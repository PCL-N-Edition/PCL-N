// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Theme;

/// <summary>
/// Shared motion and interaction tokens for Apple-style fluid desktop UI.
/// Defaults are critically damped (no bounce); keep bounce only for momentum gestures.
/// </summary>
internal static class MotionTokens
{
    /// <summary>Unified press scale for buttons and list rows (pointer-down feedback).</summary>
    public const double PressScale = 0.97d;

    /// <summary>Milliseconds for press-in scale.</summary>
    public const int PressInMs = 90;

    /// <summary>Milliseconds for press-out / release settle.</summary>
    public const int PressOutMs = 240;

    /// <summary>Main content cross-fade when switching top-level pages (out).</summary>
    public const int NavCrossfadeOutMs = 90;

    /// <summary>Main content cross-fade when switching top-level pages (in).</summary>
    public const int NavCrossfadeInMs = 150;

    /// <summary>Page content enter opacity duration.</summary>
    public const int PageEnterOpacityMs = 120;

    /// <summary>Page content enter vertical travel duration (no overshoot).</summary>
    public const int PageEnterSlideMs = 240;

    /// <summary>Stagger between animated children on page enter.</summary>
    public const int PageStaggerMs = 14;

    /// <summary>Initial translate offset (px) before page enter settles.</summary>
    public const double PageEnterOffsetY = -10d;

    /// <summary>Hover color / shadow transitions.</summary>
    public const int HoverMs = 100;

    /// <summary>Wheel scroll settle duration.</summary>
    public const int ScrollSettleMs = 220;

    /// <summary>Reduced-motion cross-fade only.</summary>
    public const int ReducedMotionFadeMs = 160;

    /// <summary>Maximum number of children that receive staggered page enter.</summary>
    public const int PageEnterMaxChildren = 36;

    /// <summary>List rows that receive entrance stagger.</summary>
    public const int ListEnterMaxChildren = 30;

    /// <summary>Stagger between list entrance rows (ms).</summary>
    public const int ListStaggerMs = 18;

    /// <summary>List entrance opacity duration.</summary>
    public const int ListEnterOpacityMs = 160;

    /// <summary>List entrance vertical travel duration.</summary>
    public const int ListEnterSlideMs = 220;

    /// <summary>List multi-selection indicator settle duration (critically damped).</summary>
    public const int ListSelectionIndicatorMs = 180;

    /// <summary>Selection action bar enter duration.</summary>
    public const int ListSelectionBarEnterMs = 220;

    /// <summary>Selection action bar exit duration.</summary>
    public const int ListSelectionBarExitMs = 150;

    /// <summary>Vertical travel used by selection action bars.</summary>
    public const double ListSelectionBarOffsetY = 12d;
}
