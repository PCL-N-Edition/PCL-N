// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Builds a semantic tree from ECS state without sharing render-tree topology.</summary>
public sealed class UiAccessibilityRuntime : IUiSystem, IDisposable
{
    private readonly UiWorld _world;
    private readonly IAccessibilityBackend? _backend;
    private readonly UiInputRuntime? _input;
    private readonly UiScopeId _rootScope;
    private readonly List<UiEntity> _semanticEntities = [];
    private readonly List<UiEntity> _dirtyEntities = [];
    private readonly List<UiEntity> _logicalRoots = [];
    private readonly HashSet<UiEntity> _logicalRootSet = [];
    private readonly List<UiSemanticNode> _nodes = [];
    private readonly Dictionary<UiSemanticNodeId, int> _childCounts = [];
    private readonly Queue<UiAccessibilityActionRequest> _pendingActions = [];
    private readonly object _actionGate = new();
    private readonly List<UiAccessibilityFrameAction> _frameActions = [];
    private uint _hierarchyVersion;
    private uint _semanticVersion;
    private bool _initialized;
    private bool _disposed;

    public UiAccessibilityRuntime(
        UiWorld world,
        UiScopeId rootScope,
        IAccessibilityBackend? backend = null,
        UiInputRuntime? input = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        if (!world.Scopes.IsAlive(rootScope))
            throw new InvalidOperationException("Accessibility root scope is not alive: " + rootScope);
        _rootScope = rootScope;
        _backend = backend;
        _input = input;
        if (_backend is not null)
            _backend.AccessibilityActionRaised += OnAccessibilityAction;
        _world.Systems.Register(this);
    }

    public UiSystemPhase Phase => UiSystemPhase.AccessibilityUpdate;
    public string Name => "accessibility.update";
    public UiSemanticTreeSnapshot Tree { get; private set; } = UiSemanticTreeSnapshot.Empty;
    public IReadOnlyList<UiAccessibilityFrameAction> FrameActions => _frameActions;

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainActions(frame.Now);
        bool changed = !_initialized ||
                       _hierarchyVersion != world.Hierarchy.StructuralVersion ||
                       HasAccessibilityDirty(world);
        if (!changed)
            return;

        Rebuild(world, frame.FrameIndex);
        _hierarchyVersion = world.Hierarchy.StructuralVersion;
        _initialized = true;
        _backend?.CommitAccessibility(Tree);
        ClearAccessibilityDirty(world);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.Systems.Unregister(this);
        if (_backend is not null)
            _backend.AccessibilityActionRaised -= OnAccessibilityAction;
        _semanticEntities.Clear();
        _dirtyEntities.Clear();
        _logicalRoots.Clear();
        _logicalRootSet.Clear();
        _nodes.Clear();
        _childCounts.Clear();
        lock (_actionGate)
            _pendingActions.Clear();
        _frameActions.Clear();
        _disposed = true;
    }

    private void OnAccessibilityAction(UiAccessibilityActionRequest request)
    {
        lock (_actionGate)
            _pendingActions.Enqueue(request);
        _world.Scheduler.RequestReactiveFrame();
    }

    private void DrainActions(UiTimestamp now)
    {
        _frameActions.Clear();
        while (true)
        {
            UiAccessibilityActionRequest request;
            lock (_actionGate)
            {
                if (!_pendingActions.TryDequeue(out request))
                    break;
            }
            if (!_world.Entities.IsAlive(request.Owner) ||
                !IsInRoot(_world, request.Owner) ||
                !_world.Components.TryGet(request.Owner, out AccessibleAction supported) ||
                (supported.Value & request.Action) == 0)
            {
                continue;
            }

            UiTimestamp timestamp = request.Timestamp == UiTimestamp.Zero ? now : request.Timestamp;
            _frameActions.Add(new UiAccessibilityFrameAction(
                request.Owner,
                request.Action,
                timestamp,
                request.Value));
            if (_input is null)
                continue;
            if (request.Action == UiAccessibleAction.Focus)
            {
                _input.Focus.Focus(request.Owner, timestamp);
            }
            else if (request.Action == UiAccessibleAction.Invoke &&
                     UiEffectiveState.IsInteractive(_world, request.Owner) &&
                     _world.Components.TryGet(request.Owner, out CommandBindingComponent command) &&
                     command.CommandId != 0 &&
                     _world.Entities.TryGetScope(request.Owner, out UiScopeId scope))
            {
                UiCommandInvocation invocation = new(
                    new UiCommand(command.CommandId),
                    request.Owner,
                    scope,
                    UiCommandTrigger.Accessibility,
                    timestamp);
                _input.Commands.Enqueue(in invocation);
            }
        }
    }

    private void Rebuild(UiWorld world, long frameId)
    {
        _semanticEntities.Clear();
        _logicalRoots.Clear();
        _logicalRootSet.Clear();
        _nodes.Clear();
        _childCounts.Clear();

        world.Components.Pool<SemanticRole>().CopyEntitiesTo(_semanticEntities);
        for (int i = 0; i < _semanticEntities.Count; i++)
        {
            UiEntity entity = _semanticEntities[i];
            if (!world.Entities.IsAlive(entity) || !IsInRoot(world, entity))
                continue;
            UiEntity root = FindLogicalRoot(world, entity);
            if (_logicalRootSet.Add(root))
                _logicalRoots.Add(root);
        }

        _logicalRoots.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        for (int i = 0; i < _logicalRoots.Count; i++)
            Visit(world, _logicalRoots[i], UiSemanticNodeId.None);

        unchecked
        {
            _semanticVersion++;
            if (_semanticVersion == 0)
                _semanticVersion = 1;
        }
        Tree = new UiSemanticTreeSnapshot(frameId, _semanticVersion, _nodes.ToArray());
    }

    private void Visit(UiWorld world, UiEntity entity, UiSemanticNodeId semanticParent)
    {
        if (!world.Entities.IsAlive(entity) || !IsInRoot(world, entity))
            return;
        if (!UiEffectiveState.IsVisible(world, entity))
            return;

        UiSemanticNodeId descendantParent = semanticParent;
        if (world.Components.TryGet(entity, out SemanticRole role))
        {
            UiAccessibleState state = ResolveState(world, entity);
            if ((state & UiAccessibleState.Hidden) != 0)
                return;

            UiSemanticNodeId id = UiSemanticNodeId.FromEntity(entity);
            int childOrder = 0;
            if (_childCounts.TryGetValue(semanticParent, out int count))
                childOrder = count;
            _childCounts[semanticParent] = childOrder + 1;

            _nodes.Add(new UiSemanticNode(
                id,
                entity,
                semanticParent,
                childOrder,
                role.Value,
                ResolveName(world, entity),
                world.Components.TryGet(entity, out AccessibleDescription description)
                    ? description.Value ?? string.Empty
                    : string.Empty,
                ResolveValue(world, entity, role.Value),
                state,
                world.Components.TryGet(entity, out AccessibleAction action)
                    ? action.Value
                    : UiAccessibleAction.None,
                UiVisualGeometry.ResolveBounds(world, entity)));
            descendantParent = id;
        }

        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode hierarchy))
            return;
        UiEntity child = hierarchy.FirstChild;
        while (child != UiEntity.None)
        {
            UiEntity next = world.Hierarchy.TryGetNode(child, out HierarchyNode childNode)
                ? childNode.NextSibling
                : UiEntity.None;
            Visit(world, child, descendantParent);
            child = next;
        }
    }

    private static UiAccessibleState ResolveState(UiWorld world, UiEntity entity)
    {
        UiAccessibleState state = world.Components.TryGet(entity, out AccessibleState explicitState)
            ? explicitState.Value
            : UiAccessibleState.None;
        if (world.Components.TryGet(entity, out InteractionStateComponent interaction))
        {
            if ((interaction.Value & InteractionState.Disabled) != 0) state |= UiAccessibleState.Disabled;
            if ((interaction.Value & InteractionState.Focused) != 0) state |= UiAccessibleState.Focused;
            if ((interaction.Value & InteractionState.Selected) != 0) state |= UiAccessibleState.Selected;
            if ((interaction.Value & InteractionState.Checked) != 0) state |= UiAccessibleState.Checked;
            if ((interaction.Value & InteractionState.Expanded) != 0) state |= UiAccessibleState.Expanded;
        }
        if (!UiEffectiveState.IsEnabled(world, entity)) state |= UiAccessibleState.Disabled;
        if (!UiEffectiveState.IsVisible(world, entity)) state |= UiAccessibleState.Hidden;
        if (world.Components.TryGet(entity, out NativeHostComponent native) && native.IsReadOnly)
            state |= UiAccessibleState.ReadOnly;
        return state;
    }

    private static string ResolveName(UiWorld world, UiEntity entity)
    {
        if (world.Components.TryGet(entity, out AccessibleName name) && !string.IsNullOrEmpty(name.Value))
            return name.Value;
        if (world.Components.TryGet(entity, out TextContent text) && !string.IsNullOrEmpty(text.Value))
            return text.Value;
        if (world.Components.TryGet(entity, out NativeHostComponent native) && !string.IsNullOrEmpty(native.Placeholder))
            return native.Placeholder;
        return FindDescendantText(world, entity);
    }

    private static string ResolveValue(UiWorld world, UiEntity entity, UiSemanticRole role)
    {
        if (world.Components.TryGet(entity, out AccessibleValue accessibleValue))
            return accessibleValue.Value ?? string.Empty;
        if (role != UiSemanticRole.PasswordBox &&
            world.Components.TryGet(entity, out NativeHostComponent native))
        {
            return native.Value ?? string.Empty;
        }
        return string.Empty;
    }

    private static string FindDescendantText(UiWorld world, UiEntity entity)
    {
        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode hierarchy))
            return string.Empty;
        UiEntity child = hierarchy.FirstChild;
        while (child != UiEntity.None)
        {
            if (world.Components.TryGet(child, out TextContent text) && !string.IsNullOrEmpty(text.Value))
                return text.Value;
            string nested = FindDescendantText(world, child);
            if (!string.IsNullOrEmpty(nested))
                return nested;
            child = world.Hierarchy.TryGetNode(child, out HierarchyNode childNode)
                ? childNode.NextSibling
                : UiEntity.None;
        }
        return string.Empty;
    }

    private bool IsInRoot(UiWorld world, UiEntity entity)
    {
        if (!world.Entities.TryGetScope(entity, out UiScopeId scope))
            return false;
        int guard = 0;
        while (world.Scopes.IsAlive(scope) && guard++ < 1_000_000)
        {
            if (scope == _rootScope)
                return true;
            if (!world.Scopes.TryGetParent(scope, out scope) || scope.IsNone)
                break;
        }
        return false;
    }

    private UiEntity FindLogicalRoot(UiWorld world, UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (world.Hierarchy.TryGetNode(current, out HierarchyNode node) &&
               node.Parent != UiEntity.None && guard++ < 1_000_000)
        {
            if (!IsInRoot(world, node.Parent))
                break;
            current = node.Parent;
        }
        return current;
    }

    private bool HasAccessibilityDirty(UiWorld world)
    {
        _dirtyEntities.Clear();
        world.Dirty.Collect(UiDirtyFlags.Accessibility, _dirtyEntities);
        for (int i = 0; i < _dirtyEntities.Count; i++)
        {
            if (world.Entities.IsAlive(_dirtyEntities[i]) && IsInRoot(world, _dirtyEntities[i]))
                return true;
        }
        return false;
    }

    private void ClearAccessibilityDirty(UiWorld world)
    {
        _dirtyEntities.Clear();
        world.Dirty.Collect(UiDirtyFlags.Accessibility, _dirtyEntities);
        for (int i = 0; i < _dirtyEntities.Count; i++)
        {
            UiEntity entity = _dirtyEntities[i];
            if (world.Entities.IsAlive(entity) && IsInRoot(world, entity))
                world.Dirty.Clear(entity, UiDirtyFlags.Accessibility);
        }
    }
}
