// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;

namespace PCL.UI.Next;

/// <summary>
/// Incremental logical-pixel measure/arrange engine. Dirty ancestors are measured once;
/// unchanged child measurements are reused through LayoutMeasureInput.
/// </summary>
public sealed class LayoutEngine
{
    private readonly UiWorld _world;
    private readonly TextMeasurementService _textMeasurement;
    private readonly List<UiEntity> _dirty = [];
    private readonly List<UiEntity> _roots = [];
    private readonly List<UiEntity> _entities = [];
    private UiSize _viewport;

    public LayoutEngine(UiWorld world, UiSize viewport, TextMeasurementService textMeasurement)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _textMeasurement = textMeasurement ?? throw new ArgumentNullException(nameof(textMeasurement));
        _viewport = NormalizeViewport(viewport);
    }

    public UiSize Viewport => _viewport;

    /// <summary>Diagnostics: entities that performed real measure work in the latest layout phase.</summary>
    public int LastMeasureCount { get; private set; }

    public void SetViewport(UiSize viewport)
    {
        UiSize next = NormalizeViewport(viewport);
        if (next == _viewport)
            return;
        _viewport = next;

        _entities.Clear();
        _world.Components.Pool<LayoutStyle>().CopyEntitiesTo(_entities);
        for (int i = 0; i < _entities.Count; i++)
        {
            UiEntity entity = _entities[i];
            if (_world.Entities.IsAlive(entity) && IsHierarchyRoot(entity))
                LayoutInvalidation.MarkMeasure(_world, entity, requestFrame: false);
        }

        _world.Scheduler.RequestReactiveFrame();
    }

    internal void Measure()
    {
        LastMeasureCount = 0;
        const int maxBoundaryPasses = 64;
        for (int pass = 0; pass < maxBoundaryPasses; pass++)
        {
            _dirty.Clear();
            _world.Dirty.Collect(UiDirtyFlags.LayoutMeasure, _dirty);
            if (_dirty.Count == 0)
                return;

            _roots.Clear();
            for (int i = 0; i < _dirty.Count; i++)
            {
                UiEntity entity = _dirty[i];
                if (!_world.Entities.IsAlive(entity) || HasDirtyParent(entity, UiDirtyFlags.LayoutMeasure))
                    continue;
                _roots.Add(entity);
            }

            for (int i = 0; i < _roots.Count; i++)
            {
                UiEntity root = _roots[i];
                UiSize available = GetMeasureAvailable(root);
                MeasureEntity(root, available);
            }
        }

        _dirty.Clear();
        _world.Dirty.Collect(UiDirtyFlags.LayoutMeasure, _dirty);
        if (_dirty.Count == 0)
            return;
        throw new InvalidOperationException("Layout boundary measure propagation did not converge.");
    }

    internal void Arrange()
    {
        _dirty.Clear();
        _world.Dirty.Collect(UiDirtyFlags.LayoutArrange, _dirty);
        _roots.Clear();
        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!_world.Entities.IsAlive(entity) || HasDirtyParent(entity, UiDirtyFlags.LayoutArrange))
                continue;
            _roots.Add(entity);
        }

        for (int i = 0; i < _roots.Count; i++)
        {
            UiEntity root = _roots[i];
            UiRect slot = GetArrangeSlot(root);
            ArrangeEntity(root, slot);
        }
    }

    private UiSize MeasureEntity(UiEntity entity, UiSize available)
    {
        available = NormalizeAvailable(available);
        bool inputChanged = !_world.Components.TryGet(entity, out LayoutMeasureInput previousInput) ||
                            previousInput.Available != available;
        bool mustMeasure = inputChanged ||
                           !_world.Components.Has<DesiredSize>(entity) ||
                           (_world.Dirty.GetFlags(entity) & UiDirtyFlags.LayoutMeasure) != 0;
        if (!mustMeasure)
            return _world.Components.Get<DesiredSize>(entity).Value;
        LastMeasureCount++;

        LayoutStyle style = GetLayoutStyle(entity);
        UiThickness padding = GetPadding(entity, in style);
        float availableBorderWidth = SubtractFinite(available.Width, style.Margin.Horizontal);
        float availableBorderHeight = SubtractFinite(available.Height, style.Margin.Vertical);
        float measureWidth = ResolveMeasureConstraint(style.Width, availableBorderWidth, padding.Horizontal);
        float measureHeight = ResolveMeasureConstraint(style.Height, availableBorderHeight, padding.Vertical);
        UiSize contentAvailable = new(
            SubtractFinite(measureWidth, padding.Horizontal),
            SubtractFinite(measureHeight, padding.Vertical));

        UiSize content = MeasureContent(entity, contentAvailable);
        float naturalWidth = content.Width + padding.Horizontal;
        float naturalHeight = content.Height + padding.Vertical;
        float borderWidth = ResolveDesired(style.Width, naturalWidth, availableBorderWidth);
        float borderHeight = ResolveDesired(style.Height, naturalHeight, availableBorderHeight);
        borderWidth = ClampDimension(borderWidth, style.MinSize.Width, style.MaxSize.Width);
        borderHeight = ClampDimension(borderHeight, style.MinSize.Height, style.MaxSize.Height);
        UiSize desired = new(borderWidth + style.Margin.Horizontal, borderHeight + style.Margin.Vertical);

        bool changed = !_world.Components.TryGet(entity, out DesiredSize previous) || previous.Value != desired;
        _world.Set(entity, new LayoutMeasureInput { Available = available });
        _world.Set(entity, new DesiredSize { Value = desired });
        _world.Dirty.Clear(entity, UiDirtyFlags.LayoutMeasure);
        if (changed)
        {
            _world.Dirty.Mark(entity, UiDirtyFlags.LayoutArrange);
            if (style.IsMeasureBoundary &&
                _world.Hierarchy.TryGetNode(entity, out HierarchyNode node) &&
                _world.Entities.IsAlive(node.Parent))
            {
                LayoutInvalidation.MarkMeasure(_world, node.Parent, requestFrame: false);
            }
        }
        return desired;
    }

    private UiSize MeasureContent(UiEntity entity, UiSize available)
    {
        if (_world.Components.Has<TextContent>(entity))
            return _textMeasurement.Resolve(entity, available.Width);
        if (_world.Components.TryGet(entity, out StackLayout stack))
            return MeasureStack(entity, available, in stack);
        if (_world.Components.TryGet(entity, out GridLayout grid))
            return MeasureGrid(entity, available, in grid);
        if (_world.Components.Has<AbsoluteLayout>(entity))
            return MeasureAbsolute(entity, available);
        if (_world.Components.Has<OverlayLayout>(entity))
            return MeasureOverlay(entity, available);
        return MeasureOverlay(entity, available);
    }

    private UiSize MeasureStack(UiEntity entity, UiSize available, in StackLayout stack)
    {
        float main = 0f;
        float cross = 0f;
        int count = 0;
        UiEntity child = FirstChild(entity);
        while (child != UiEntity.None)
        {
            UiSize childAvailable = stack.Orientation == UiOrientation.Vertical
                ? new UiSize(available.Width, float.PositiveInfinity)
                : new UiSize(float.PositiveInfinity, available.Height);
            UiSize desired = MeasureEntity(child, childAvailable);
            if (stack.Orientation == UiOrientation.Vertical)
            {
                main += desired.Height;
                cross = Math.Max(cross, desired.Width);
            }
            else
            {
                main += desired.Width;
                cross = Math.Max(cross, desired.Height);
            }
            count++;
            child = NextSibling(child);
        }

        if (count > 1)
            main += Math.Max(0f, stack.Gap) * (count - 1);
        return stack.Orientation == UiOrientation.Vertical
            ? new UiSize(cross, main)
            : new UiSize(main, cross);
    }

    private UiSize MeasureOverlay(UiEntity entity, UiSize available)
    {
        float width = 0f;
        float height = 0f;
        UiEntity child = FirstChild(entity);
        while (child != UiEntity.None)
        {
            UiSize desired = MeasureEntity(child, available);
            width = Math.Max(width, desired.Width);
            height = Math.Max(height, desired.Height);
            child = NextSibling(child);
        }
        return new UiSize(width, height);
    }

    private UiSize MeasureAbsolute(UiEntity entity, UiSize available)
    {
        float width = 0f;
        float height = 0f;
        UiEntity child = FirstChild(entity);
        while (child != UiEntity.None)
        {
            UiSize desired = MeasureEntity(child, UiSize.Infinite);
            AbsolutePlacement placement = _world.Components.TryGet(child, out AbsolutePlacement configured)
                ? configured
                : default;
            width = Math.Max(width, placement.Left + desired.Width);
            height = Math.Max(height, placement.Top + desired.Height);
            child = NextSibling(child);
        }

        if (float.IsFinite(available.Width)) width = Math.Min(width, available.Width);
        if (float.IsFinite(available.Height)) height = Math.Min(height, available.Height);
        return new UiSize(width, height);
    }

    private UiSize MeasureGrid(UiEntity entity, UiSize available, in GridLayout grid)
    {
        ReadOnlySpan<UiGridTrack> columns = _world.LayoutResources.GetColumns(grid.Tracks);
        ReadOnlySpan<UiGridTrack> rows = _world.LayoutResources.GetRows(grid.Tracks);
        float[] columnSizes = ArrayPool<float>.Shared.Rent(columns.Length);
        float[] rowSizes = ArrayPool<float>.Shared.Rent(rows.Length);
        try
        {
            // Resolve content-independent tracks first so constrained children (notably
            // wrapped text) see their actual fixed/star cell width during this measure.
            InitializeTracks(columns, columnSizes);
            InitializeTracks(rows, rowSizes);
            ResolveStars(columns, columnSizes, available.Width, grid.ColumnGap);
            ResolveStars(rows, rowSizes, available.Height, grid.RowGap);
            MeasureGridChildren(entity, columns, rows, columnSizes, rowSizes, grid.ColumnGap, grid.RowGap);

            // Auto/content contributions can change the remaining star space. Recompute,
            // then remeasure once with the final constrained track widths in the same pass.
            ComputeGridTracks(entity, columns, rows, available, grid.ColumnGap, grid.RowGap, columnSizes, rowSizes);
            MeasureGridChildren(entity, columns, rows, columnSizes, rowSizes, grid.ColumnGap, grid.RowGap);
            ComputeGridTracks(entity, columns, rows, available, grid.ColumnGap, grid.RowGap, columnSizes, rowSizes);
            return new UiSize(
                Sum(columnSizes, columns.Length) + GapTotal(grid.ColumnGap, columns.Length),
                Sum(rowSizes, rows.Length) + GapTotal(grid.RowGap, rows.Length));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(columnSizes);
            ArrayPool<float>.Shared.Return(rowSizes);
        }
    }

    private void MeasureGridChildren(
        UiEntity entity,
        ReadOnlySpan<UiGridTrack> columns,
        ReadOnlySpan<UiGridTrack> rows,
        float[] columnSizes,
        float[] rowSizes,
        float columnGap,
        float rowGap)
    {
        UiEntity child = FirstChild(entity);
        while (child != UiEntity.None)
        {
            GridPlacement placement = GetGridPlacement(child, columns.Length, rows.Length);
            float width = GridMeasureConstraint(columns, columnSizes, placement.Column, placement.ColumnSpan, columnGap);
            float height = GridMeasureConstraint(rows, rowSizes, placement.Row, placement.RowSpan, rowGap);
            MeasureEntity(child, new UiSize(width, height));
            child = NextSibling(child);
        }
    }

    private void ArrangeEntity(UiEntity entity, UiRect slot)
    {
        bool slotChanged = !_world.Components.TryGet(entity, out LayoutSlot previousSlot) || previousSlot.Value != slot;
        bool mustArrange = slotChanged ||
                           !_world.Components.Has<LayoutRect>(entity) ||
                           (_world.Dirty.GetFlags(entity) & UiDirtyFlags.LayoutArrange) != 0;
        if (!mustArrange)
            return;

        if (!_world.Components.TryGet(entity, out DesiredSize desiredComponent))
            desiredComponent = new DesiredSize { Value = MeasureEntity(entity, new UiSize(slot.Width, slot.Height)) };

        LayoutStyle style = GetLayoutStyle(entity);
        UiThickness margin = style.Margin;
        float availableWidth = Math.Max(0f, slot.Width - margin.Horizontal);
        float availableHeight = Math.Max(0f, slot.Height - margin.Vertical);
        float desiredWidth = Math.Max(0f, desiredComponent.Value.Width - margin.Horizontal);
        float desiredHeight = Math.Max(0f, desiredComponent.Value.Height - margin.Vertical);
        float width = ResolveArrangeDimension(style.Width, style.HorizontalAlignment == UiHorizontalAlignment.Stretch, availableWidth, desiredWidth);
        float height = ResolveArrangeDimension(style.Height, style.VerticalAlignment == UiVerticalAlignment.Stretch, availableHeight, desiredHeight);
        width = ClampDimension(width, style.MinSize.Width, Math.Min(style.MaxSize.Width, availableWidth));
        height = ClampDimension(height, style.MinSize.Height, Math.Min(style.MaxSize.Height, availableHeight));
        float x = AlignHorizontal(slot.X + margin.Left, availableWidth, width, style.HorizontalAlignment);
        float y = AlignVertical(slot.Y + margin.Top, availableHeight, height, style.VerticalAlignment);
        UiRect rect = new(x, y, width, height);

        bool rectChanged = !_world.Components.TryGet(entity, out LayoutRect previousRect) || previousRect.Value != rect;
        _world.Set(entity, new LayoutSlot { Value = slot });
        _world.Set(entity, new LayoutRect { Value = rect });
        _world.Dirty.Clear(entity, UiDirtyFlags.LayoutArrange);
        if (rectChanged)
            _world.Dirty.Mark(entity, UiDirtyFlags.Transform | UiDirtyFlags.HitTest | UiDirtyFlags.Render);

        UiThickness padding = GetPadding(entity, in style);
        UiRect content = new(
            rect.X + padding.Left,
            rect.Y + padding.Top,
            Math.Max(0f, rect.Width - padding.Horizontal),
            Math.Max(0f, rect.Height - padding.Vertical));

        if (_world.Components.TryGet(entity, out StackLayout stack))
            ArrangeStack(entity, content, in stack);
        else if (_world.Components.TryGet(entity, out GridLayout grid))
            ArrangeGrid(entity, content, in grid);
        else if (_world.Components.Has<AbsoluteLayout>(entity))
            ArrangeAbsolute(entity, content);
        else
            ArrangeOverlay(entity, content);
    }

    private void ArrangeStack(UiEntity entity, UiRect content, in StackLayout stack)
    {
        float cursor = stack.Orientation == UiOrientation.Vertical ? content.Y : content.X;
        UiEntity child = FirstChild(entity);
        while (child != UiEntity.None)
        {
            UiSize desired = _world.Components.TryGet(child, out DesiredSize size) ? size.Value : UiSize.Zero;
            UiRect slot = stack.Orientation == UiOrientation.Vertical
                ? new UiRect(content.X, cursor, content.Width, desired.Height)
                : new UiRect(cursor, content.Y, desired.Width, content.Height);
            ArrangeEntity(child, slot);
            cursor += (stack.Orientation == UiOrientation.Vertical ? desired.Height : desired.Width) + Math.Max(0f, stack.Gap);
            child = NextSibling(child);
        }
    }

    private void ArrangeOverlay(UiEntity entity, UiRect content)
    {
        UiEntity child = FirstChild(entity);
        while (child != UiEntity.None)
        {
            ArrangeEntity(child, content);
            child = NextSibling(child);
        }
    }

    private void ArrangeAbsolute(UiEntity entity, UiRect content)
    {
        UiEntity child = FirstChild(entity);
        while (child != UiEntity.None)
        {
            UiSize desired = _world.Components.TryGet(child, out DesiredSize size) ? size.Value : UiSize.Zero;
            AbsolutePlacement placement = _world.Components.TryGet(child, out AbsolutePlacement configured)
                ? configured
                : default;
            ArrangeEntity(child, new UiRect(
                content.X + placement.Left,
                content.Y + placement.Top,
                desired.Width,
                desired.Height));
            child = NextSibling(child);
        }
    }

    private void ArrangeGrid(UiEntity entity, UiRect content, in GridLayout grid)
    {
        ReadOnlySpan<UiGridTrack> columns = _world.LayoutResources.GetColumns(grid.Tracks);
        ReadOnlySpan<UiGridTrack> rows = _world.LayoutResources.GetRows(grid.Tracks);
        float[] columnSizes = ArrayPool<float>.Shared.Rent(columns.Length);
        float[] rowSizes = ArrayPool<float>.Shared.Rent(rows.Length);
        try
        {
            ComputeGridTracks(
                entity,
                columns,
                rows,
                new UiSize(content.Width, content.Height),
                grid.ColumnGap,
                grid.RowGap,
                columnSizes,
                rowSizes);

            UiEntity child = FirstChild(entity);
            while (child != UiEntity.None)
            {
                GridPlacement placement = GetGridPlacement(child, columns.Length, rows.Length);
                float x = content.X + Prefix(columnSizes, placement.Column, grid.ColumnGap);
                float y = content.Y + Prefix(rowSizes, placement.Row, grid.RowGap);
                float width = SpanSize(columnSizes, placement.Column, placement.ColumnSpan, grid.ColumnGap);
                float height = SpanSize(rowSizes, placement.Row, placement.RowSpan, grid.RowGap);
                ArrangeEntity(child, new UiRect(x, y, width, height));
                child = NextSibling(child);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(columnSizes);
            ArrayPool<float>.Shared.Return(rowSizes);
        }
    }

    private void ComputeGridTracks(
        UiEntity entity,
        ReadOnlySpan<UiGridTrack> columns,
        ReadOnlySpan<UiGridTrack> rows,
        UiSize available,
        float columnGap,
        float rowGap,
        float[] columnSizes,
        float[] rowSizes)
    {
        InitializeTracks(columns, columnSizes);
        InitializeTracks(rows, rowSizes);

        UiEntity child = FirstChild(entity);
        while (child != UiEntity.None)
        {
            UiSize desired = _world.Components.TryGet(child, out DesiredSize size) ? size.Value : UiSize.Zero;
            GridPlacement placement = GetGridPlacement(child, columns.Length, rows.Length);
            GrowTracks(columns, columnSizes, placement.Column, placement.ColumnSpan, desired.Width, columnGap, !float.IsFinite(available.Width));
            GrowTracks(rows, rowSizes, placement.Row, placement.RowSpan, desired.Height, rowGap, !float.IsFinite(available.Height));
            child = NextSibling(child);
        }

        ResolveStars(columns, columnSizes, available.Width, columnGap);
        ResolveStars(rows, rowSizes, available.Height, rowGap);
    }

    private static void InitializeTracks(ReadOnlySpan<UiGridTrack> tracks, float[] sizes)
    {
        for (int i = 0; i < tracks.Length; i++)
        {
            UiGridTrack track = tracks[i];
            sizes[i] = track.Kind == UiGridTrackKind.Fixed
                ? ClampDimension(track.Value, track.Min, track.Max)
                : Math.Max(0f, track.Min);
        }
    }

    private static void GrowTracks(
        ReadOnlySpan<UiGridTrack> tracks,
        float[] sizes,
        int start,
        int span,
        float desired,
        float gap,
        bool growStars)
    {
        float existing = SpanSize(sizes, start, span, gap);
        float deficit = desired - existing;
        if (deficit <= 0f)
            return;

        int growable = 0;
        int end = Math.Min(tracks.Length, start + span);
        for (int i = start; i < end; i++)
        {
            if (tracks[i].Kind == UiGridTrackKind.Auto || (growStars && tracks[i].Kind == UiGridTrackKind.Star))
                growable++;
        }
        if (growable == 0)
            return;

        float share = deficit / growable;
        for (int i = start; i < end; i++)
        {
            UiGridTrack track = tracks[i];
            if (track.Kind == UiGridTrackKind.Auto || (growStars && track.Kind == UiGridTrackKind.Star))
                sizes[i] = ClampDimension(sizes[i] + share, track.Min, track.Max);
        }
    }

    private static void ResolveStars(ReadOnlySpan<UiGridTrack> tracks, float[] sizes, float available, float gap)
    {
        if (!float.IsFinite(available))
            return;
        float nonStar = GapTotal(gap, tracks.Length);
        float remainingWeight = 0f;
        int unresolved = 0;
        for (int i = 0; i < tracks.Length; i++)
        {
            if (tracks[i].Kind == UiGridTrackKind.Star)
            {
                remainingWeight += tracks[i].Value;
                unresolved++;
            }
            else
                nonStar += sizes[i];
        }
        if (remainingWeight <= 0f)
            return;

        float remainingSpace = available - nonStar;
        bool[] frozen = ArrayPool<bool>.Shared.Rent(tracks.Length);
        Array.Clear(frozen, 0, tracks.Length);
        try
        {
            while (unresolved > 0)
            {
                bool frozeAny = false;
                for (int i = 0; i < tracks.Length; i++)
                {
                    UiGridTrack track = tracks[i];
                    if (track.Kind != UiGridTrackKind.Star || frozen[i])
                        continue;

                    float tentative = remainingWeight > 0f
                        ? remainingSpace * track.Value / remainingWeight
                        : 0f;
                    float min = Math.Max(0f, float.IsNaN(track.Min) ? 0f : track.Min);
                    float max = float.IsNaN(track.Max) || track.Max < min ? min : track.Max;
                    if (tentative >= min && tentative <= max)
                        continue;

                    float fixedSize = tentative < min ? min : max;
                    sizes[i] = fixedSize;
                    frozen[i] = true;
                    unresolved--;
                    remainingSpace -= fixedSize;
                    remainingWeight -= track.Value;
                    frozeAny = true;
                }

                if (frozeAny)
                    continue;

                for (int i = 0; i < tracks.Length; i++)
                {
                    UiGridTrack track = tracks[i];
                    if (track.Kind == UiGridTrackKind.Star && !frozen[i])
                        sizes[i] = remainingWeight > 0f
                            ? remainingSpace * track.Value / remainingWeight
                            : 0f;
                }
                break;
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(frozen, clearArray: true);
        }
    }

    private static float GridMeasureConstraint(
        ReadOnlySpan<UiGridTrack> tracks,
        float[] sizes,
        int start,
        int span,
        float gap)
    {
        int end = Math.Min(tracks.Length, start + span);
        for (int i = start; i < end; i++)
        {
            if (tracks[i].Kind == UiGridTrackKind.Auto)
                return float.PositiveInfinity;
        }
        return SpanSize(sizes, start, span, gap);
    }

    private GridPlacement GetGridPlacement(UiEntity child, int columnCount, int rowCount)
    {
        GridPlacement placement = _world.Components.TryGet(child, out GridPlacement configured)
            ? configured
            : GridPlacement.Default;
        placement.Column = Math.Clamp(placement.Column, 0, Math.Max(0, columnCount - 1));
        placement.Row = Math.Clamp(placement.Row, 0, Math.Max(0, rowCount - 1));
        placement.ColumnSpan = Math.Clamp(placement.ColumnSpan <= 0 ? 1 : placement.ColumnSpan, 1, columnCount - placement.Column);
        placement.RowSpan = Math.Clamp(placement.RowSpan <= 0 ? 1 : placement.RowSpan, 1, rowCount - placement.Row);
        return placement;
    }

    private bool HasDirtyParent(UiEntity entity, UiDirtyFlags flag)
    {
        if (!_world.Hierarchy.TryGetNode(entity, out HierarchyNode node) || node.Parent == UiEntity.None)
            return false;
        return (_world.Dirty.GetFlags(node.Parent) & flag) != 0;
    }

    private UiSize GetMeasureAvailable(UiEntity entity)
    {
        if (IsHierarchyRoot(entity))
            return _viewport;
        if (_world.Components.TryGet(entity, out LayoutSlot slot))
            return new UiSize(slot.Value.Width, slot.Value.Height);
        return UiSize.Infinite;
    }

    private UiRect GetArrangeSlot(UiEntity entity)
    {
        if (IsHierarchyRoot(entity))
            return new UiRect(0f, 0f, _viewport.Width, _viewport.Height);
        if (_world.Components.TryGet(entity, out LayoutSlot slot))
            return slot.Value;
        UiSize desired = _world.Components.TryGet(entity, out DesiredSize size) ? size.Value : UiSize.Zero;
        return new UiRect(0f, 0f, desired.Width, desired.Height);
    }

    private bool IsHierarchyRoot(UiEntity entity) =>
        !_world.Hierarchy.TryGetNode(entity, out HierarchyNode node) || node.Parent == UiEntity.None;

    private UiEntity FirstChild(UiEntity entity) =>
        _world.Hierarchy.TryGetNode(entity, out HierarchyNode node) ? node.FirstChild : UiEntity.None;

    private UiEntity NextSibling(UiEntity entity) =>
        _world.Hierarchy.TryGetNode(entity, out HierarchyNode node) ? node.NextSibling : UiEntity.None;

    private LayoutStyle GetLayoutStyle(UiEntity entity) =>
        _world.Components.TryGet(entity, out LayoutStyle style) ? style : LayoutStyle.Default;

    private UiThickness GetPadding(UiEntity entity, in LayoutStyle layout)
    {
        UiThickness stylePadding = _world.Components.TryGet(entity, out ResolvedStyle style)
            ? style.Padding
            : UiThickness.Zero;
        return layout.Padding + stylePadding;
    }

    private static float ResolveMeasureConstraint(UiLength length, float available, float padding)
    {
        return length.Kind switch
        {
            UiLengthKind.Pixels => Math.Max(0f, length.Value),
            UiLengthKind.Percent when float.IsFinite(available) => Math.Max(0f, available * length.Value),
            UiLengthKind.MinContent or UiLengthKind.MaxContent => throw UnsupportedIntrinsicLength(length.Kind),
            _ => available
        };
    }

    private static float ResolveDesired(UiLength length, float natural, float available)
    {
        return length.Kind switch
        {
            UiLengthKind.Pixels => Math.Max(0f, length.Value),
            UiLengthKind.Percent when float.IsFinite(available) => Math.Max(0f, available * length.Value),
            UiLengthKind.MinContent or UiLengthKind.MaxContent => throw UnsupportedIntrinsicLength(length.Kind),
            _ => natural
        };
    }

    private static float ResolveArrangeDimension(UiLength length, bool stretch, float available, float desired)
    {
        return length.Kind switch
        {
            UiLengthKind.Pixels => Math.Max(0f, length.Value),
            UiLengthKind.Percent => Math.Max(0f, available * length.Value),
            UiLengthKind.MinContent or UiLengthKind.MaxContent => throw UnsupportedIntrinsicLength(length.Kind),
            _ when stretch => available,
            _ => desired
        };
    }

    private static NotSupportedException UnsupportedIntrinsicLength(UiLengthKind kind) =>
        new($"{kind} intrinsic layout semantics are not implemented.");

    private static float AlignHorizontal(float start, float available, float size, UiHorizontalAlignment alignment) =>
        alignment switch
        {
            UiHorizontalAlignment.Center => start + (available - size) * 0.5f,
            UiHorizontalAlignment.End => start + available - size,
            _ => start
        };

    private static float AlignVertical(float start, float available, float size, UiVerticalAlignment alignment) =>
        alignment switch
        {
            UiVerticalAlignment.Center => start + (available - size) * 0.5f,
            UiVerticalAlignment.End => start + available - size,
            _ => start
        };

    private static float ClampDimension(float value, float min, float max)
    {
        min = Math.Max(0f, float.IsNaN(min) ? 0f : min);
        max = float.IsNaN(max) || max < min ? min : max;
        return Math.Clamp(Math.Max(0f, float.IsNaN(value) ? 0f : value), min, max);
    }

    private static float SubtractFinite(float value, float amount) =>
        float.IsFinite(value) ? Math.Max(0f, value - amount) : float.PositiveInfinity;

    private static UiSize NormalizeAvailable(UiSize value) => new(
        float.IsNaN(value.Width) || value.Width < 0f ? 0f : value.Width,
        float.IsNaN(value.Height) || value.Height < 0f ? 0f : value.Height);

    private static UiSize NormalizeViewport(UiSize value) => new(
        float.IsFinite(value.Width) ? Math.Max(0f, value.Width) : 0f,
        float.IsFinite(value.Height) ? Math.Max(0f, value.Height) : 0f);

    private static float Sum(float[] values, int count)
    {
        float total = 0f;
        for (int i = 0; i < count; i++) total += values[i];
        return total;
    }

    private static float GapTotal(float gap, int count) => Math.Max(0f, gap) * Math.Max(0, count - 1);

    private static float Prefix(float[] values, int count, float gap)
    {
        float total = Math.Max(0f, gap) * count;
        for (int i = 0; i < count; i++) total += values[i];
        return total;
    }

    private static float SpanSize(float[] values, int start, int span, float gap)
    {
        float total = GapTotal(gap, span);
        int end = Math.Min(values.Length, start + span);
        for (int i = start; i < end; i++) total += values[i];
        return total;
    }
}

public sealed class LayoutMeasureSystem : IUiSystem
{
    private readonly LayoutEngine _engine;

    public LayoutMeasureSystem(LayoutEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public UiSystemPhase Phase => UiSystemPhase.LayoutMeasure;
    public string Name => "layout.measure";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = world;
        _ = frame;
        _engine.Measure();
    }
}

public sealed class LayoutArrangeSystem : IUiSystem
{
    private readonly LayoutEngine _engine;

    public LayoutArrangeSystem(LayoutEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public UiSystemPhase Phase => UiSystemPhase.LayoutArrange;
    public string Name => "layout.arrange";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = world;
        _ = frame;
        _engine.Arrange();
    }
}
