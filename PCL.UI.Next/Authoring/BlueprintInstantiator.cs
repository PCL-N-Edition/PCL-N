// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Instantiates blueprints into a <see cref="UiWorld"/> and applies reactive bindings /
/// structural reconcile without rebuilding the whole tree.
/// </summary>
public sealed class BlueprintInstantiator
{
    private readonly UiWorld _world;
    private readonly PresentationStore _store;
    private int _nextInstanceId = 1;
    private readonly List<BlueprintInstance> _instances = [];
    private readonly HashSet<int> _candidateBindings = [];
    private readonly List<int> _structuralWorkQueue = [];
    /// <summary>Nodes currently pending in the work queue (not "already processed this pass").</summary>
    private readonly HashSet<int> _structuralWorkPending = [];
    private readonly List<int> _remountedNodes = [];

    public BlueprintInstantiator(
        UiWorld world,
        PresentationStore store,
        bool registerPipelineSystem = true)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        // State changes request a reactive frame so BindingUpdate system can run.
        _store.SliceChanged += _ => _world.Scheduler.RequestReactiveFrame();

        if (registerPipelineSystem)
            _world.Systems.Register(new BlueprintRuntimeSystem(this));
    }

    public IReadOnlyList<BlueprintInstance> Instances => _instances;

    public PresentationStore Store => _store;

    public BlueprintInstance Instantiate(UiBlueprint blueprint, UiScopeId scope)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        if (!_world.Scopes.IsAlive(scope))
            throw new InvalidOperationException("Scope is not alive: " + scope);

        int instanceId = _nextInstanceId++;
        UiEntity[] map = new UiEntity[blueprint.NodeCount];
        Array.Fill(map, UiEntity.None);

        MountNode(blueprint, blueprint.RootIndex, parentEntity: UiEntity.None, instanceId, map, scope);

        BindingStamp[] stamps = new BindingStamp[blueprint.BindingCount];
        Dictionary<int, ulong> sliceVersions = [];
        foreach (int slice in blueprint.DependencyIndex.AllSlices)
            sliceVersions[slice] = _store.Version(slice);

        var instance = new BlueprintInstance(instanceId, blueprint, scope, map, stamps, sliceVersions);

        // Immediate scope ownership — not lazy prune on UpdateAll.
        IDisposable registration = _world.Scopes.RegisterDisposeHandler(scope, OnScopeDisposed);
        instance.AttachScopeRegistration(registration);

        _instances.Add(instance);

        ReconcileStructural(instance, force: true, _remountedNodes);
        ApplyBindings(instance, force: true, remountedNodes: null);
        return instance;
    }

    public void Destroy(BlueprintInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!instance.IsAlive)
        {
            _instances.Remove(instance);
            return;
        }

        if (instance.RootEntity != UiEntity.None && _world.Entities.IsAlive(instance.RootEntity))
            _world.DestroyEntity(instance.RootEntity);
        InvalidateInstance(instance);
        _instances.Remove(instance);
    }

    /// <summary>
    /// Reactive update: structural reconcile may remount entities, then bindings
    /// re-apply only for affected dependency slices / remounted nodes.
    /// </summary>
    public void Update(BlueprintInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!instance.IsAlive)
            return;

        if (!_world.Scopes.IsAlive(instance.Scope))
        {
            // Should already have been unregistered via dispose handler.
            InvalidateInstance(instance);
            _instances.Remove(instance);
            return;
        }

        _remountedNodes.Clear();
        ReconcileStructural(instance, force: false, _remountedNodes);
        ApplyBindings(instance, force: false, _remountedNodes);
    }

    public void UpdateAll()
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            if (i >= _instances.Count)
                continue;
            Update(_instances[i]);
        }
    }

    private void OnScopeDisposed(UiScopeId scope)
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            BlueprintInstance instance = _instances[i];
            if (instance.Scope != scope)
                continue;
            // Entities already destroyed by UiWorld.DisposeScope callback chain.
            InvalidateInstance(instance);
            _instances.RemoveAt(i);
        }
    }

    private static void InvalidateInstance(BlueprintInstance instance)
    {
        instance.IsAlive = false;
        instance.DetachScopeRegistration();
        for (int i = 0; i < instance.EntitiesByNode.Length; i++)
            instance.EntitiesByNode[i] = UiEntity.None;
        for (int i = 0; i < instance.BindingStamps.Length; i++)
            instance.BindingStamps[i] = BindingStamp.None;
        instance.SliceVersions.Clear();
    }

    private void MountNode(
        UiBlueprint blueprint,
        int nodeIndex,
        UiEntity parentEntity,
        int instanceId,
        UiEntity[] map,
        UiScopeId scope)
    {
        if (nodeIndex < 0)
            return;

        BlueprintNode node = blueprint.NodesCore[nodeIndex];
        if (node.Kind == UiNodeKind.If)
        {
            CreateEntityForNode(blueprint, nodeIndex, instanceId, map, scope, parentEntity);
            return;
        }

        UiEntity entity = CreateEntityForNode(blueprint, nodeIndex, instanceId, map, scope, parentEntity);

        int child = node.FirstChildIndex;
        while (child >= 0)
        {
            MountNode(blueprint, child, entity, instanceId, map, scope);
            child = blueprint.NodesCore[child].NextSiblingIndex;
        }
    }

    private UiEntity CreateEntityForNode(
        UiBlueprint blueprint,
        int nodeIndex,
        int instanceId,
        UiEntity[] map,
        UiScopeId scope,
        UiEntity parentEntity)
    {
        BlueprintNode node = blueprint.NodesCore[nodeIndex];
        bool asRoot = parentEntity == UiEntity.None;
        UiEntity entity = _world.CreateEntity(scope, asHierarchyRoot: asRoot);
        if (!asRoot)
            _world.AttachChild(parentEntity, entity);

        map[nodeIndex] = entity;

        _world.Set(entity, new BlueprintNodeRef
        {
            InstanceId = instanceId,
            NodeIndex = nodeIndex
        });
        _world.Set(entity, new NodeKindComponent { Kind = node.Kind });
        _world.Set(entity, node.Layout);
        ApplyLayoutComponents(entity, in node);
        if (node.StyleClassIds.Length > 0)
            _world.Set(entity, StyleClassSet.From(node.StyleClassIds));
        if (node.Behaviors != UiBehavior.None)
            _world.Set(entity, new BehaviorComponent { Flags = node.Behaviors });
        if (node.IsHitTestVisible)
            _world.Set(entity, HitTestableComponent.Default);
        if ((node.Behaviors & UiBehavior.Focusable) != 0)
        {
            _world.Set(entity, new FocusableComponent
            {
                TabIndex = node.TabIndex,
                IsTabStop = true
            });
        }
        if (node.IsFocusScope)
        {
            _world.Set(entity, new FocusScopeComponent
            {
                IsTrap = node.IsFocusTrap,
                RestorePreviousFocus = node.RestorePreviousFocus
            });
        }
        if (node.Gestures != UiGestureMask.None)
            _world.Set(entity, new GestureComponent { Enabled = node.Gestures });
        if (node.Transitions.Count > 0)
            _world.Set(entity, new TransitionSetComponent { Value = node.Transitions });
        if (!node.LayoutTransition.IsNone)
            _world.Set(entity, new LayoutTransitionComponent { Motion = node.LayoutTransition });
        if (node.Behaviors != UiBehavior.None || node.IsFocusScope || node.Gestures != UiGestureMask.None)
            _world.Set(entity, new InteractionStateComponent());
        if (node.CommandId != 0)
            _world.Set(entity, new CommandBindingComponent { CommandId = node.CommandId });
        if (node.Kind == UiNodeKind.Text || node.StaticText is not null)
        {
            _world.Set(entity, new TextContent { Value = node.StaticText });
            _world.Set(entity, node.TextFormat);
        }
        if (node.Kind == UiNodeKind.If)
            _world.Set(entity, new StructuralIfState());

        UiDirtyFlags initialDirty = UiDirtyFlags.Binding | UiDirtyFlags.Style | UiDirtyFlags.Render;
        if (node.Kind == UiNodeKind.Text || node.StaticText is not null)
            initialDirty |= UiDirtyFlags.TextMeasure;
        _world.Dirty.Mark(entity, initialDirty);
        return entity;
    }

    private void ApplyLayoutComponents(UiEntity entity, in BlueprintNode node)
    {
        switch (node.Kind)
        {
            case UiNodeKind.Column:
                _world.Set(entity, new StackLayout { Orientation = UiOrientation.Vertical, Gap = node.LayoutGap });
                break;
            case UiNodeKind.Row:
                _world.Set(entity, new StackLayout { Orientation = UiOrientation.Horizontal, Gap = node.LayoutGap });
                break;
            case UiNodeKind.Grid:
                GridTrackSetHandle tracks = _world.LayoutResources.Intern(node.GridColumns, node.GridRows);
                _world.Set(entity, new GridLayout
                {
                    Tracks = tracks,
                    ColumnGap = node.LayoutGap,
                    RowGap = node.LayoutGap
                });
                break;
            case UiNodeKind.Absolute:
                _world.Set(entity, new AbsoluteLayout());
                break;
            case UiNodeKind.Container:
            case UiNodeKind.Overlay:
            case UiNodeKind.Button:
            case UiNodeKind.If:
                _world.Set(entity, new OverlayLayout());
                break;
        }

        if (node.HasGridPlacement)
            _world.Set(entity, node.GridPlacement);
        if (node.HasAbsolutePlacement)
            _world.Set(entity, node.AbsolutePlacement);
    }

    private void ApplyBindings(
        BlueprintInstance instance,
        bool force,
        List<int>? remountedNodes)
    {
        BlueprintBinding[] bindings = instance.Blueprint.BindingsCore;
        BlueprintDependencyIndex index = instance.Blueprint.DependencyIndex;

        if (force)
        {
            for (int i = 0; i < bindings.Length; i++)
                TryApplyPropertyBinding(instance, i);
            SyncSliceVersions(instance);
            return;
        }

        _candidateBindings.Clear();

        // Changed slices → only affected binding indices.
        foreach (int slice in index.AllSlices)
        {
            ulong now = _store.Version(slice);
            if (!instance.SliceVersions.TryGetValue(slice, out ulong last) || last != now)
            {
                if (index.TryGetPropertyBindings(slice, out ReadOnlySpan<int> property))
                {
                    for (int i = 0; i < property.Length; i++)
                        _candidateBindings.Add(property[i]);
                }
            }
        }

        // Remounted nodes: re-apply their property bindings even if state version unchanged.
        if (remountedNodes is { Count: > 0 })
        {
            for (int n = 0; n < remountedNodes.Count; n++)
            {
                int nodeIndex = remountedNodes[n];
                for (int i = 0; i < bindings.Length; i++)
                {
                    if (bindings[i].Kind == BlueprintBindingKind.Text &&
                        bindings[i].NodeIndex == nodeIndex)
                    {
                        _candidateBindings.Add(i);
                    }
                }
            }
        }

        foreach (int bindingIndex in _candidateBindings)
            TryApplyPropertyBinding(instance, bindingIndex);

        SyncSliceVersions(instance);
    }

    private void TryApplyPropertyBinding(BlueprintInstance instance, int bindingIndex)
    {
        BlueprintBinding binding = instance.Blueprint.BindingsCore[bindingIndex];
        if (binding.Kind is BlueprintBindingKind.None or BlueprintBindingKind.Condition)
            return;

        UiEntity entity = instance.EntitiesByNode[binding.NodeIndex];
        if (!_world.Entities.IsAlive(entity))
            return;

        ulong version = _store.CombinedVersion(binding.DependencySlices);
        BindingStamp stamp = instance.BindingStamps[bindingIndex];
        if (stamp.Matches(version, entity))
            return;

        if (binding.Kind == BlueprintBindingKind.Text && binding.ReadString is not null)
        {
            string value = binding.ReadString(_store);
            _world.Set(entity, new TextContent { Value = value });
            _world.Dirty.Mark(entity, UiDirtyFlags.Binding | UiDirtyFlags.TextMeasure | UiDirtyFlags.Render);
            instance.BindingStamps[bindingIndex] = new BindingStamp { StateVersion = version, Entity = entity };
        }
    }

    private void SyncSliceVersions(BlueprintInstance instance)
    {
        foreach (int slice in instance.Blueprint.DependencyIndex.AllSlices)
            instance.SliceVersions[slice] = _store.Version(slice);
    }

    /// <summary>
    /// Structural fixpoint: evaluate If hosts from a work queue. Mounting a branch
    /// enqueues nested structural hosts so outer remounts reconcile inner Ifs immediately
    /// without waiting for the inner condition slice to change again.
    /// </summary>
    private void ReconcileStructural(
        BlueprintInstance instance,
        bool force,
        List<int> remountedNodes)
    {
        remountedNodes.Clear();
        BlueprintNode[] nodes = instance.Blueprint.NodesCore;
        BlueprintDependencyIndex index = instance.Blueprint.DependencyIndex;

        _structuralWorkQueue.Clear();
        _structuralWorkPending.Clear();

        if (force)
        {
            // Seed every structural host that already has a live entity (skeleton hosts).
            for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            {
                if (nodes[nodeIndex].Kind != UiNodeKind.If)
                    continue;
                if (_world.Entities.IsAlive(instance.EntitiesByNode[nodeIndex]))
                    EnqueueStructuralWork(nodeIndex);
            }
        }
        else
        {
            foreach (int slice in index.AllSlices)
            {
                ulong now = _store.Version(slice);
                if (instance.SliceVersions.TryGetValue(slice, out ulong last) && last == now)
                    continue;
                if (!index.TryGetStructuralBindings(slice, out ReadOnlySpan<int> structural))
                    continue;
                for (int i = 0; i < structural.Length; i++)
                {
                    int bindingIndex = structural[i];
                    int nodeIndex = instance.Blueprint.BindingsCore[bindingIndex].NodeIndex;
                    EnqueueStructuralWork(nodeIndex);
                }
            }
        }

        int head = 0;
        while (head < _structuralWorkQueue.Count)
        {
            int nodeIndex = _structuralWorkQueue[head++];
            // Pending membership ends on dequeue so a later remount can re-enqueue the same node.
            _structuralWorkPending.Remove(nodeIndex);

            BlueprintNode node = nodes[nodeIndex];
            if (node.Kind != UiNodeKind.If)
                continue;

            UiEntity host = instance.EntitiesByNode[nodeIndex];
            if (!_world.Entities.IsAlive(host))
                continue;

            if (node.ConditionBindingIndex < 0)
                continue;
            BlueprintBinding condition = instance.Blueprint.BindingsCore[node.ConditionBindingIndex];
            ulong version = _store.CombinedVersion(condition.DependencySlices);

            ref StructuralIfState state = ref _world.Components.Pool<StructuralIfState>().Get(host);
            if (!force && state.LastConditionVersion == version && state.ActiveBranch != 0)
                continue;

            bool value = condition.ReadBool?.Invoke(_store) ?? false;
            byte desired = value ? (byte)1 : (byte)2;
            if (!force && state.ActiveBranch == desired)
            {
                state.LastConditionVersion = version;
                continue;
            }

            DismountBranch(instance, nodeIndex, state.ActiveBranch);

            int branchRoot = desired == 1 ? node.TrueBranchRoot : node.FalseBranchRoot;
            if (branchRoot >= 0)
            {
                MountNode(instance.Blueprint, branchRoot, host, instance.InstanceId, instance.EntitiesByNode, instance.Scope);
                CollectSubtreeNodeIndices(instance.Blueprint, branchRoot, remountedNodes);
                // Nested If hosts created as empty shells must evaluate this same pass.
                EnqueueStructuralHostsInMountedSubtree(instance.Blueprint, branchRoot);
            }

            state.ActiveBranch = desired;
            state.LastConditionVersion = version;
            _world.Dirty.Mark(host, UiDirtyFlags.StructuralCascade);
            LayoutInvalidation.MarkMeasure(_world, host, requestFrame: false);
            _world.Scheduler.RequestReactiveFrame();
        }
    }

    private void EnqueueStructuralWork(int nodeIndex)
    {
        if (nodeIndex < 0)
            return;
        if (_structuralWorkPending.Add(nodeIndex))
            _structuralWorkQueue.Add(nodeIndex);
    }

    /// <summary>
    /// Walk a just-mounted blueprint subtree and enqueue every If host shell
    /// (created by MountNode without an active branch yet).
    /// </summary>
    private void EnqueueStructuralHostsInMountedSubtree(UiBlueprint blueprint, int nodeIndex)
    {
        if (nodeIndex < 0)
            return;

        BlueprintNode node = blueprint.NodesCore[nodeIndex];
        if (node.Kind == UiNodeKind.If)
        {
            EnqueueStructuralWork(nodeIndex);
            // Active branch is not mounted yet — evaluation will mount and enqueue further nests.
            return;
        }

        int child = node.FirstChildIndex;
        while (child >= 0)
        {
            EnqueueStructuralHostsInMountedSubtree(blueprint, child);
            child = blueprint.NodesCore[child].NextSiblingIndex;
        }
    }

    private void DismountBranch(BlueprintInstance instance, int ifNodeIndex, byte activeBranch)
    {
        if (activeBranch is not (1 or 2))
            return;

        BlueprintNode node = instance.Blueprint.NodesCore[ifNodeIndex];
        int branchRoot = activeBranch == 1 ? node.TrueBranchRoot : node.FalseBranchRoot;
        if (branchRoot < 0)
            return;

        UiEntity branchEntity = instance.EntitiesByNode[branchRoot];
        if (_world.Entities.IsAlive(branchEntity))
            _world.DestroyEntity(branchEntity);

        ClearSubtreeMap(instance, branchRoot);
        InvalidateBindingStampsInSubtree(instance, branchRoot);
    }

    private static void CollectSubtreeNodeIndices(UiBlueprint blueprint, int nodeIndex, List<int> destination)
    {
        if (nodeIndex < 0)
            return;
        destination.Add(nodeIndex);
        BlueprintNode node = blueprint.NodesCore[nodeIndex];
        if (node.Kind == UiNodeKind.If)
        {
            CollectSubtreeNodeIndices(blueprint, node.TrueBranchRoot, destination);
            CollectSubtreeNodeIndices(blueprint, node.FalseBranchRoot, destination);
            return;
        }

        int child = node.FirstChildIndex;
        while (child >= 0)
        {
            CollectSubtreeNodeIndices(blueprint, child, destination);
            child = blueprint.NodesCore[child].NextSiblingIndex;
        }
    }

    private static void InvalidateBindingStampsInSubtree(BlueprintInstance instance, int nodeIndex)
    {
        if (nodeIndex < 0)
            return;

        // TODO(perf): compile DFS ranges (SubtreeStart/End, BindingStart/End) for O(subtree bindings).
        BlueprintBinding[] bindings = instance.Blueprint.BindingsCore;
        List<int> nodes = [];
        CollectSubtreeNodeIndices(instance.Blueprint, nodeIndex, nodes);
        HashSet<int> subtree = new(nodes);

        for (int i = 0; i < bindings.Length; i++)
        {
            if (subtree.Contains(bindings[i].NodeIndex))
                instance.BindingStamps[i] = BindingStamp.None;
        }
    }

    private static void ClearSubtreeMap(BlueprintInstance instance, int nodeIndex)
    {
        if (nodeIndex < 0)
            return;
        instance.EntitiesByNode[nodeIndex] = UiEntity.None;
        BlueprintNode node = instance.Blueprint.NodesCore[nodeIndex];
        if (node.Kind == UiNodeKind.If)
        {
            ClearSubtreeMap(instance, node.TrueBranchRoot);
            ClearSubtreeMap(instance, node.FalseBranchRoot);
            return;
        }

        int child = node.FirstChildIndex;
        while (child >= 0)
        {
            ClearSubtreeMap(instance, child);
            child = instance.Blueprint.NodesCore[child].NextSiblingIndex;
        }
    }
}
