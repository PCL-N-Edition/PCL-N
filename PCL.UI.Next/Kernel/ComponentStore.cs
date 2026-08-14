// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Typed component pool registry for a <see cref="UiWorld"/>.
/// All mutations validate entity generation via <see cref="EntityRegistry"/>.
/// Hot paths may cache <see cref="Pool{T}"/> for reads; mutations should still go through
/// <see cref="Add{T}"/> / <see cref="Set{T}"/> unless the caller already called EnsureAlive.
/// </summary>
public sealed class ComponentStore
{
    private readonly EntityRegistry _entities;
    private readonly Dictionary<Type, IComponentPool> _pools = new();

    public ComponentStore(EntityRegistry entities)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
    }

    /// <summary>Read-only pool access (Has/Get/TryGet/enumerate).</summary>
    public ComponentPool<T> Pool<T>() where T : struct
    {
        Type type = typeof(T);
        if (_pools.TryGetValue(type, out IComponentPool? existing))
            return ((ComponentPoolAdapter<T>)existing).Pool;

        var pool = new ComponentPool<T>();
        _pools[type] = new ComponentPoolAdapter<T>(pool);
        return pool;
    }

    public void Add<T>(UiEntity entity, in T component) where T : struct
    {
        _entities.EnsureAlive(entity);
        Pool<T>().UnsafeAdd(entity, in component);
    }

    public void Set<T>(UiEntity entity, in T component) where T : struct
    {
        _entities.EnsureAlive(entity);
        Pool<T>().UnsafeSet(entity, in component);
    }

    public bool Remove<T>(UiEntity entity) where T : struct
    {
        // Removal is generation-sensitive via dense entity match; allow no-op on stale.
        return Pool<T>().UnsafeRemove(entity);
    }

    public bool Has<T>(UiEntity entity) where T : struct => Pool<T>().Has(entity);

    public ref T Get<T>(UiEntity entity) where T : struct => ref Pool<T>().Get(entity);

    public bool TryGet<T>(UiEntity entity, out T component) where T : struct =>
        Pool<T>().TryGet(entity, out component);

    public bool RemoveAll(UiEntity entity)
    {
        bool removed = false;
        foreach (IComponentPool pool in _pools.Values)
            removed |= pool.Remove(entity);
        return removed;
    }

    /// <summary>Diagnostics-only component type enumeration without reflecting over values.</summary>
    public void CopyComponentTypes(UiEntity entity, List<Type> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!_entities.IsAlive(entity))
            return;
        foreach (IComponentPool pool in _pools.Values)
        {
            if (pool.Has(entity))
                destination.Add(pool.ComponentType);
        }
    }

    public void ClearAll()
    {
        foreach (IComponentPool pool in _pools.Values)
            pool.Clear();
    }

    private interface IComponentPool
    {
        Type ComponentType { get; }
        bool Has(UiEntity entity);
        bool Remove(UiEntity entity);
        void Clear();
    }

    private sealed class ComponentPoolAdapter<T> : IComponentPool where T : struct
    {
        public ComponentPoolAdapter(ComponentPool<T> pool) => Pool = pool;

        public ComponentPool<T> Pool { get; }

        public Type ComponentType => typeof(T);

        public bool Has(UiEntity entity) => Pool.Has(entity);

        public bool Remove(UiEntity entity) => Pool.UnsafeRemove(entity);

        public void Clear() => Pool.Clear();
    }
}
