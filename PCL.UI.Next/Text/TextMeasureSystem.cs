// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public sealed class TextMeasureSystem : IUiSystem
{
    private readonly TextLayoutCache _cache;
    private readonly List<UiEntity> _dirty = [];

    public TextMeasureSystem(TextLayoutCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public UiSystemPhase Phase => UiSystemPhase.TextImageMeasure;

    public string Name => "text.measure";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = frame;
        _dirty.Clear();
        world.Dirty.Collect(UiDirtyFlags.TextMeasure, _dirty);

        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!world.Entities.IsAlive(entity) || !world.Components.TryGet(entity, out TextContent text))
            {
                world.Dirty.Clear(entity, UiDirtyFlags.TextMeasure);
                continue;
            }

            ResolvedStyle style = world.Components.TryGet(entity, out ResolvedStyle resolved)
                ? resolved
                : ResolvedStyle.Default;
            TextFormat format = world.Components.TryGet(entity, out TextFormat configured)
                ? configured
                : TextFormat.Default;
            float constraint = format.MaxWidth;
            if (world.Components.TryGet(entity, out LayoutStyle layout) &&
                layout.Width.Kind == UiLengthKind.Pixels)
            {
                constraint = Math.Min(constraint, Math.Max(0f, layout.Width.Value - layout.Padding.Horizontal - style.Padding.Horizontal));
            }

            TextLayoutRequest request = new(
                text.Value ?? string.Empty,
                style.FontFamilyId,
                style.FontSize,
                style.FontWeight,
                constraint,
                format.Wrapping,
                format.Direction);
            TextLayout next = _cache.GetOrCreate(in request);
            bool sizeChanged = !world.Components.TryGet(entity, out TextLayout previous) || previous.Size != next.Size;
            world.Set(entity, next);
            world.Dirty.Clear(entity, UiDirtyFlags.TextMeasure);
            world.Dirty.Mark(entity, UiDirtyFlags.Render);
            if (sizeChanged)
                LayoutInvalidation.MarkMeasure(world, entity, requestFrame: false);
        }
    }
}
