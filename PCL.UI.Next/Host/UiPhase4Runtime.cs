// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Composition root for Phase 4 layout/style/text plus input and focus.</summary>
public sealed class UiPhase4Runtime : IDisposable
{
    private readonly UiPhase3Runtime _phase3;
    private bool _disposed;

    public UiPhase4Runtime(
        UiWorld world,
        ITextEngine textEngine,
        UiSize viewport,
        bool applyDefaults = true,
        int textCacheCapacity = 512,
        UiGestureThresholds? gestureThresholds = null)
    {
        _phase3 = new UiPhase3Runtime(
            world,
            textEngine,
            viewport,
            applyDefaults,
            textCacheCapacity);
        Input = new UiInputRuntime(world, gestureThresholds);
    }

    public UiWorld World => _phase3.World;
    public ThemeRegistry Theme => _phase3.Theme;
    public UiStyleSheet Styles => _phase3.Styles;
    public TextLayoutCache TextCache => _phase3.TextCache;
    public TextMeasurementService TextMeasurement => _phase3.TextMeasurement;
    public LayoutEngine Layout => _phase3.Layout;
    public UiInputRuntime Input { get; }

    public void SetViewport(UiSize viewport) => _phase3.SetViewport(viewport);

    public void Dispose()
    {
        if (_disposed)
            return;
        Input.Dispose();
        _phase3.Dispose();
        _disposed = true;
    }
}
