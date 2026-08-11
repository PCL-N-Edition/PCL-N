// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Composition root for the Phase 3 style, text and incremental layout systems.</summary>
public sealed class UiPhase3Runtime
{
    public UiPhase3Runtime(
        UiWorld world,
        ITextEngine textEngine,
        UiSize viewport,
        bool applyDefaults = true)
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

        TextCache = new TextLayoutCache(textEngine);
        Layout = new LayoutEngine(world, viewport);
        world.Systems.Register(new StyleSystem(world, Theme, Styles));
        world.Systems.Register(new TextMeasureSystem(TextCache));
        world.Systems.Register(new LayoutMeasureSystem(Layout));
        world.Systems.Register(new LayoutArrangeSystem(Layout));
        world.Scheduler.RequestReactiveFrame();
    }

    public UiWorld World { get; }
    public ThemeRegistry Theme { get; }
    public UiStyleSheet Styles { get; }
    public TextLayoutCache TextCache { get; }
    public LayoutEngine Layout { get; }

    public void SetViewport(UiSize viewport) => Layout.SetViewport(viewport);
}
