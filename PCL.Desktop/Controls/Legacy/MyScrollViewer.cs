// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

public class MyScrollViewer : ScrollViewer
{
    private readonly string _scrollAnimationId = $"MyScrollViewer Scroll {Guid.NewGuid():N}";
    private double _realOffset;
    private double _overscrollY;
    private int _lastWheelTick;

    public MyScrollViewer()
    {
        PointerWheelChanged += MyScrollViewer_PointerWheelChanged;
        ScrollChanged += (_, _) =>
        {
            if (Math.Abs(_overscrollY) < 0.01d)
                _realOffset = Offset.Y;
        };
    }

    public static readonly StyledProperty<double> DeltaMultProperty =
        AvaloniaProperty.Register<MyScrollViewer, double>(nameof(DeltaMult), 1d);

    public double DeltaMult
    {
        get => GetValue(DeltaMultProperty);
        set => SetValue(DeltaMultProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    public new void ScrollToHome()
    {
        _realOffset = 0d;
        _overscrollY = 0d;
        Offset = new Vector(Offset.X, 0d);
        ModAnimation.AniStop(_scrollAnimationId);
    }

    public void PerformVerticalOffsetDelta(double delta)
    {
        double maxOffset = GetMaxVerticalOffset();
        if (maxOffset <= 0d && Math.Abs(delta) < 0.0001d)
            return;

        bool trackpadLike = OperatingSystem.IsMacOS() || IsHighFrequencyWheel();
        if (trackpadLike)
        {
            ApplyContinuousScroll(delta * DeltaMult, maxOffset);
            return;
        }

        ModAnimation.AniStart(
            ModAnimation.AaDouble(value =>
            {
                _realOffset = Math.Clamp(_realOffset + value, 0d, Math.Max(0d, maxOffset));
                Offset = new Vector(Offset.X, _realOffset);
            }, delta * DeltaMult, MotionTokens.ScrollSettleMs, ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
            _scrollAnimationId);
    }

    private void MyScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < 0.0001d)
            return;

        if (ShouldLetChildHandleWheel(e.Source))
            return;

        double maxOffset = GetMaxVerticalOffset();
        if (maxOffset <= 0d && !OperatingSystem.IsMacOS())
            return;

        e.Handled = true;
        _lastWheelTick = Environment.TickCount;
        PerformVerticalOffsetDelta(ResolveWheelDeltaPixels(e));
    }

    /// <summary>
    /// Windows mouse wheels report notch multiples of ~1 (later ×120).
    /// macOS trackpads deliver continuous fractional line deltas — use a gentler scale
    /// so content stays 1:1 with finger motion (Apple direct manipulation).
    /// </summary>
    private static double ResolveWheelDeltaPixels(PointerWheelEventArgs e)
    {
        double lines = e.Delta.Y;
        if (OperatingSystem.IsMacOS())
        {
            // Fractional trackpad deltas are already smooth; map closer to pixels.
            double magnitude = Math.Abs(lines);
            double scale = magnitude < 1d ? 36d : 48d;
            return -lines * scale;
        }

        return -lines * 120d;
    }

    private void ApplyContinuousScroll(double deltaPixels, double maxOffset)
    {
        ModAnimation.AniStop(_scrollAnimationId);

        double next = _realOffset + deltaPixels + _overscrollY;
        if (maxOffset <= 0d)
        {
            // Empty content: still absorb the gesture (no hard edge feel).
            _overscrollY = RubberBand(next, viewport: Math.Max(1d, Viewport.Height));
            _realOffset = 0d;
            Offset = new Vector(Offset.X, _overscrollY * 0.35d);
            ScheduleOverscrollSettle();
            return;
        }

        if (next < 0d)
        {
            _realOffset = 0d;
            _overscrollY = RubberBand(next, viewport: Math.Max(1d, Viewport.Height));
            Offset = new Vector(Offset.X, _overscrollY);
            ScheduleOverscrollSettle();
            return;
        }

        if (next > maxOffset)
        {
            _realOffset = maxOffset;
            _overscrollY = RubberBand(next - maxOffset, viewport: Math.Max(1d, Viewport.Height));
            Offset = new Vector(Offset.X, maxOffset + _overscrollY);
            ScheduleOverscrollSettle();
            return;
        }

        _overscrollY = 0d;
        // 1:1 with trackpad finger motion (Apple direct manipulation).
        _realOffset = next;
        Offset = new Vector(Offset.X, _realOffset);
    }

    private void ScheduleOverscrollSettle()
    {
        double from = _overscrollY;
        if (Math.Abs(from) < 0.5d)
        {
            _overscrollY = 0d;
            Offset = new Vector(Offset.X, _realOffset);
            return;
        }

        ModAnimation.AniStart(
            ModAnimation.AaDouble(value =>
            {
                _overscrollY = from + value;
                Offset = new Vector(Offset.X, _realOffset + _overscrollY);
            }, -from, 220, ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
            _scrollAnimationId + "-overscroll");
        _overscrollY = 0d;
    }

    /// <summary>Apple-style progressive resistance past scroll bounds.</summary>
    private static double RubberBand(double overshoot, double viewport, double constant = 0.55d)
    {
        if (Math.Abs(overshoot) < 0.0001d)
            return 0d;
        double sign = Math.Sign(overshoot);
        double magnitude = Math.Abs(overshoot);
        return sign * (magnitude * viewport * constant) / (viewport + constant * magnitude);
    }

    private bool IsHighFrequencyWheel()
    {
        int now = Environment.TickCount;
        int elapsed = now - _lastWheelTick;
        return elapsed is > 0 and < 40;
    }

    private static bool ShouldLetChildHandleWheel(object? source) =>
        source is ComboBox { IsDropDownOpen: true } ||
        source is TextBox { AcceptsReturn: true } ||
        source is ComboBoxItem ||
        source is CheckBox;

    private double GetMaxVerticalOffset()
    {
        double maxOffset = Extent.Height - Viewport.Height;
        if (double.IsNaN(maxOffset) || double.IsInfinity(maxOffset))
            return 0d;

        return Math.Max(0d, maxOffset);
    }
}
