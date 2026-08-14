// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;

namespace PCL.UI.Next;

/// <summary>Incrementally resolves ECS visual state into retained scene mutations.</summary>
public sealed class RenderDiffSystem : IUiSystem, IDisposable
{
    private readonly UiWorld _world;
    private readonly UiScopeId _rootScope;
    private readonly List<RenderMutation> _mutations = [];
    private readonly List<UiEntity> _dirty = [];
    private readonly List<UiEntity> _roots = [];
    private readonly HashSet<UiEntity> _rootSet = [];
    private readonly HashSet<UiEntity> _retained = [];
    private uint _structuralVersion;
    private bool _initialized;
    private bool _disposed;

    public RenderDiffSystem(UiWorld world, RenderScene scene, UiScopeId rootScope)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        if (!world.Scopes.IsAlive(rootScope))
            throw new InvalidOperationException("Render root scope is not alive: " + rootScope);
        _rootScope = rootScope;
        _world.EntityDestroying += OnEntityDestroying;
    }

    public UiSystemPhase Phase => UiSystemPhase.RenderDiff;

    public string Name => "render.diff";

    public RenderScene Scene { get; }

    public UiCommitBatch? PendingBatch { get; private set; }

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PendingBatch is not null)
            throw new InvalidOperationException("The previous render batch was not committed.");

        bool structureChanged = !_initialized || _structuralVersion != world.Hierarchy.StructuralVersion;
        if (structureChanged)
            ReconcileScene(world);
        else
            ReconcileDirty(world);

        _structuralVersion = world.Hierarchy.StructuralVersion;
        _initialized = true;
        if (_mutations.Count > 0)
            PendingBatch = new UiCommitBatch(frame.FrameIndex, _mutations.ToArray(), takeOwnership: true);
        _mutations.Clear();
    }

    public void MarkCommitted()
    {
        if (PendingBatch is null)
            throw new InvalidOperationException("There is no pending render batch.");
        Scene.CompleteCommit();
        PendingBatch = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.EntityDestroying -= OnEntityDestroying;
        _mutations.Clear();
        PendingBatch = null;
        _disposed = true;
    }

    private void ReconcileScene(UiWorld world)
    {
        _roots.Clear();
        _rootSet.Clear();
        _retained.Clear();

        ReadOnlySpan<UiEntity> entities = world.Components.Pool<NodeKindComponent>().Entities;
        for (int i = 0; i < entities.Length; i++)
        {
            UiEntity entity = entities[i];
            if (!world.Entities.IsAlive(entity) || !IsInRenderRoot(world, entity))
                continue;
            UiEntity root = FindRenderRoot(world, entity);
            if (_rootSet.Add(root))
                _roots.Add(root);
        }

        _roots.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        for (int i = 0; i < _roots.Count; i++)
            ReconcileSubtree(world, _roots[i], RenderNodeId.None, i);

        Scene.RemoveMissing(_retained, _mutations);
    }

    private void ReconcileSubtree(
        UiWorld world,
        UiEntity entity,
        RenderNodeId renderParent,
        int siblingOrder)
    {
        if (!world.Entities.IsAlive(entity))
            return;

        RenderNodeId descendantParent = renderParent;
        int descendantOrder = siblingOrder;
        if (world.Components.TryGet(entity, out NodeKindComponent kind))
        {
            RenderNodeState desired = ResolveState(
                world,
                entity,
                kind.Kind,
                renderParent,
                ComposeZOrder(world, entity, siblingOrder));
            descendantParent = Scene.Apply(in desired, _mutations);
            descendantOrder = 0;
            _retained.Add(entity);
        }
        world.Dirty.Clear(entity, UiDirtyFlags.Render);

        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node))
            return;
        UiEntity child = node.FirstChild;
        while (child != UiEntity.None)
        {
            UiEntity next = world.Hierarchy.TryGetNode(child, out HierarchyNode childNode)
                ? childNode.NextSibling
                : UiEntity.None;
            ReconcileSubtree(world, child, descendantParent, descendantOrder++);
            child = next;
        }
    }

    private void ReconcileDirty(UiWorld world)
    {
        _dirty.Clear();
        world.Dirty.Collect(UiDirtyFlags.Render, _dirty);
        if (HasRenderTopologyMismatch(world))
        {
            ReconcileScene(world);
            return;
        }

        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!world.Entities.IsAlive(entity) || !IsInRenderRoot(world, entity))
                continue;
            if (!world.Components.TryGet(entity, out NodeKindComponent kind))
            {
                Scene.RemoveEntity(entity, _mutations);
                world.Dirty.Clear(entity, UiDirtyFlags.Render);
                continue;
            }

            RenderNodeId parent = FindRenderParent(world, entity);
            long zOrder = ComposeZOrder(world, entity, FindSiblingOrder(world, entity));
            RenderNodeState desired = ResolveState(world, entity, kind.Kind, parent, zOrder);
            Scene.Apply(in desired, _mutations);
            world.Dirty.Clear(entity, UiDirtyFlags.Render);
        }
    }

    private bool HasRenderTopologyMismatch(UiWorld world)
    {
        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!world.Entities.IsAlive(entity) || !IsInRenderRoot(world, entity))
                continue;
            bool hasComponent = world.Components.Has<NodeKindComponent>(entity);
            bool hasRetainedNode = Scene.TryGetNode(entity, out _);
            if (hasComponent != hasRetainedNode)
                return true;
        }
        return false;
    }

    private void OnEntityDestroying(UiEntity entity) =>
        Scene.RemoveEntity(entity, _mutations);

    private RenderNodeId FindRenderParent(UiWorld world, UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (world.Hierarchy.TryGetNode(current, out HierarchyNode node) &&
               node.Parent != UiEntity.None &&
               guard++ < 1_000_000)
        {
            current = node.Parent;
            if (Scene.TryGetNode(current, out RenderNodeId parent))
                return parent;
        }
        return RenderNodeId.None;
    }

    private static UiEntity FindRenderRoot(UiWorld world, UiEntity entity)
    {
        UiEntity root = entity;
        UiEntity current = entity;
        int guard = 0;
        while (world.Hierarchy.TryGetNode(current, out HierarchyNode node) &&
               node.Parent != UiEntity.None &&
               guard++ < 1_000_000)
        {
            current = node.Parent;
            if (world.Components.Has<NodeKindComponent>(current))
                root = current;
        }
        return root;
    }

    private bool IsInRenderRoot(UiWorld world, UiEntity entity)
    {
        if (!world.Entities.TryGetScope(entity, out UiScopeId scope))
            return false;
        UiScopeId current = scope;
        int guard = 0;
        while (world.Scopes.IsAlive(current) && guard++ < 1_000_000)
        {
            if (current == _rootScope)
                return true;
            if (!world.Scopes.TryGetParent(current, out current) || current.IsNone)
                break;
        }
        return false;
    }

    private static int FindSiblingOrder(UiWorld world, UiEntity entity)
    {
        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node))
            return entity.Index;
        int order = 0;
        UiEntity previous = node.PreviousSibling;
        while (previous != UiEntity.None && order++ < 1_000_000)
        {
            previous = world.Hierarchy.TryGetNode(previous, out HierarchyNode previousNode)
                ? previousNode.PreviousSibling
                : UiEntity.None;
        }
        return order;
    }

    private static long ComposeZOrder(UiWorld world, UiEntity entity, int siblingOrder)
    {
        int zIndex = world.Components.TryGet(entity, out HitTestableComponent hitTestable)
            ? hitTestable.ZIndex
            : 0;
        return ((long)zIndex << 32) | (uint)siblingOrder;
    }

    private static RenderNodeState ResolveState(
        UiWorld world,
        UiEntity entity,
        UiNodeKind nodeKind,
        RenderNodeId parent,
        long zOrder)
    {
        ResolvedStyle target = world.Components.TryGet(entity, out ResolvedStyle resolved)
            ? resolved
            : ResolvedStyle.Default;
        ComputedVisual visual = world.Components.TryGet(entity, out ComputedVisual computed)
            ? computed
            : ComputedVisual.FromResolved(in target);
        bool visible = !world.Components.TryGet(entity, out HitTestableComponent hitTestable) ||
                       hitTestable.IsVisible;
        bool isText = nodeKind == UiNodeKind.Text;
        bool isClip = nodeKind is UiNodeKind.Scroll or UiNodeKind.VirtualList;
        bool isNativeHost = nodeKind == UiNodeKind.NativeHost;
        bool realized = !world.Components.TryGet(entity, out VirtualItemSlot virtualSlot) || virtualSlot.IsRealized;
        TextLayoutHandle textLayout = TextLayoutHandle.None;
        TextCacheEntryHandle textCacheEntry = TextCacheEntryHandle.None;
        if (isText && world.Components.TryGet(entity, out TextLayout text))
        {
            textLayout = text.Handle;
            textCacheEntry = text.CacheEntry;
        }

        return new RenderNodeState
        {
            Owner = entity,
            Kind = isText
                ? UiRenderNodeKind.Text
                : isClip
                    ? UiRenderNodeKind.Clip
                    : isNativeHost
                        ? UiRenderNodeKind.NativeHostPlaceholder
                        : UiRenderNodeKind.RoundedRectangle,
            Parent = parent,
            ZOrder = zOrder,
            Bounds = world.Components.TryGet(entity, out LayoutRect layout)
                ? layout.Value
                : UiRect.Empty,
            Transform = world.Components.Has<LayoutRect>(entity)
                ? UiTransformMath.CreateLocalTransform(world, entity)
                : Matrix3x2.Identity,
            Opacity = visible && realized ? Math.Clamp(visual.Opacity, 0f, 1f) : 0f,
            Brush = isText ? visual.Foreground : visual.Background,
            CornerRadius = Math.Max(0f, visual.CornerRadius),
            TextLayout = textLayout,
            TextCacheEntry = textCacheEntry
        };
    }
}

internal sealed class BackendCommitSystem(
    RenderDiffSystem diff,
    IUiBackend backend) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.BackendCommit;

    public string Name => "render.commit";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = world;
        _ = frame;
        if (diff.PendingBatch is not { } batch)
            return;
        backend.Commit(in batch);
        diff.MarkCommitted();
        backend.RequestFrame();
    }
}
