// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Generation-safe identity for one window or independent input surface.</summary>
public readonly record struct UiInputRootId(int Index, uint Generation)
{
    public static UiInputRootId None => default;

    public bool IsNone => Index == 0 || Generation == 0;

    public override string ToString() =>
        IsNone ? "UiInputRoot(none)" : $"UiInputRoot({Index}@{Generation})";
}

/// <summary>
/// Explicitly maps Window/Input Surface scopes to input roots. Ordinary scope ancestry
/// never implicitly creates an input root; descendants resolve to the nearest registered root.
/// </summary>
public sealed class UiInputRootRegistry : IDisposable
{
    private const int InitialCapacity = 8;

    private readonly UiWorld _world;
    private uint[] _generations = new uint[InitialCapacity];
    private bool[] _alive = new bool[InitialCapacity];
    private UiScopeId[] _scopes = new UiScopeId[InitialCapacity];
    private IDisposable?[] _scopeRegistrations = new IDisposable?[InitialCapacity];
    private readonly Dictionary<UiScopeId, UiInputRootId> _byScope = [];
    private readonly Stack<int> _free = [];
    private int _highWater = 1;
    private bool _disposed;

    public UiInputRootRegistry(UiWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public event Action<UiInputRootId>? InputRootDestroying;

    public UiInputRootId Register(UiScopeId scope)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_world.Scopes.IsAlive(scope))
            throw new InvalidOperationException("Scope is not alive: " + scope);
        if (_byScope.TryGetValue(scope, out UiInputRootId existing) && IsAlive(existing))
            return existing;

        int index = AllocateSlot();
        uint generation = _generations[index];
        if (generation == 0)
            generation = 1;
        _generations[index] = generation;
        _alive[index] = true;
        _scopes[index] = scope;
        UiInputRootId root = new(index, generation);
        _byScope[scope] = root;
        _scopeRegistrations[index] = _world.Scopes.RegisterDisposeHandler(scope, _ => Unregister(root));
        return root;
    }

    public bool Unregister(UiInputRootId root)
    {
        if (!IsAlive(root))
            return false;

        InputRootDestroying?.Invoke(root);
        int index = root.Index;
        UiScopeId scope = _scopes[index];
        _scopeRegistrations[index]?.Dispose();
        _scopeRegistrations[index] = null;
        _byScope.Remove(scope);
        _alive[index] = false;
        _scopes[index] = UiScopeId.None;
        uint next = unchecked(_generations[index] + 1);
        _generations[index] = next == 0 ? 1 : next;
        _free.Push(index);
        return true;
    }

    public bool IsAlive(UiInputRootId root) =>
        !root.IsNone &&
        root.Index > 0 &&
        root.Index < _highWater &&
        _alive[root.Index] &&
        _generations[root.Index] == root.Generation;

    public UiScopeId GetScope(UiInputRootId root)
    {
        if (!IsAlive(root))
            throw new InvalidOperationException("Input root is stale or invalid: " + root);
        return _scopes[root.Index];
    }

    public bool TryResolve(UiScopeId scope, out UiInputRootId root)
    {
        UiScopeId current = scope;
        int guard = 0;
        while (_world.Scopes.IsAlive(current) && guard++ < 1_000_000)
        {
            if (_byScope.TryGetValue(current, out root) && IsAlive(root))
                return true;
            if (!_world.Scopes.TryGetParent(current, out current) || current.IsNone)
                break;
        }

        root = UiInputRootId.None;
        return false;
    }

    public bool TryResolve(UiEntity entity, out UiInputRootId root)
    {
        if (_world.Entities.TryGetScope(entity, out UiScopeId scope))
            return TryResolve(scope, out root);

        root = UiInputRootId.None;
        return false;
    }

    public bool Contains(UiInputRootId root, UiEntity entity) =>
        IsAlive(root) && TryResolve(entity, out UiInputRootId resolved) && resolved == root;

    public void Dispose()
    {
        if (_disposed)
            return;
        for (int i = 1; i < _highWater; i++)
        {
            if (_alive[i])
                Unregister(new UiInputRootId(i, _generations[i]));
        }

        InputRootDestroying = null;
        _byScope.Clear();
        _free.Clear();
        _disposed = true;
    }

    private int AllocateSlot()
    {
        if (_free.TryPop(out int free))
            return free;
        if (_highWater >= _generations.Length)
            Grow(_generations.Length * 2);
        return _highWater++;
    }

    private void Grow(int capacity)
    {
        Array.Resize(ref _generations, capacity);
        Array.Resize(ref _alive, capacity);
        Array.Resize(ref _scopes, capacity);
        Array.Resize(ref _scopeRegistrations, capacity);
    }
}

internal readonly record struct UiPointerKey(UiInputRootId InputRoot, int PointerId);
