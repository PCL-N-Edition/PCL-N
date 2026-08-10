// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Typed component pool registry for a <see cref="UiWorld"/>.
/// Hot paths should cache the returned <see cref="ComponentPool{T}"/> reference.
/// </summary>
public sealed class ComponentStore
{
    private readonly Dictionary<Type, IComponentPool> _pools = new();

    public ComponentPool<T> Pool<T>() where T : struct
    {
        Type type = typeof(T);
        if (_pools.TryGetValue(type, out IComponentPool? existing))
            return ((ComponentPoolAdapter<T>)existing).Pool;

        var pool = new ComponentPool<T>();
        _pools[type] = new ComponentPoolAdapter<T>(pool);
        return pool;
    }

    public bool RemoveAll(UiEntity entity)
    {
        bool removed = false;
        foreach (IComponentPool pool in _pools.Values)
            removed |= pool.Remove(entity);
        return removed;
    }

    public void ClearAll()
    {
        foreach (IComponentPool pool in _pools.Values)
            pool.Clear();
    }

    private interface IComponentPool
    {
        bool Remove(UiEntity entity);
        void Clear();
    }

    private sealed class ComponentPoolAdapter<T> : IComponentPool where T : struct
    {
        public ComponentPoolAdapter(ComponentPool<T> pool) => Pool = pool;

        public ComponentPool<T> Pool { get; }

        public bool Remove(UiEntity entity) => Pool.Remove(entity);

        public void Clear() => Pool.Clear();
    }
}
