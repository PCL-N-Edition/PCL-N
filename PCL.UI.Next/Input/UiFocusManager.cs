// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Owns focus per input root and deterministic keyboard navigation.</summary>
public sealed class UiFocusManager : IDisposable
{
    private readonly UiWorld _world;
    private readonly UiRoutedEventRouter _router;
    private readonly UiHitTestIndex _hitTest;
    private readonly UiInputRootRegistry _inputRoots;
    private readonly Dictionary<UiInputRootId, UiEntity> _focusedByRoot = [];
    private readonly Dictionary<UiInputRootId, UiEntity> _activeScopeByRoot = [];
    private readonly Dictionary<UiEntity, UiEntity> _restoreByFocusScope = [];
    private readonly Dictionary<UiEntity, UiEntity> _previousActiveScope = [];
    private readonly List<UiEntity> _candidates = [];
    private readonly List<UiInputRootId> _validationRoots = [];
    private bool _disposed;

    public UiFocusManager(
        UiWorld world,
        UiRoutedEventRouter router,
        UiHitTestIndex hitTest,
        UiInputRootRegistry inputRoots)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _hitTest = hitTest ?? throw new ArgumentNullException(nameof(hitTest));
        _inputRoots = inputRoots ?? throw new ArgumentNullException(nameof(inputRoots));
        _world.EntityDestroying += OnEntityDestroying;
        _inputRoots.InputRootDestroying += OnInputRootDestroying;
    }

    public UiEntity GetFocused(UiInputRootId inputRoot)
    {
        if (!_inputRoots.IsAlive(inputRoot) ||
            !_focusedByRoot.TryGetValue(inputRoot, out UiEntity focused))
        {
            return UiEntity.None;
        }

        if (CanFocus(focused) && _inputRoots.Contains(inputRoot, focused))
            return focused;
        InvalidateFocus(inputRoot, focused, _world.Clock.Now);
        return UiEntity.None;
    }

    public void Validate(UiTimestamp timestamp)
    {
        _validationRoots.Clear();
        foreach (UiInputRootId inputRoot in _focusedByRoot.Keys)
            _validationRoots.Add(inputRoot);
        for (int i = 0; i < _validationRoots.Count; i++)
        {
            UiInputRootId inputRoot = _validationRoots[i];
            if (_focusedByRoot.TryGetValue(inputRoot, out UiEntity focused) &&
                (!CanFocus(focused) || !_inputRoots.Contains(inputRoot, focused)))
            {
                InvalidateFocus(inputRoot, focused, timestamp);
            }
        }
    }

    public bool Focus(UiEntity entity, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanFocus(entity))
            return false;

        if (!_inputRoots.TryResolve(entity, out UiInputRootId inputRoot))
            return false;
        if (_activeScopeByRoot.TryGetValue(inputRoot, out UiEntity activeScope) &&
            IsTrappingScope(activeScope) &&
            !IsDescendantOrSelf(activeScope, entity))
        {
            return false;
        }

        UiEntity previous = GetFocused(inputRoot);
        if (previous == entity)
            return true;

        if (_world.Entities.IsAlive(previous))
        {
            InteractionStateStore.Set(_world, previous, InteractionState.Focused, enabled: false);
            UiRoutedEventData lostData = new(timestamp, InputRoot: inputRoot);
            _router.Dispatch(UiRoutedEventKind.LostFocus, previous, in lostData);
        }

        _focusedByRoot[inputRoot] = entity;
        InteractionStateStore.Set(_world, entity, InteractionState.Focused, enabled: true);
        UiRoutedEventData gotData = new(timestamp, InputRoot: inputRoot);
        _router.Dispatch(UiRoutedEventKind.GotFocus, entity, in gotData);
        return true;
    }

    public bool ClearFocus(UiInputRootId inputRoot, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiEntity previous = GetFocused(inputRoot);
        if (previous == UiEntity.None)
            return false;
        _focusedByRoot.Remove(inputRoot);
        if (_world.Entities.IsAlive(previous))
        {
            InteractionStateStore.Set(_world, previous, InteractionState.Focused, enabled: false);
            UiRoutedEventData data = new(timestamp, InputRoot: inputRoot);
            _router.Dispatch(UiRoutedEventKind.LostFocus, previous, in data);
        }

        return true;
    }

    public bool MoveTab(UiInputRootId inputRoot, bool reverse, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiEntity current = GetFocused(inputRoot);
        UiEntity restriction = ResolveNavigationScope(inputRoot, current);
        CollectCandidates(inputRoot, restriction);
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

    public bool MoveDirectional(UiInputRootId inputRoot, UiKey direction, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (direction is not (UiKey.Left or UiKey.Up or UiKey.Right or UiKey.Down))
            throw new ArgumentOutOfRangeException(nameof(direction));
        UiEntity current = GetFocused(inputRoot);
        if (!_world.Entities.IsAlive(current) || !_world.Components.TryGet(current, out LayoutRect currentRect))
            return MoveTab(inputRoot, reverse: direction is UiKey.Left or UiKey.Up, timestamp);

        UiEntity restriction = ResolveNavigationScope(inputRoot, current);
        CollectCandidates(inputRoot, restriction);
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

        if (!_inputRoots.TryResolve(focusScope, out UiInputRootId inputRoot))
            return false;
        if (_activeScopeByRoot.TryGetValue(inputRoot, out UiEntity alreadyActive) && alreadyActive == focusScope)
            return true;
        UiEntity previous = GetFocused(inputRoot);
        _restoreByFocusScope[focusScope] = previous;
        _previousActiveScope[focusScope] = _activeScopeByRoot.TryGetValue(inputRoot, out UiEntity active)
            ? active
            : UiEntity.None;
        _activeScopeByRoot[inputRoot] = focusScope;
        CollectCandidates(inputRoot, focusScope);
        _candidates.Sort(CompareTabOrder);
        if (_candidates.Count > 0)
            return Focus(_candidates[0], timestamp);
        ClearFocus(inputRoot, timestamp);
        return true;
    }

    public bool DeactivateScope(UiEntity focusScope, UiTimestamp timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_inputRoots.TryResolve(focusScope, out UiInputRootId inputRoot))
            return false;
        if (!_activeScopeByRoot.TryGetValue(inputRoot, out UiEntity active) || active != focusScope)
            return false;
        if (_previousActiveScope.Remove(focusScope, out UiEntity previousActive) &&
            _world.Entities.IsAlive(previousActive) &&
            _world.Components.Has<FocusScopeComponent>(previousActive))
        {
            _activeScopeByRoot[inputRoot] = previousActive;
        }
        else
        {
            _activeScopeByRoot.Remove(inputRoot);
        }

        bool restore = _world.Components.TryGet(focusScope, out FocusScopeComponent component) &&
                       component.RestorePreviousFocus;
        UiEntity previous = _restoreByFocusScope.Remove(focusScope, out UiEntity saved)
            ? saved
            : UiEntity.None;
        if (restore && CanFocus(previous))
            return Focus(previous, timestamp);
        return ClearFocus(inputRoot, timestamp);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.EntityDestroying -= OnEntityDestroying;
        _inputRoots.InputRootDestroying -= OnInputRootDestroying;
        _focusedByRoot.Clear();
        _activeScopeByRoot.Clear();
        _restoreByFocusScope.Clear();
        _previousActiveScope.Clear();
        _candidates.Clear();
        _validationRoots.Clear();
        _disposed = true;
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        UiInputRootId[] roots = _focusedByRoot
            .Where(pair => pair.Value == entity)
            .Select(pair => pair.Key)
            .ToArray();
        for (int i = 0; i < roots.Length; i++)
        {
            InteractionStateStore.Set(_world, entity, InteractionState.Focused, enabled: false);
            _focusedByRoot.Remove(roots[i]);
        }

        UiInputRootId[] activeRoots = _activeScopeByRoot
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

    internal bool CanFocus(UiEntity entity)
    {
        if (!_world.Entities.IsAlive(entity) ||
            !_world.Components.Has<FocusableComponent>(entity))
        {
            return false;
        }

        return InteractionStateStore.IsEnabledAndVisible(_world, entity);
    }

    private void CollectCandidates(UiInputRootId inputRoot, UiEntity restriction)
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
            if (!_inputRoots.Contains(inputRoot, candidate))
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

    private UiEntity ResolveNavigationScope(UiInputRootId inputRoot, UiEntity current)
    {
        if (_activeScopeByRoot.TryGetValue(inputRoot, out UiEntity active) && IsTrappingScope(active))
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

    private void InvalidateFocus(UiInputRootId inputRoot, UiEntity focused, UiTimestamp timestamp)
    {
        _focusedByRoot.Remove(inputRoot);
        if (_world.Entities.IsAlive(focused))
        {
            InteractionStateStore.Set(_world, focused, InteractionState.Focused, enabled: false);
            UiRoutedEventData data = new(timestamp, InputRoot: inputRoot);
            _router.Dispatch(UiRoutedEventKind.LostFocus, focused, in data);
        }
    }

    private void OnInputRootDestroying(UiInputRootId inputRoot)
    {
        if (_focusedByRoot.TryGetValue(inputRoot, out UiEntity focused))
            InvalidateFocus(inputRoot, focused, _world.Clock.Now);
        _activeScopeByRoot.Remove(inputRoot);
        foreach (UiEntity focusScope in _restoreByFocusScope.Keys
                     .Where(entity => _inputRoots.Contains(inputRoot, entity))
                     .ToArray())
        {
            _restoreByFocusScope.Remove(focusScope);
            _previousActiveScope.Remove(focusScope);
        }
    }

    private static UiPoint Center(UiRect rect) =>
        new(rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f));
}

internal static class InteractionStateStore
{
    public static bool IsEnabledAndVisible(UiWorld world, UiEntity entity)
    {
        if (!world.Entities.IsAlive(entity))
            return false;
        if (world.Components.TryGet(entity, out InteractionStateComponent state) &&
            (state.Value & InteractionState.Disabled) != 0)
        {
            return false;
        }

        return !world.Components.TryGet(entity, out HitTestableComponent hitTestable) ||
               (hitTestable.IsEnabled && hitTestable.IsVisible);
    }

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
        world.Dirty.Mark(entity, UiDirtyFlags.Style | UiDirtyFlags.Render | UiDirtyFlags.Accessibility);
        return true;
    }
}
