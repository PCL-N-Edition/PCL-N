// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Composition root for layout, style, text and interactive input systems.</summary>
public sealed class UiInteractiveRuntime : IDisposable
{
    private readonly UiLayoutRuntime _layoutRuntime;
    private bool _disposed;

    public UiInteractiveRuntime(
        UiWorld world,
        ITextEngine textEngine,
        UiSize viewport,
        bool applyDefaults = true,
        int textCacheCapacity = 512,
        UiGestureThresholds? gestureThresholds = null,
        UiMotionRegistry? motionRegistry = null)
    {
        _layoutRuntime = new UiLayoutRuntime(
            world,
            textEngine,
            viewport,
            applyDefaults,
            textCacheCapacity);
        Animation = new UiAnimationRuntime(world, motionRegistry);
        Input = new UiInputRuntime(world, gestureThresholds);
        Scroll = new UiScrollRuntime(world, Input);
    }

    public UiWorld World => _layoutRuntime.World;
    public ThemeRegistry Theme => _layoutRuntime.Theme;
    public UiStyleSheet Styles => _layoutRuntime.Styles;
    public TextLayoutCache TextCache => _layoutRuntime.TextCache;
    public TextMeasurementService TextMeasurement => _layoutRuntime.TextMeasurement;
    public LayoutEngine Layout => _layoutRuntime.Layout;
    public UiAnimationRuntime Animation { get; }
    public UiInputRuntime Input { get; }
    public UiScrollRuntime Scroll { get; }

    public void SetViewport(UiSize viewport) => _layoutRuntime.SetViewport(viewport);

    public void Dispose()
    {
        if (_disposed)
            return;
        Scroll.Dispose();
        Input.Dispose();
        Animation.Dispose();
        _layoutRuntime.Dispose();
        _disposed = true;
    }
}
