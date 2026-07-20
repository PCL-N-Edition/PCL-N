// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Theme;

/// <summary>
/// Shared motion tokens for Apple-style fluid desktop UI
/// (WWDC "Designing Fluid Interfaces": response, interruptibility, critically damped default).
/// <list type="bullet">
/// <item>Default damping is critical (no bounce). Bounce only for momentum gestures.</item>
/// <item>Response is ~0.28–0.4s for structural UI; press feedback is snappier (~80–220ms).</item>
/// <item>Prefer opacity + transform only; never leave presentation mid-flight on interrupt.</item>
/// </list>
/// </summary>
internal static class MotionTokens
{
    /// <summary>Unified press scale for buttons and list rows (pointer-down feedback).</summary>
    public const double PressScale = 0.97d;

    /// <summary>Milliseconds for press-in scale (instant response on pointer-down).</summary>
    public const int PressInMs = 80;

    /// <summary>Milliseconds for press-out / release settle (critically damped).</summary>
    public const int PressOutMs = 220;

    /// <summary>Main content cross-fade when switching top-level pages (out).</summary>
    public const int NavCrossfadeOutMs = 70;

    /// <summary>Main content cross-fade when switching top-level pages (in).</summary>
    public const int NavCrossfadeInMs = 140;

    /// <summary>Subtle vertical settle on page host during nav enter (px).</summary>
    public const double NavEnterOffsetY = 4d;

    /// <summary>Page content enter opacity duration (snappy response).</summary>
    public const int PageEnterOpacityMs = 120;

    /// <summary>Page content enter vertical travel duration (no overshoot).</summary>
    public const int PageEnterSlideMs = 200;

    /// <summary>Stagger between animated children on page enter.</summary>
    public const int PageStaggerMs = 12;

    /// <summary>
    /// Initial translate offset (px) before page enter settles.
    /// Positive = start slightly below and rise (content materializes upward).
    /// </summary>
    public const double PageEnterOffsetY = 8d;

    /// <summary>Hover color / shadow transitions.</summary>
    public const int HoverMs = 100;

    /// <summary>Wheel scroll settle duration.</summary>
    public const int ScrollSettleMs = 220;

    /// <summary>Window show opacity / settle (critically damped, no bounce).</summary>
    public const int WindowShowOpacityMs = 280;

    /// <summary>Window show vertical settle.</summary>
    public const int WindowShowSlideMs = 360;

    /// <summary>Reduced-motion cross-fade only (no slide / scale).</summary>
    public const int ReducedMotionFadeMs = 180;

    /// <summary>Max children that stagger on page enter; rest appear settled (keeps motion + snappiness).</summary>
    public const int PageEnterMaxChildren = 12;

    /// <summary>List rows that receive entrance stagger.</summary>
    public const int ListEnterMaxChildren = 30;

    /// <summary>Stagger between list entrance rows (ms).</summary>
    public const int ListStaggerMs = 16;

    /// <summary>List entrance opacity duration.</summary>
    public const int ListEnterOpacityMs = 180;

    /// <summary>List entrance vertical travel duration.</summary>
    public const int ListEnterSlideMs = 260;

    /// <summary>List entrance start Y offset (rise from below).</summary>
    public const double ListEnterOffsetY = 8d;
}
