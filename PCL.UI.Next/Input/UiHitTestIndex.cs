// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;

namespace PCL.UI.Next;

public readonly record struct UiHitTestEntry(
    UiEntity Entity,
    UiRect Bounds,
    int ZIndex,
    int RenderOrder);

/// <summary>
/// Retained first-version hit-test structure: hierarchy render order plus explicit Z index.
/// It rebuilds only when structure or hit-test dirtiness changes; querying is allocation-free.
/// </summary>
public sealed class UiHitTestIndex
{
    private readonly UiWorld _world;
    private readonly UiInputRootRegistry _inputRoots;
    private readonly List<UiHitTestEntry> _entries = [];
    private readonly List<UiEntity> _dirty = [];
    private readonly List<UiEntity> _roots = [];
    private readonly HashSet<UiEntity> _rootSet = [];
    private uint _structuralVersion;
    private bool _initialized;
    private int _renderOrder;

    public UiHitTestIndex(UiWorld world, UiInputRootRegistry inputRoots)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _inputRoots = inputRoots ?? throw new ArgumentNullException(nameof(inputRoots));
    }

    public IReadOnlyList<UiHitTestEntry> Entries => _entries;

    public UiEntity HitTest(UiPoint point, UiInputRootId inputRoot)
    {
        if (!_inputRoots.IsAlive(inputRoot))
            return UiEntity.None;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            UiHitTestEntry entry = _entries[i];
            if (!_world.Entities.IsAlive(entry.Entity) || !entry.Bounds.Contains(point))
                continue;
            if (!_inputRoots.Contains(inputRoot, entry.Entity))
                continue;
            if (!_world.Components.TryGet(entry.Entity, out HitTestableComponent hitTestable) ||
                !hitTestable.IsVisible || !hitTestable.IsEnabled)
            {
                continue;
            }

            if (_world.Components.TryGet(entry.Entity, out InteractionStateComponent state) &&
                (state.Value & InteractionState.Disabled) != 0)
            {
                continue;
            }

            if (_world.Components.TryGet(entry.Entity, out ComputedTransform transform))
            {
                if (!Matrix3x2.Invert(transform.Value, out Matrix3x2 inverse) ||
                    !_world.Components.TryGet(entry.Entity, out LayoutRect layout))
                {
                    continue;
                }

                Vector2 local = Vector2.Transform(new Vector2(point.X, point.Y), inverse);
                if (!layout.Value.Contains(new UiPoint(local.X, local.Y)))
                    continue;
            }

            return entry.Entity;
        }

        return UiEntity.None;
    }

    public bool TryGetRenderOrder(UiEntity entity, out int renderOrder)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Entity != entity)
                continue;
            renderOrder = _entries[i].RenderOrder;
            return true;
        }

        renderOrder = 0;
        return false;
    }

    public bool Update()
    {
        _dirty.Clear();
        _world.Dirty.Collect(UiDirtyFlags.HitTest, _dirty);
        bool changed = !_initialized ||
                       _structuralVersion != _world.Hierarchy.StructuralVersion ||
                       _dirty.Count > 0;
        if (changed)
            Rebuild();

        for (int i = 0; i < _dirty.Count; i++)
            _world.Dirty.Clear(_dirty[i], UiDirtyFlags.HitTest);
        return changed;
    }

    private void Rebuild()
    {
        _entries.Clear();
        _roots.Clear();
        _rootSet.Clear();
        _renderOrder = 0;

        ReadOnlySpan<UiEntity> entities = _world.Components.Pool<HitTestableComponent>().Entities;
        for (int i = 0; i < entities.Length; i++)
        {
            UiEntity root = FindHierarchyRoot(entities[i]);
            if (_rootSet.Add(root))
                _roots.Add(root);
        }

        _roots.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        for (int i = 0; i < _roots.Count; i++)
            AppendSubtree(_roots[i]);

        _entries.Sort(static (left, right) =>
        {
            int z = left.ZIndex.CompareTo(right.ZIndex);
            return z != 0 ? z : left.RenderOrder.CompareTo(right.RenderOrder);
        });
        _structuralVersion = _world.Hierarchy.StructuralVersion;
        _initialized = true;
    }

    private void AppendSubtree(UiEntity entity)
    {
        if (!_world.Entities.IsAlive(entity))
            return;

        int order = _renderOrder++;
        if (_world.Components.TryGet(entity, out HitTestableComponent hitTestable) &&
            hitTestable.IsVisible &&
            _world.Components.TryGet(entity, out LayoutRect layout))
        {
            UiRect bounds = _world.Components.TryGet(entity, out ComputedTransform transform)
                ? TransformBounds(layout.Value, transform.Value)
                : layout.Value;
            _entries.Add(new UiHitTestEntry(entity, bounds, hitTestable.ZIndex, order));
        }

        if (!_world.Hierarchy.TryGetNode(entity, out HierarchyNode node))
            return;
        UiEntity child = node.FirstChild;
        while (child != UiEntity.None)
        {
            UiEntity next = _world.Hierarchy.TryGetNode(child, out HierarchyNode childNode)
                ? childNode.NextSibling
                : UiEntity.None;
            AppendSubtree(child);
            child = next;
        }
    }

    private UiEntity FindHierarchyRoot(UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (_world.Hierarchy.TryGetNode(current, out HierarchyNode node) &&
               node.Parent != UiEntity.None &&
               guard++ < 1_000_000)
        {
            current = node.Parent;
        }

        return current;
    }

    private static UiRect TransformBounds(UiRect rect, Matrix3x2 transform)
    {
        Vector2 topLeft = Vector2.Transform(new Vector2(rect.X, rect.Y), transform);
        Vector2 topRight = Vector2.Transform(new Vector2(rect.Right, rect.Y), transform);
        Vector2 bottomLeft = Vector2.Transform(new Vector2(rect.X, rect.Bottom), transform);
        Vector2 bottomRight = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), transform);
        float left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
        float top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
        float right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
        float bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));
        return new UiRect(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
    }

}
