// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.DevTools;

/// <summary>Read-only inspector facade over Runtime-owned state.</summary>
public sealed class UiInspector
{
    private readonly UiWorld _world;
    private readonly UiInteractiveRuntime? _interactive;
    private readonly UiRenderingRuntime? _rendering;
    private readonly List<Type> _componentTypes = [];
    private readonly List<UiEntity> _entities = [];
    private readonly List<UiAnimationSnapshot> _animations = [];

    public UiInspector(
        UiWorld world,
        UiInteractiveRuntime? interactive = null,
        UiRenderingRuntime? rendering = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        if (interactive is not null && !ReferenceEquals(interactive.World, world))
            throw new InvalidOperationException("Inspector and interactive runtime must use the same world.");
        if (rendering is not null && !ReferenceEquals(rendering.World, world))
            throw new InvalidOperationException("Inspector and rendering runtime must use the same world.");
        _interactive = interactive;
        _rendering = rendering;
    }

    public bool TryInspectEntity(UiEntity entity, out UiEntityInspection inspection)
    {
        if (!_world.Entities.TryGetScope(entity, out UiScopeId scope))
        {
            inspection = null!;
            return false;
        }

        _componentTypes.Clear();
        _world.Components.CopyComponentTypes(entity, _componentTypes);
        _componentTypes.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.FullName, right.FullName));
        string[] componentNames = new string[_componentTypes.Count];
        for (int i = 0; i < _componentTypes.Count; i++)
            componentNames[i] = _componentTypes[i].Name;

        _entities.Clear();
        _world.Hierarchy.EnumerateChildren(entity, _entities);
        UiEntity[] children = _entities.ToArray();
        HierarchyNode hierarchy = _world.Hierarchy.TryGetNode(entity, out HierarchyNode node)
            ? node
            : default;
        inspection = new UiEntityInspection(
            entity,
            scope,
            hierarchy.Parent,
            hierarchy.Depth,
            _world.Dirty.GetFlags(entity),
            children,
            componentNames);
        return true;
    }

    public bool TryInspectLayout(UiEntity entity, out UiLayoutInspection inspection)
    {
        if (!_world.Entities.IsAlive(entity))
        {
            inspection = default;
            return false;
        }

        UiEntity parent = _world.Hierarchy.TryGetNode(entity, out HierarchyNode hierarchy)
            ? hierarchy.Parent
            : UiEntity.None;
        UiSize? desired = _world.Components.TryGet(entity, out DesiredSize desiredSize)
            ? desiredSize.Value
            : null;
        UiRect? rect = _world.Components.TryGet(entity, out LayoutRect layoutRect)
            ? layoutRect.Value
            : null;
        UiSize? constraint = _interactive is not null &&
                             _interactive.Layout.TryGetLastMeasureConstraint(entity, out UiSize available)
            ? available
            : null;
        bool boundary = _world.Components.TryGet(entity, out LayoutStyle style) && style.IsMeasureBoundary;
        inspection = new UiLayoutInspection(
            entity,
            parent,
            desired,
            rect,
            constraint,
            boundary,
            _world.Dirty.GetFlags(entity));
        return true;
    }

    public bool TryInspectRender(UiEntity entity, out UiRenderNodeSnapshot snapshot)
    {
        if (_rendering is null || !_rendering.Scene.TryGetNode(entity, out RenderNodeId node))
        {
            snapshot = default;
            return false;
        }
        return _rendering.Scene.TryGetNode(node, out snapshot);
    }

    public bool TryInspectInteraction(
        UiEntity entity,
        out UiInteractionInspection inspection,
        int pointerId = 0)
    {
        if (_interactive is null ||
            !_world.Entities.IsAlive(entity) ||
            !_interactive.Input.InputRoots.TryResolve(entity, out UiInputRootId inputRoot))
        {
            inspection = null!;
            return false;
        }

        UiRect? hitBounds = null;
        IReadOnlyList<UiHitTestEntry> hitEntries = _interactive.Input.HitTest.Entries;
        for (int i = 0; i < hitEntries.Count; i++)
        {
            if (hitEntries[i].Entity == entity)
            {
                hitBounds = hitEntries[i].Bounds;
                break;
            }
        }

        _entities.Clear();
        UiEntity current = entity;
        int guard = 0;
        while (_world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            _entities.Add(current);
            if (!_world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent.IsNone)
                break;
            current = node.Parent;
        }

        inspection = new UiInteractionInspection(
            entity,
            inputRoot,
            hitBounds,
            _interactive.Input.Focus.GetFocused(inputRoot),
            _interactive.Input.GetHovered(inputRoot, pointerId),
            _interactive.Input.GetPressed(inputRoot, pointerId),
            _interactive.Input.PointerCapture.GetCaptured(inputRoot, pointerId),
            _entities.ToArray());
        return true;
    }

    public int CopyAnimations(UiEntity entity, List<UiAnimationSnapshot> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_interactive is null || !_world.Entities.IsAlive(entity))
            return 0;
        _animations.Clear();
        _interactive.Animation.CopySnapshotsTo(_animations);
        int count = 0;
        for (int i = 0; i < _animations.Count; i++)
        {
            if (_animations[i].Entity != entity)
                continue;
            destination.Add(_animations[i]);
            count++;
        }
        return count;
    }

    public bool TryInspectVirtualization(UiEntity entity, out UiVirtualizationSnapshot snapshot)
    {
        if (_interactive is not null)
            return _interactive.Virtualization.TryGetSnapshot(entity, out snapshot);
        snapshot = default;
        return false;
    }

    public int CopyDirtyTrace(UiEntity entity, List<UiDiagnosticEvent> destination)
    {
        return CopyDirtyTrace(entity, destination, out _);
    }

    public int CopyDirtyTrace(
        UiEntity entity,
        List<UiDiagnosticEvent> destination,
        out long droppedCount)
    {
        ArgumentNullException.ThrowIfNull(destination);
        UiDiagnosticEventReader reader = _world.Diagnostics.Events.CreateReader();
        int count = 0;
        while (reader.TryRead(out UiDiagnosticEvent diagnosticEvent))
        {
            if (diagnosticEvent.Kind != UiDiagnosticEventKind.DirtyMarked ||
                (diagnosticEvent.Entity != entity && diagnosticEvent.RelatedEntity != entity))
            {
                continue;
            }
            destination.Add(diagnosticEvent);
            count++;
        }
        droppedCount = reader.DroppedCount;
        return count;
    }

    public void CopyTimelinesTo(List<UiFrameTimeline> destination) =>
        _world.Diagnostics.CopyTimelinesTo(destination);
}
