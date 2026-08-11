// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Converts text-content/style dirtiness into layout dirtiness. Actual constrained shaping
/// is performed synchronously by <see cref="TextMeasurementService"/> during Layout Measure.
/// </summary>
public sealed class TextMeasureSystem : IUiSystem
{
    private readonly List<UiEntity> _dirty = [];

    public UiSystemPhase Phase => UiSystemPhase.TextImageMeasure;

    public string Name => "text.invalidate-measure";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = frame;
        _dirty.Clear();
        world.Dirty.Collect(UiDirtyFlags.TextMeasure, _dirty);
        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!world.Entities.IsAlive(entity) || !world.Components.Has<TextContent>(entity))
            {
                world.Dirty.Clear(entity, UiDirtyFlags.TextMeasure);
                continue;
            }
            LayoutInvalidation.MarkMeasure(world, entity, requestFrame: false);
        }
    }
}

/// <summary>Constraint-aware text shaping service called from LayoutEngine.MeasureEntity.</summary>
public sealed class TextMeasurementService : IDisposable
{
    private readonly UiWorld _world;
    private readonly TextLayoutCache _cache;
    private readonly List<UiEntity> _entities = [];
    private bool _disposed;

    public TextMeasurementService(UiWorld world, TextLayoutCache cache)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _world.EntityDestroying += OnEntityDestroying;
    }

    public UiSize Resolve(UiEntity entity, float availableContentWidth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_world.Components.TryGet(entity, out TextContent text))
            return UiSize.Zero;

        ResolvedStyle style = _world.Components.TryGet(entity, out ResolvedStyle resolved)
            ? resolved
            : ResolvedStyle.Default;
        TextFormat format = _world.Components.TryGet(entity, out TextFormat configured)
            ? configured
            : TextFormat.Default;
        float constraint = ResolveConstraint(format, availableContentWidth);
        TextLayoutRequest request = new(
            text.Value ?? string.Empty,
            style.FontFamilyId,
            style.FontSize,
            style.FontWeight,
            constraint,
            format.Wrapping,
            format.Direction);

        TextCacheEntryHandle previous = _world.Components.TryGet(entity, out TextLayout current)
            ? current.CacheEntry
            : TextCacheEntryHandle.None;
        TextLayout next = _cache.Acquire(in request, previous);
        bool changed = !_world.Components.TryGet(entity, out current) || current.Handle != next.Handle;
        _world.Set(entity, next);
        _world.Dirty.Clear(entity, UiDirtyFlags.TextMeasure);
        if (changed)
            _world.Dirty.Mark(entity, UiDirtyFlags.Render);
        return next.Size;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.EntityDestroying -= OnEntityDestroying;
        _entities.Clear();
        _world.Components.Pool<TextLayout>().CopyEntitiesTo(_entities);
        for (int i = 0; i < _entities.Count; i++)
        {
            UiEntity entity = _entities[i];
            if (!_world.Entities.IsAlive(entity) || !_world.Components.TryGet(entity, out TextLayout layout))
                continue;
            _cache.Release(layout.CacheEntry);
            _world.Remove<TextLayout>(entity);
        }
        _disposed = true;
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        if (_world.Components.TryGet(entity, out TextLayout layout))
            _cache.Release(layout.CacheEntry);
    }

    private static float ResolveConstraint(TextFormat format, float availableContentWidth)
    {
        if (format.Wrapping != UiTextWrapping.Wrap)
            return float.PositiveInfinity;
        float available = float.IsFinite(availableContentWidth)
            ? Math.Max(0f, availableContentWidth)
            : float.PositiveInfinity;
        float configured = format.MaxWidth > 0f && !float.IsNaN(format.MaxWidth)
            ? format.MaxWidth
            : float.PositiveInfinity;
        return Math.Min(available, configured);
    }
}
