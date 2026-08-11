// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Owns focus per input root and deterministic keyboard navigation.</summary>
public sealed class UiFocusManager : IDisposable
{
    private readonly UiWorld _world;
    private readonly UiRoutedEventRouter _router;
    private readonly UiHitTestIndex _hitTest;
    private readonly Dictionary<UiScopeId, UiEntity> _focusedByRoot = [];
    private readonly Dictionary<UiScopeId, UiEntity> _activeScopeByRoot = [];
    private readonly Dictionary<UiEntity, UiEntity> _restoreByFocusScope = [];
    private readonly Dictionary<UiEntity, UiEntity> _previousActiveScope = [];
    private readonly List<UiEntity> _candidates = [];
    private bool _disposed;

    public UiFocusManager(UiWorld world, UiRoutedEventRouter router, UiHitTestIndex hitTest)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _hitTest = hitTest ?? throw new ArgumentNullException(nameof(hitTest));
        _world.EntityDestroying += OnEntityDestroying;
    }

    public UiEntity GetFocused(UiScopeId scope)
    {
        UiScopeId root = GetRootScope(scope);
        if (_focusedByRoot.TryGetValue(root, out UiEntity focused) && _world.Entities.IsAlive(focused))
            return focused;
        _focusedByRoot.Remove(root);
        return UiEntity.None;
    }

    public bool Focus(UiEntity entity, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanFocus(entity))
            return false;

        UiScopeId entityScope = _world.Entities.GetScope(entity);
        UiScopeId root = GetRootScope(entityScope);
        if (_activeScopeByRoot.TryGetValue(root, out UiEntity activeScope) &&
            IsTrappingScope(activeScope) &&
            !IsDescendantOrSelf(activeScope, entity))
        {
            return false;
        }

        UiEntity previous = GetFocused(root);
        if (previous == entity)
            return true;

        if (_world.Entities.IsAlive(previous))
        {
            InteractionStateStore.Set(_world, previous, InteractionState.Focused, enabled: false);
            UiRoutedEventData lostData = new(timestamp);
            _router.Dispatch(UiRoutedEventKind.LostFocus, previous, in lostData);
        }

        _focusedByRoot[root] = entity;
        InteractionStateStore.Set(_world, entity, InteractionState.Focused, enabled: true);
        UiRoutedEventData gotData = new(timestamp);
        _router.Dispatch(UiRoutedEventKind.GotFocus, entity, in gotData);
        return true;
    }

    public bool ClearFocus(UiScopeId scope, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiScopeId root = GetRootScope(scope);
        UiEntity previous = GetFocused(root);
        if (previous == UiEntity.None)
            return false;
        _focusedByRoot.Remove(root);
        if (_world.Entities.IsAlive(previous))
        {
            InteractionStateStore.Set(_world, previous, InteractionState.Focused, enabled: false);
            UiRoutedEventData data = new(timestamp);
            _router.Dispatch(UiRoutedEventKind.LostFocus, previous, in data);
        }

        return true;
    }

    public bool MoveTab(UiScopeId scope, bool reverse, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiScopeId root = GetRootScope(scope);
        UiEntity current = GetFocused(root);
        UiEntity restriction = ResolveNavigationScope(root, current);
        CollectCandidates(root, restriction);
        if (_candidates.Count == 0)
            return false;

        _candidates.Sort(CompareTabOrder);
        int currentIndex = _candidates.IndexOf(current);
        int nextIndex;
        if (currentIndex < 0)
            nextIndex = reverse ? _candidates.Count - 1 : 0;
        else if (reverse)
            nextIndex = (currentIndex - 1 + _candidates.Count) % _candidates.Count;
        else
            nextIndex = (currentIndex + 1) % _candidates.Count;
        return Focus(_candidates[nextIndex], timestamp);
    }

    public bool MoveDirectional(UiScopeId scope, UiKey direction, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (direction is not (UiKey.Left or UiKey.Up or UiKey.Right or UiKey.Down))
            throw new ArgumentOutOfRangeException(nameof(direction));
        UiScopeId root = GetRootScope(scope);
        UiEntity current = GetFocused(root);
        if (!_world.Entities.IsAlive(current) || !_world.Components.TryGet(current, out LayoutRect currentRect))
            return MoveTab(scope, reverse: direction is UiKey.Left or UiKey.Up, timestamp);

        UiEntity restriction = ResolveNavigationScope(root, current);
        CollectCandidates(root, restriction);
        UiPoint origin = Center(currentRect.Value);
        UiEntity best = UiEntity.None;
        float bestScore = float.PositiveInfinity;
        for (int i = 0; i < _candidates.Count; i++)
        {
            UiEntity candidate = _candidates[i];
            if (candidate == current || !_world.Components.TryGet(candidate, out LayoutRect candidateRect))
                continue;
            UiPoint point = Center(candidateRect.Value);
            float dx = point.X - origin.X;
            float dy = point.Y - origin.Y;
            float primary;
            float cross;
            switch (direction)
            {
                case UiKey.Left when dx < 0f:
                    primary = -dx;
                    cross = MathF.Abs(dy);
                    break;
                case UiKey.Right when dx > 0f:
                    primary = dx;
                    cross = MathF.Abs(dy);
                    break;
                case UiKey.Up when dy < 0f:
                    primary = -dy;
                    cross = MathF.Abs(dx);
                    break;
                case UiKey.Down when dy > 0f:
                    primary = dy;
                    cross = MathF.Abs(dx);
                    break;
                default:
                    continue;
            }

            float score = (primary * primary * 4f) + (cross * cross);
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best != UiEntity.None && Focus(best, timestamp);
    }

    public UiEntity FindFocusableAncestor(UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (_world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (CanFocus(current))
                return current;
            if (!_world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            current = node.Parent;
        }

        return UiEntity.None;
    }

    public bool ActivateScope(UiEntity focusScope, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_world.Entities.IsAlive(focusScope) ||
            !_world.Components.Has<FocusScopeComponent>(focusScope))
        {
            return false;
        }

        UiScopeId root = GetRootScope(_world.Entities.GetScope(focusScope));
        if (_activeScopeByRoot.TryGetValue(root, out UiEntity alreadyActive) && alreadyActive == focusScope)
            return true;
        UiEntity previous = GetFocused(root);
        _restoreByFocusScope[focusScope] = previous;
        _previousActiveScope[focusScope] = _activeScopeByRoot.TryGetValue(root, out UiEntity active)
            ? active
            : UiEntity.None;
        _activeScopeByRoot[root] = focusScope;
        CollectCandidates(root, focusScope);
        _candidates.Sort(CompareTabOrder);
        if (_candidates.Count > 0)
            return Focus(_candidates[0], timestamp);
        ClearFocus(root, timestamp);
        return true;
    }

    public bool DeactivateScope(UiEntity focusScope, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_world.Entities.TryGetScope(focusScope, out UiScopeId scope))
            return false;
        UiScopeId root = GetRootScope(scope);
        if (!_activeScopeByRoot.TryGetValue(root, out UiEntity active) || active != focusScope)
            return false;
        if (_previousActiveScope.Remove(focusScope, out UiEntity previousActive) &&
            _world.Entities.IsAlive(previousActive) &&
            _world.Components.Has<FocusScopeComponent>(previousActive))
        {
            _activeScopeByRoot[root] = previousActive;
        }
        else
        {
            _activeScopeByRoot.Remove(root);
        }

        bool restore = _world.Components.TryGet(focusScope, out FocusScopeComponent component) &&
                       component.RestorePreviousFocus;
        UiEntity previous = _restoreByFocusScope.Remove(focusScope, out UiEntity saved)
            ? saved
            : UiEntity.None;
        if (restore && CanFocus(previous))
            return Focus(previous, timestamp);
        return ClearFocus(root, timestamp);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.EntityDestroying -= OnEntityDestroying;
        _focusedByRoot.Clear();
        _activeScopeByRoot.Clear();
        _restoreByFocusScope.Clear();
        _previousActiveScope.Clear();
        _candidates.Clear();
        _disposed = true;
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        UiScopeId[] roots = _focusedByRoot
            .Where(pair => pair.Value == entity)
            .Select(pair => pair.Key)
            .ToArray();
        for (int i = 0; i < roots.Length; i++)
        {
            InteractionStateStore.Set(_world, entity, InteractionState.Focused, enabled: false);
            _focusedByRoot.Remove(roots[i]);
        }

        UiScopeId[] activeRoots = _activeScopeByRoot
            .Where(pair => pair.Value == entity)
            .Select(pair => pair.Key)
            .ToArray();
        for (int i = 0; i < activeRoots.Length; i++)
        {
            if (_previousActiveScope.Remove(entity, out UiEntity previousActive) &&
                _world.Entities.IsAlive(previousActive) &&
                _world.Components.Has<FocusScopeComponent>(previousActive))
            {
                _activeScopeByRoot[activeRoots[i]] = previousActive;
            }
            else
            {
                _activeScopeByRoot.Remove(activeRoots[i]);
            }
            if (_restoreByFocusScope.Remove(entity, out UiEntity previous) && CanFocus(previous))
                Focus(previous, _world.Clock.Now);
        }

        foreach (UiEntity key in _restoreByFocusScope
                     .Where(pair => pair.Value == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _restoreByFocusScope[key] = UiEntity.None;
        }

        foreach (UiEntity key in _previousActiveScope
                     .Where(pair => pair.Value == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _previousActiveScope[key] = UiEntity.None;
        }
    }

    private bool CanFocus(UiEntity entity)
    {
        if (!_world.Entities.IsAlive(entity) ||
            !_world.Components.TryGet(entity, out FocusableComponent focusable) ||
            !focusable.IsTabStop)
        {
            return false;
        }

        return !_world.Components.TryGet(entity, out InteractionStateComponent state) ||
               (state.Value & InteractionState.Disabled) == 0;
    }

    private void CollectCandidates(UiScopeId root, UiEntity restriction)
    {
        _candidates.Clear();
        ComponentPool<FocusableComponent> pool = _world.Components.Pool<FocusableComponent>();
        ReadOnlySpan<UiEntity> entities = pool.Entities;
        ReadOnlySpan<FocusableComponent> components = pool.Components;
        for (int i = 0; i < entities.Length; i++)
        {
            UiEntity candidate = entities[i];
            FocusableComponent component = components[i];
            if (!component.IsTabStop || component.TabIndex < 0 || !CanFocus(candidate))
                continue;
            if (GetRootScope(_world.Entities.GetScope(candidate)) != root)
                continue;
            if (restriction != UiEntity.None && !IsDescendantOrSelf(restriction, candidate))
                continue;
            _candidates.Add(candidate);
        }
    }

    private int CompareTabOrder(UiEntity left, UiEntity right)
    {
        int leftIndex = _world.Components.Get<FocusableComponent>(left).TabIndex;
        int rightIndex = _world.Components.Get<FocusableComponent>(right).TabIndex;
        int tab = leftIndex.CompareTo(rightIndex);
        if (tab != 0)
            return tab;
        bool hasLeftOrder = _hitTest.TryGetRenderOrder(left, out int leftOrder);
        bool hasRightOrder = _hitTest.TryGetRenderOrder(right, out int rightOrder);
        if (hasLeftOrder && hasRightOrder)
            return leftOrder.CompareTo(rightOrder);
        return left.Index.CompareTo(right.Index);
    }

    private UiEntity ResolveNavigationScope(UiScopeId root, UiEntity current)
    {
        if (_activeScopeByRoot.TryGetValue(root, out UiEntity active) && IsTrappingScope(active))
            return active;
        UiEntity cursor = current;
        int guard = 0;
        while (_world.Entities.IsAlive(cursor) && guard++ < 1_000_000)
        {
            if (IsTrappingScope(cursor))
                return cursor;
            if (!_world.Hierarchy.TryGetNode(cursor, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            cursor = node.Parent;
        }

        return UiEntity.None;
    }

    private bool IsTrappingScope(UiEntity entity) =>
        _world.Entities.IsAlive(entity) &&
        _world.Components.TryGet(entity, out FocusScopeComponent scope) &&
        scope.IsTrap;

    private bool IsDescendantOrSelf(UiEntity ancestor, UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (_world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (current == ancestor)
                return true;
            if (!_world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            current = node.Parent;
        }

        return false;
    }

    private UiScopeId GetRootScope(UiScopeId scope)
    {
        UiScopeId current = scope;
        int guard = 0;
        while (_world.Scopes.TryGetParent(current, out UiScopeId parent) &&
               !parent.IsNone &&
               guard++ < 1_000_000)
        {
            current = parent;
        }

        return current;
    }

    private static UiPoint Center(UiRect rect) =>
        new(rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f));
}

internal static class InteractionStateStore
{
    public static bool Set(UiWorld world, UiEntity entity, InteractionState flag, bool enabled)
    {
        if (!world.Entities.IsAlive(entity))
            return false;
        InteractionState value = world.Components.TryGet(entity, out InteractionStateComponent component)
            ? component.Value
            : InteractionState.None;
        InteractionState next = enabled ? value | flag : value & ~flag;
        if (next == value)
            return false;
        world.Set(entity, new InteractionStateComponent { Value = next });
        world.Dirty.Mark(entity, UiDirtyFlags.Style | UiDirtyFlags.Render);
        return true;
    }
}
