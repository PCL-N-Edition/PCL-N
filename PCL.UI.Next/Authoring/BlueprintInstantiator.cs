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

    public BlueprintInstantiator(UiWorld world, PresentationStore store)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _store = store ?? throw new ArgumentNullException(nameof(store));
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

        // Mount non-structural skeleton depth-first; If nodes mount only the active branch.
        MountNode(blueprint, blueprint.RootIndex, parentEntity: UiEntity.None, instanceId, map, scope);

        BindingStamp[] stamps = new BindingStamp[blueprint.Bindings.Count];
        var instance = new BlueprintInstance(instanceId, blueprint, scope, map, stamps);
        _instances.Add(instance);

        // Structural first so branch entities exist before property bindings apply.
        ReconcileStructural(instance, force: true);
        ApplyBindings(instance, force: true);
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
    /// re-apply when state version or target entity generation changed.
    /// </summary>
    public void Update(BlueprintInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!EnsureInstanceAlive(instance))
            return;

        ReconcileStructural(instance, force: false);
        ApplyBindings(instance, force: false);
    }

    public void UpdateAll()
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            BlueprintInstance instance = _instances[i];
            if (!EnsureInstanceAlive(instance))
                continue;
            Update(instance);
        }
    }

    private bool EnsureInstanceAlive(BlueprintInstance instance)
    {
        if (!instance.IsAlive)
        {
            _instances.Remove(instance);
            return false;
        }

        if (!_world.Scopes.IsAlive(instance.Scope))
        {
            // Scope disposed externally — entities already destroyed by UiWorld.
            InvalidateInstance(instance);
            _instances.Remove(instance);
            return false;
        }

        return true;
    }

    private static void InvalidateInstance(BlueprintInstance instance)
    {
        instance.IsAlive = false;
        for (int i = 0; i < instance.EntitiesByNode.Length; i++)
            instance.EntitiesByNode[i] = UiEntity.None;
        for (int i = 0; i < instance.BindingStamps.Length; i++)
            instance.BindingStamps[i] = BindingStamp.None;
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
            // Placeholder entity for the structural host; branches mount separately.
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
        if (node.StyleClassIds.Length > 0)
            _world.Set(entity, StyleClassSet.From(node.StyleClassIds));
        if (node.Behaviors != UiBehavior.None)
            _world.Set(entity, new BehaviorComponent { Flags = node.Behaviors });
        if (node.CommandId != 0)
            _world.Set(entity, new CommandBindingComponent { CommandId = node.CommandId });
        // Only real text carriers get TextContent — not Button shell entities.
        if (node.Kind == UiNodeKind.Text || node.StaticText is not null)
            _world.Set(entity, new TextContent { Value = node.StaticText });
        if (node.Kind == UiNodeKind.If)
            _world.Set(entity, new StructuralIfState());

        _world.Dirty.Mark(entity, UiDirtyFlags.Binding | UiDirtyFlags.Style | UiDirtyFlags.Render);
        return entity;
    }

    private void ApplyBindings(BlueprintInstance instance, bool force)
    {
        BlueprintBinding[] bindings = instance.Blueprint.BindingsCore;
        for (int i = 0; i < bindings.Length; i++)
        {
            BlueprintBinding binding = bindings[i];
            if (binding.Kind is BlueprintBindingKind.None or BlueprintBindingKind.Condition)
                continue;

            UiEntity entity = instance.EntitiesByNode[binding.NodeIndex];
            if (!_world.Entities.IsAlive(entity))
            {
                // Do not stamp success for unmounted targets — remount must re-apply.
                continue;
            }

            ulong version = _store.CombinedVersion(binding.DependencySlices);
            BindingStamp stamp = instance.BindingStamps[i];
            if (!force && stamp.Matches(version, entity))
                continue;

            if (binding.Kind == BlueprintBindingKind.Text && binding.ReadString is not null)
            {
                string value = binding.ReadString(_store);
                _world.Set(entity, new TextContent { Value = value });
                _world.Dirty.Mark(entity, UiDirtyFlags.Binding | UiDirtyFlags.TextMeasure | UiDirtyFlags.Render);
                instance.BindingStamps[i] = new BindingStamp { StateVersion = version, Entity = entity };
            }
        }
    }

    private void ReconcileStructural(BlueprintInstance instance, bool force)
    {
        BlueprintNode[] nodes = instance.Blueprint.NodesCore;
        for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
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
            if (!force && state.LastConditionVersion == version)
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
                MountNode(instance.Blueprint, branchRoot, host, instance.InstanceId, instance.EntitiesByNode, instance.Scope);

            state.ActiveBranch = desired;
            state.LastConditionVersion = version;
            _world.Dirty.Mark(host, UiDirtyFlags.StructuralCascade);
            _world.Scheduler.RequestReactiveFrame();
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
        // Invalidate stamps for any bindings under the torn-down branch.
        InvalidateBindingStampsInSubtree(instance, branchRoot);
    }

    private static void InvalidateBindingStampsInSubtree(BlueprintInstance instance, int nodeIndex)
    {
        if (nodeIndex < 0)
            return;

        BlueprintBinding[] bindings = instance.Blueprint.BindingsCore;
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].NodeIndex == nodeIndex)
                instance.BindingStamps[i] = BindingStamp.None;
        }

        BlueprintNode node = instance.Blueprint.NodesCore[nodeIndex];
        if (node.Kind == UiNodeKind.If)
        {
            InvalidateBindingStampsInSubtree(instance, node.TrueBranchRoot);
            InvalidateBindingStampsInSubtree(instance, node.FalseBranchRoot);
            return;
        }

        int child = node.FirstChildIndex;
        while (child >= 0)
        {
            InvalidateBindingStampsInSubtree(instance, child);
            child = instance.Blueprint.NodesCore[child].NextSiblingIndex;
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
