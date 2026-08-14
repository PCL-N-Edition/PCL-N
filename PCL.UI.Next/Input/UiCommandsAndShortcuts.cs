// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiCommandTrigger : byte
{
    Pointer = 0,
    Keyboard = 1,
    Shortcut = 2,
    Gesture = 3,
    Accessibility = 4
}

public readonly record struct UiCommandInvocation(
    UiCommand Command,
    UiEntity Source,
    UiScopeId Scope,
    UiCommandTrigger Trigger,
    UiTimestamp Timestamp);

/// <summary>Runtime-to-presentation FIFO command boundary.</summary>
public sealed class UiCommandQueue
{
    private readonly Queue<UiCommandInvocation> _queue = new();

    public int Count => _queue.Count;

    public void Enqueue(in UiCommandInvocation invocation) => _queue.Enqueue(invocation);

    public bool TryDequeue(out UiCommandInvocation invocation) => _queue.TryDequeue(out invocation);

    public int Drain(List<UiCommandInvocation> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int count = 0;
        while (_queue.TryDequeue(out UiCommandInvocation invocation))
        {
            destination.Add(invocation);
            count++;
        }

        return count;
    }

    public void Clear() => _queue.Clear();
}

/// <summary>Central shortcut registry; widgets never subscribe to keyboard events individually.</summary>
public sealed class UiShortcutRegistry : IDisposable
{
    private readonly UiWorld _world;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<Entry, IDisposable> _scopeRegistrations = [];
    private long _nextOrder;
    private bool _disposed;

    public UiShortcutRegistry(UiWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public IDisposable Register(UiScopeId scope, UiKeyGesture gesture, UiCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_world.Scopes.IsAlive(scope))
            throw new InvalidOperationException("Scope is not alive: " + scope);
        if (gesture.Key == UiKey.None)
            throw new ArgumentOutOfRangeException(nameof(gesture));
        if (command.IsNone)
            throw new ArgumentOutOfRangeException(nameof(command));

        Entry entry = new(scope, gesture, command, _nextOrder++);
        _entries.Add(entry);
        IDisposable scopeRegistration = _world.Scopes.RegisterDisposeHandler(
            scope,
            _ => Remove(entry));
        _scopeRegistrations[entry] = scopeRegistration;
        return new Registration(this, entry);
    }

    public bool TryResolve(UiScopeId eventScope, in UiKeyEvent keyEvent, out UiCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Entry? best = null;
        int bestDepth = -1;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Gesture.Matches(in keyEvent))
                continue;
            int depth = ScopeDistance(eventScope, entry.Scope);
            if (depth < 0)
                continue;
            if (best is null || depth < bestDepth || (depth == bestDepth && entry.Order > best.Value.Order))
            {
                best = entry;
                bestDepth = depth;
            }
        }

        if (best is { } match)
        {
            command = match.Command;
            return true;
        }

        command = UiCommand.None;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (IDisposable registration in _scopeRegistrations.Values)
            registration.Dispose();
        _scopeRegistrations.Clear();
        _entries.Clear();
        _disposed = true;
    }

    private int ScopeDistance(UiScopeId scope, UiScopeId ancestor)
    {
        UiScopeId current = scope;
        int distance = 0;
        while (!current.IsNone && distance < 1_000_000)
        {
            if (current == ancestor)
                return distance;
            if (!_world.Scopes.TryGetParent(current, out current))
                break;
            distance++;
        }

        return -1;
    }

    private void Remove(Entry entry)
    {
        _entries.Remove(entry);
        if (_scopeRegistrations.Remove(entry, out IDisposable? registration))
            registration.Dispose();
    }

    private readonly record struct Entry(
        UiScopeId Scope,
        UiKeyGesture Gesture,
        UiCommand Command,
        long Order);

    private sealed class Registration : IDisposable
    {
        private UiShortcutRegistry? _owner;
        private readonly Entry _entry;

        public Registration(UiShortcutRegistry owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public void Dispose()
        {
            UiShortcutRegistry? owner = _owner;
            if (owner is null)
                return;
            _owner = null;
            owner.Remove(_entry);
        }
    }
}
