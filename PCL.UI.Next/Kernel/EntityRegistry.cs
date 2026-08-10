// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Entity slot allocator with generation validation, free-list reuse, and scope ownership.
/// Index 0 is reserved as <see cref="UiEntity.None"/>.
/// </summary>
public sealed class EntityRegistry
{
    private const int InitialCapacity = 64;

    private uint[] _generations = new uint[InitialCapacity];
    private bool[] _alive = new bool[InitialCapacity];
    private UiScopeId[] _scopes = new UiScopeId[InitialCapacity];
    private readonly Stack<int> _free = new();
    private int _highWater = 1; // next never-used index when free list empty
    private int _aliveCount;

    public int AliveCount => _aliveCount;

    public int Capacity => _generations.Length;

    public UiEntity Create(UiScopeId scope)
    {
        if (scope.IsNone)
            throw new ArgumentException("Entity must be created inside a live scope.", nameof(scope));

        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else
        {
            if (_highWater >= _generations.Length)
                Grow(_generations.Length * 2);
            index = _highWater++;
        }

        uint generation = _generations[index];
        if (generation == 0)
            generation = 1;
        _generations[index] = generation;
        _alive[index] = true;
        _scopes[index] = scope;
        _aliveCount++;
        return new UiEntity(index, generation);
    }

    public bool IsAlive(UiEntity entity)
    {
        if (entity.IsNone || entity.Index <= 0 || entity.Index >= _highWater)
            return false;
        return _alive[entity.Index] && _generations[entity.Index] == entity.Generation;
    }

    public bool TryGetScope(UiEntity entity, out UiScopeId scope)
    {
        if (!IsAlive(entity))
        {
            scope = UiScopeId.None;
            return false;
        }

        scope = _scopes[entity.Index];
        return true;
    }

    public UiScopeId GetScope(UiEntity entity)
    {
        if (!TryGetScope(entity, out UiScopeId scope))
            throw new InvalidOperationException("Entity is not alive: " + entity);
        return scope;
    }

    /// <summary>
    /// Destroys a single entity slot. Callers must detach hierarchy / components first.
    /// Returns false if the handle was already stale.
    /// </summary>
    public bool Destroy(UiEntity entity)
    {
        if (!IsAlive(entity))
            return false;

        int index = entity.Index;
        _alive[index] = false;
        _scopes[index] = UiScopeId.None;
        uint nextGen = unchecked(_generations[index] + 1);
        if (nextGen == 0)
            nextGen = 1;
        _generations[index] = nextGen;
        _free.Push(index);
        _aliveCount--;
        return true;
    }

    public void AppendAliveEntities(List<UiEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        for (int i = 1; i < _highWater; i++)
        {
            if (_alive[i])
                destination.Add(new UiEntity(i, _generations[i]));
        }
    }

    public void AppendAliveInScope(UiScopeId scope, List<UiEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (scope.IsNone)
            return;

        for (int i = 1; i < _highWater; i++)
        {
            if (_alive[i] && _scopes[i] == scope)
                destination.Add(new UiEntity(i, _generations[i]));
        }
    }

    private void Grow(int newCapacity)
    {
        Array.Resize(ref _generations, newCapacity);
        Array.Resize(ref _alive, newCapacity);
        Array.Resize(ref _scopes, newCapacity);
    }
}
