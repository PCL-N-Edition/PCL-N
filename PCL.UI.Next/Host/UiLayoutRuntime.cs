// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Composition root for style, text and incremental layout systems.</summary>
public sealed class UiLayoutRuntime : IDisposable
{
    private readonly StyleSystem _styleSystem;
    private readonly TextMeasureSystem _textMeasureSystem;
    private readonly LayoutMeasureSystem _layoutMeasureSystem;
    private readonly LayoutArrangeSystem _layoutArrangeSystem;
    private bool _disposed;

    public UiLayoutRuntime(
        UiWorld world,
        ITextEngine textEngine,
        UiSize viewport,
        bool applyDefaults = true,
        int textCacheCapacity = 512)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        ArgumentNullException.ThrowIfNull(textEngine);

        Theme = new ThemeRegistry();
        Styles = new UiStyleSheet();
        if (applyDefaults)
        {
            UiDefaultTheme.Apply(Theme);
            UiDefaultStyles.Apply(Styles);
        }

        TextCache = new TextLayoutCache(textEngine, textCacheCapacity);
        TextMeasurement = new TextMeasurementService(world, TextCache);
        Layout = new LayoutEngine(world, viewport, TextMeasurement);
        _styleSystem = new StyleSystem(world, Theme, Styles);
        _textMeasureSystem = new TextMeasureSystem();
        _layoutMeasureSystem = new LayoutMeasureSystem(Layout);
        _layoutArrangeSystem = new LayoutArrangeSystem(Layout);
        world.Systems.Register(_styleSystem);
        world.Systems.Register(_textMeasureSystem);
        world.Systems.Register(_layoutMeasureSystem);
        world.Systems.Register(_layoutArrangeSystem);
        world.Scheduler.RequestReactiveFrame();
    }

    public UiWorld World { get; }
    public ThemeRegistry Theme { get; }
    public UiStyleSheet Styles { get; }
    public TextLayoutCache TextCache { get; }
    public TextMeasurementService TextMeasurement { get; }
    public LayoutEngine Layout { get; }

    public void SetViewport(UiSize viewport) => Layout.SetViewport(viewport);

    internal void EnsureCanDispose() => TextCache.EnsureCanDispose();

    public void Dispose()
    {
        if (_disposed)
            return;
        EnsureCanDispose();
        World.Systems.Unregister(_layoutArrangeSystem);
        World.Systems.Unregister(_layoutMeasureSystem);
        World.Systems.Unregister(_textMeasureSystem);
        World.Systems.Unregister(_styleSystem);
        _styleSystem.Dispose();
        TextMeasurement.Dispose();
        TextCache.Dispose();
        _disposed = true;
    }
}
