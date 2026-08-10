// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Sparse-index + packed dense array component storage (architecture §10).
/// Sparse[entity.Index] stores denseIndex+1 (0 = absent).
/// </summary>
public sealed class ComponentPool<T> where T : struct
{
    private const int InitialCapacity = 32;

    private int[] _sparse = new int[InitialCapacity];
    private UiEntity[] _denseEntities = new UiEntity[InitialCapacity];
    private T[] _dense = new T[InitialCapacity];
    private int _count;

    public int Count => _count;

    public bool Has(UiEntity entity)
    {
        if (entity.IsNone || entity.Index <= 0)
            return false;
        if (entity.Index >= _sparse.Length)
            return false;
        int packed = _sparse[entity.Index];
        if (packed == 0)
            return false;
        int dense = packed - 1;
        return dense < _count && _denseEntities[dense] == entity;
    }

    public ref T Get(UiEntity entity)
    {
        if (!TryGetDenseIndex(entity, out int dense))
            throw new InvalidOperationException($"Entity {entity} has no component {typeof(T).Name}.");
        return ref _dense[dense];
    }

    public bool TryGet(UiEntity entity, out T component)
    {
        if (!TryGetDenseIndex(entity, out int dense))
        {
            component = default;
            return false;
        }

        component = _dense[dense];
        return true;
    }

    public void Add(UiEntity entity, in T component)
    {
        if (entity.IsNone || entity.Index <= 0)
            throw new ArgumentException("Cannot add component to None entity.", nameof(entity));
        if (Has(entity))
            throw new InvalidOperationException($"Entity {entity} already has component {typeof(T).Name}.");

        EnsureSparse(entity.Index);
        EnsureDense(_count + 1);

        int dense = _count++;
        _denseEntities[dense] = entity;
        _dense[dense] = component;
        _sparse[entity.Index] = dense + 1;
    }

    public void Set(UiEntity entity, in T component)
    {
        if (TryGetDenseIndex(entity, out int dense))
        {
            _dense[dense] = component;
            return;
        }

        Add(entity, in component);
    }

    public bool Remove(UiEntity entity)
    {
        if (!TryGetDenseIndex(entity, out int dense))
            return false;

        int last = _count - 1;
        if (dense != last)
        {
            UiEntity moved = _denseEntities[last];
            _denseEntities[dense] = moved;
            _dense[dense] = _dense[last];
            _sparse[moved.Index] = dense + 1;
        }

        _denseEntities[last] = UiEntity.None;
        _dense[last] = default;
        _sparse[entity.Index] = 0;
        _count = last;
        return true;
    }

    public void Clear()
    {
        Array.Clear(_sparse, 0, _sparse.Length);
        Array.Clear(_denseEntities, 0, _count);
        Array.Clear(_dense, 0, _count);
        _count = 0;
    }

    public ReadOnlySpan<UiEntity> Entities => _denseEntities.AsSpan(0, _count);

    public ReadOnlySpan<T> Components => _dense.AsSpan(0, _count);

    public void CopyEntitiesTo(List<UiEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        for (int i = 0; i < _count; i++)
            destination.Add(_denseEntities[i]);
    }

    private bool TryGetDenseIndex(UiEntity entity, out int dense)
    {
        dense = 0;
        if (entity.IsNone || entity.Index <= 0 || entity.Index >= _sparse.Length)
            return false;
        int packed = _sparse[entity.Index];
        if (packed == 0)
            return false;
        dense = packed - 1;
        return dense < _count && _denseEntities[dense] == entity;
    }

    private void EnsureSparse(int entityIndex)
    {
        if (entityIndex < _sparse.Length)
            return;
        int capacity = _sparse.Length;
        while (capacity <= entityIndex)
            capacity *= 2;
        Array.Resize(ref _sparse, capacity);
    }

    private void EnsureDense(int required)
    {
        if (required <= _dense.Length)
            return;
        int capacity = _dense.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref _denseEntities, capacity);
        Array.Resize(ref _dense, capacity);
    }
}
