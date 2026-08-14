// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Synchronizes ECS target state to native controls and journals native input.</summary>
public sealed class UiNativeHostRuntime : IUiSystem, IDisposable
{
    private readonly UiWorld _world;
    private readonly INativeHostBackend _backend;
    private readonly UiScopeId _rootScope;
    private readonly UiInputRuntime? _input;
    private readonly Dictionary<UiEntity, Entry> _entries = [];
    private readonly Dictionary<NativeHostHandle, UiEntity> _owners = [];
    private readonly Queue<NativeHostEvent> _pendingEvents = [];
    private readonly object _eventGate = new();
    private readonly List<NativeHostFrameEvent> _frameEvents = [];
    private readonly List<UiEntity> _entities = [];
    private readonly HashSet<UiEntity> _seen = [];
    private bool _disposed;

    public UiNativeHostRuntime(
        UiWorld world,
        INativeHostBackend backend,
        UiScopeId rootScope,
        UiInputRuntime? input = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        if (!world.Scopes.IsAlive(rootScope))
            throw new InvalidOperationException("Native-host root scope is not alive: " + rootScope);
        _rootScope = rootScope;
        _input = input;
        _backend.NativeHostEventRaised += OnNativeHostEvent;
        _world.EntityDestroying += OnEntityDestroying;
        _world.Systems.Register(this);
    }

    public UiSystemPhase Phase => UiSystemPhase.AccessibilityUpdate;
    public string Name => "native-host.update";
    public IReadOnlyList<NativeHostFrameEvent> FrameEvents => _frameEvents;
    public int HostCount => _entries.Count;

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainEvents(frame.Now);
        _entities.Clear();
        _seen.Clear();
        world.Components.Pool<NativeHostComponent>().CopyEntitiesTo(_entities);
        for (int i = 0; i < _entities.Count; i++)
        {
            UiEntity entity = _entities[i];
            if (!world.Entities.IsAlive(entity) || !IsInRoot(entity))
                continue;
            _seen.Add(entity);
            NativeHostComponent component = world.Components.Get<NativeHostComponent>(entity);
            NativeHostVisualState desired = ResolveState(entity, in component);
            if (!_entries.TryGetValue(entity, out Entry entry))
            {
                NativeHostDescriptor descriptor = new(
                    entity,
                    world.Entities.GetScope(entity),
                    component.Kind,
                    desired);
                NativeHostHandle handle = _backend.CreateNativeHost(in descriptor);
                if (handle.IsNone || _owners.ContainsKey(handle))
                    throw new InvalidOperationException("Native-host backend returned an invalid or duplicate handle: " + handle);
                entry = new Entry(handle, component.Kind, desired);
                _entries.Add(entity, entry);
                _owners.Add(handle, entity);
            }
            else
            {
                if (entry.Kind != component.Kind)
                {
                    Destroy(entity, entry);
                    NativeHostDescriptor descriptor = new(
                        entity,
                        world.Entities.GetScope(entity),
                        component.Kind,
                        desired);
                    NativeHostHandle handle = _backend.CreateNativeHost(in descriptor);
                    entry = new Entry(handle, component.Kind, desired);
                    _entries[entity] = entry;
                    _owners[handle] = entity;
                }
                else
                {
                    NativeHostVisualState previousState = entry.State;
                    NativeHostMutationFlags flags = Diff(in previousState, in desired);
                    if (flags != NativeHostMutationFlags.None)
                    {
                        NativeHostMutation mutation = new(flags, desired);
                        _backend.UpdateNativeHost(entry.Handle, in mutation);
                        _entries[entity] = entry with { State = desired };
                    }
                }
            }
        }

        foreach ((UiEntity entity, Entry entry) in _entries.ToArray())
        {
            if (!_seen.Contains(entity))
                Destroy(entity, entry);
        }

        _backend.ReconcileNativeHostFocus(ResolveFocusedHost());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.Systems.Unregister(this);
        _world.EntityDestroying -= OnEntityDestroying;
        _backend.NativeHostEventRaised -= OnNativeHostEvent;
        foreach ((UiEntity entity, Entry entry) in _entries.ToArray())
            Destroy(entity, entry);
        lock (_eventGate)
            _pendingEvents.Clear();
        _frameEvents.Clear();
        _disposed = true;
    }

    private void DrainEvents(UiTimestamp now)
    {
        _frameEvents.Clear();
        while (true)
        {
            NativeHostEvent nativeEvent;
            lock (_eventGate)
            {
                if (!_pendingEvents.TryDequeue(out nativeEvent))
                    break;
            }
            if (!_owners.TryGetValue(nativeEvent.Handle, out UiEntity entity) || !_world.Entities.IsAlive(entity))
                continue;
            UiTimestamp timestamp = nativeEvent.Timestamp == UiTimestamp.Zero ? now : nativeEvent.Timestamp;
            _frameEvents.Add(new NativeHostFrameEvent(
                entity,
                nativeEvent.Kind,
                timestamp,
                nativeEvent.Value,
                nativeEvent.SelectionStart,
                nativeEvent.SelectionEnd));
            if (_input is null || !_input.InputRoots.TryResolve(entity, out UiInputRootId inputRoot))
                continue;
            if (nativeEvent.Kind == NativeHostEventKind.GotFocus &&
                UiEffectiveState.IsInteractive(_world, entity) &&
                !IsBlockedByInteractionBarrier(entity))
            {
                _input.Focus.Focus(entity, timestamp);
            }
            else if (nativeEvent.Kind == NativeHostEventKind.LostFocus && _input.Focus.GetFocused(inputRoot) == entity)
                _input.Focus.ClearFocus(inputRoot, timestamp);
        }
    }

    private NativeHostVisualState ResolveState(UiEntity entity, in NativeHostComponent component)
    {
        UiRect bounds = UiVisualGeometry.ResolveBounds(_world, entity);
        bool blocked = IsBlockedByInteractionBarrier(entity);
        bool visible = UiEffectiveState.IsVisible(_world, entity) && !blocked;
        bool enabled = UiEffectiveState.IsEnabled(_world, entity) && !blocked;
        bool focused = visible && enabled &&
                       _world.Components.TryGet(entity, out InteractionStateComponent interaction) &&
                       (interaction.Value & InteractionState.Focused) != 0;
        string value = component.Value ?? string.Empty;
        int start = Math.Clamp(component.SelectionStart, 0, value.Length);
        int end = Math.Clamp(component.SelectionEnd, start, value.Length);
        return new NativeHostVisualState(
            bounds,
            value,
            component.Placeholder ?? string.Empty,
            start,
            end,
            visible,
            enabled,
            focused,
            component.IsReadOnly,
            component.AcceptsReturn);
    }

    private bool IsInRoot(UiEntity entity)
    {
        if (!_world.Entities.TryGetScope(entity, out UiScopeId scope))
            return false;
        int guard = 0;
        while (_world.Scopes.IsAlive(scope) && guard++ < 1_000_000)
        {
            if (scope == _rootScope)
                return true;
            if (!_world.Scopes.TryGetParent(scope, out scope) || scope == UiScopeId.None)
                break;
        }
        return false;
    }

    private void OnNativeHostEvent(NativeHostEvent nativeEvent)
    {
        lock (_eventGate)
            _pendingEvents.Enqueue(nativeEvent);
        _world.Scheduler.RequestReactiveFrame();
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        if (_entries.TryGetValue(entity, out Entry entry))
            Destroy(entity, entry);
    }

    private void Destroy(UiEntity entity, Entry entry)
    {
        _backend.DestroyNativeHost(entry.Handle);
        _entries.Remove(entity);
        _owners.Remove(entry.Handle);
    }

    private NativeHostHandle ResolveFocusedHost()
    {
        if (_input is not null &&
            _input.InputRoots.TryResolve(_rootScope, out UiInputRootId inputRoot))
        {
            UiEntity focused = _input.Focus.GetFocused(inputRoot);
            return _entries.TryGetValue(focused, out Entry focusedEntry) && focusedEntry.State.IsFocused
                ? focusedEntry.Handle
                : NativeHostHandle.None;
        }

        foreach (Entry entry in _entries.Values)
        {
            if (entry.State.IsFocused)
                return entry.Handle;
        }
        return NativeHostHandle.None;
    }

    private bool IsBlockedByInteractionBarrier(UiEntity entity)
    {
        if (!_world.Entities.TryGetScope(entity, out UiScopeId entityScope))
            return true;
        ReadOnlySpan<UiEntity> barriers = _world.Components.Pool<UiInteractionBarrier>().Entities;
        UiInteractionBarrier selected = default;
        int selectedZ = int.MinValue;
        bool found = false;
        for (int i = 0; i < barriers.Length; i++)
        {
            UiEntity barrierEntity = barriers[i];
            if (!_world.Entities.IsAlive(barrierEntity) ||
                !IsInRoot(barrierEntity) ||
                !UiEffectiveState.IsVisible(_world, barrierEntity))
            {
                continue;
            }
            int z = _world.Components.TryGet(barrierEntity, out HitTestableComponent hit)
                ? hit.ZIndex
                : 0;
            if (found && z < selectedZ)
                continue;
            selected = _world.Components.Get<UiInteractionBarrier>(barrierEntity);
            selectedZ = z;
            found = true;
        }
        return found &&
               selected.OccludeNativeHosts &&
               !IsScopeWithin(entityScope, selected.AllowedScope);
    }

    private bool IsScopeWithin(UiScopeId scope, UiScopeId ancestor)
    {
        int guard = 0;
        while (_world.Scopes.IsAlive(scope) && guard++ < 1_000_000)
        {
            if (scope == ancestor)
                return true;
            if (!_world.Scopes.TryGetParent(scope, out scope) || scope.IsNone)
                break;
        }
        return false;
    }

    private static NativeHostMutationFlags Diff(
        in NativeHostVisualState previous,
        in NativeHostVisualState next)
    {
        NativeHostMutationFlags flags = NativeHostMutationFlags.None;
        if (previous.Bounds != next.Bounds) flags |= NativeHostMutationFlags.Bounds;
        if (!string.Equals(previous.Value, next.Value, StringComparison.Ordinal)) flags |= NativeHostMutationFlags.Value;
        if (!string.Equals(previous.Placeholder, next.Placeholder, StringComparison.Ordinal)) flags |= NativeHostMutationFlags.Placeholder;
        if (previous.SelectionStart != next.SelectionStart || previous.SelectionEnd != next.SelectionEnd) flags |= NativeHostMutationFlags.Selection;
        if (previous.IsVisible != next.IsVisible) flags |= NativeHostMutationFlags.Visibility;
        if (previous.IsEnabled != next.IsEnabled) flags |= NativeHostMutationFlags.Enabled;
        if (previous.IsFocused != next.IsFocused) flags |= NativeHostMutationFlags.Focus;
        if (previous.IsReadOnly != next.IsReadOnly) flags |= NativeHostMutationFlags.ReadOnly;
        if (previous.AcceptsReturn != next.AcceptsReturn) flags |= NativeHostMutationFlags.AcceptsReturn;
        return flags;
    }

    private readonly record struct Entry(
        NativeHostHandle Handle,
        UiNativeHostKind Kind,
        NativeHostVisualState State);
}
