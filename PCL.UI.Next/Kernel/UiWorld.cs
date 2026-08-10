// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Phase 1 ECS world: registry, scopes, hierarchy, components, dirty sets, queues, pipeline.
/// Single-owner-thread write model (architecture ADR-010).
/// </summary>
public sealed class UiWorld
{
    private readonly List<UiEntity> _scopeEntityScratch = [];
    private long _frameIndex;
    private UiTimestamp _lastFrameTime = UiTimestamp.Zero;
    private bool _hasLastFrameTime;

    public UiWorld(IUiClock? clock = null)
    {
        Clock = clock ?? new StopwatchUiClock();
        Entities = new EntityRegistry();
        Scopes = new ScopeRegistry();
        Hierarchy = new HierarchyStore(Entities);
        Components = new ComponentStore();
        Dirty = new DirtyTracker(Entities);
        Events = new EventQueue();
        Patches = new StatePatchQueue();
        Systems = new SystemPipeline();
        Scheduler = new UiFrameScheduler();
    }

    public IUiClock Clock { get; }

    public EntityRegistry Entities { get; }

    public ScopeRegistry Scopes { get; }

    public HierarchyStore Hierarchy { get; }

    public ComponentStore Components { get; }

    public DirtyTracker Dirty { get; }

    public EventQueue Events { get; }

    public StatePatchQueue Patches { get; }

    public SystemPipeline Systems { get; }

    public UiFrameScheduler Scheduler { get; }

    public long FrameIndex => _frameIndex;

    public UiScopeId CreateRootScope() => Scopes.CreateRoot();

    public UiScopeId CreateScope(UiScopeId parent) => Scopes.Create(parent);

    public UiEntity CreateEntity(UiScopeId scope, bool asHierarchyRoot = true)
    {
        if (!Scopes.IsAlive(scope))
            throw new InvalidOperationException("Scope is not alive: " + scope);

        UiEntity entity = Entities.Create(scope);
        if (asHierarchyRoot)
            Hierarchy.EnsureRoot(entity);
        Dirty.Mark(entity, UiDirtyFlags.StructuralCascade);
        Scheduler.RequestReactiveFrame();
        return entity;
    }

    public void AttachChild(UiEntity parent, UiEntity child)
    {
        Hierarchy.AttachChild(parent, child);
        Dirty.Mark(parent, UiDirtyFlags.StructuralCascade);
        Dirty.Mark(child, UiDirtyFlags.StructuralCascade);
        Scheduler.RequestReactiveFrame();
    }

    public void DestroyEntity(UiEntity entity)
    {
        if (!Entities.IsAlive(entity))
            return;

        Hierarchy.DestroySubtree(entity, DestroyEntityLeaf);
        Scheduler.RequestReactiveFrame();
    }

    public bool DisposeScope(UiScopeId scope)
    {
        if (!Scopes.IsAlive(scope))
            return false;

        return Scopes.Dispose(scope, disposed =>
        {
            _scopeEntityScratch.Clear();
            Entities.AppendAliveInScope(disposed, _scopeEntityScratch);
            // Destroy roots only — subtree walk handles descendants that share the scope.
            for (int i = 0; i < _scopeEntityScratch.Count; i++)
            {
                UiEntity entity = _scopeEntityScratch[i];
                if (!Entities.IsAlive(entity))
                    continue;
                if (Hierarchy.TryGetNode(entity, out HierarchyNode node) && node.Parent != UiEntity.None)
                {
                    // Child of another entity still alive in same scope — destroy via root later.
                    if (Entities.IsAlive(node.Parent) &&
                        Entities.TryGetScope(node.Parent, out UiScopeId parentScope) &&
                        parentScope == disposed)
                    {
                        continue;
                    }
                }

                DestroyEntity(entity);
            }
        });
    }

    public void EnqueuePlatformEvent(in UiPlatformEvent platformEvent)
    {
        Events.Enqueue(in platformEvent);
        Scheduler.RequestReactiveFrame();
    }

    public void EnqueueStatePatch(in UiStatePatch patch)
    {
        Patches.Enqueue(in patch);
        Scheduler.RequestReactiveFrame();
    }

    /// <summary>
    /// Runs one frame when the scheduler needs work (or <paramref name="force"/> is true).
    /// Returns false when idle and not forced.
    /// </summary>
    public bool Update(bool force = false)
    {
        if (!force && !Scheduler.NeedsFrame)
            return false;

        UiTimestamp now = Clock.Now;
        double delta = 0d;
        if (_hasLastFrameTime)
            delta = Math.Max(0d, now.SecondsSince(_lastFrameTime));
        _lastFrameTime = now;
        _hasLastFrameTime = true;
        _frameIndex++;

        UiFrameContext frame = new(_frameIndex, delta, now);
        Systems.Run(this, in frame);
        Scheduler.AcknowledgeReactiveFrame();
        return true;
    }

    private void DestroyEntityLeaf(UiEntity entity)
    {
        if (!Entities.IsAlive(entity))
            return;
        Components.RemoveAll(entity);
        Dirty.RemoveEntity(entity);
        Hierarchy.RemoveNode(entity);
        Entities.Destroy(entity);
    }
}
