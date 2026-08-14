// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Per-entity dirty flags with per-flag active sets for reactive system dispatch.
/// <para>
/// TODO (hot path): replace Dictionary + HashSet with PackedDirtySet / SparseSet.
/// Collect reuses its scratch set and does not allocate after capacity warm-up.
/// </para>
/// </summary>
public sealed class DirtyTracker
{
    private readonly EntityRegistry _entities;
    private readonly UiDiagnostics? _diagnostics;
    private readonly Dictionary<int, DirtyEntry> _byIndex = new();
    private readonly HashSet<int>[] _sets;
    private readonly HashSet<int> _collectSeen = [];

    public DirtyTracker(EntityRegistry entities, UiDiagnostics? diagnostics = null)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _diagnostics = diagnostics;
        int flagCount = 32;
        _sets = new HashSet<int>[flagCount];
        for (int i = 0; i < flagCount; i++)
            _sets[i] = new HashSet<int>();
    }

    public bool HasAny => _byIndex.Count > 0;

    public void Mark(UiEntity entity, UiDirtyFlags flags, UiEntity source = default)
    {
        if (flags == UiDirtyFlags.None || !_entities.IsAlive(entity))
            return;

        UiDirtyFlags effective;

        if (!_byIndex.TryGetValue(entity.Index, out DirtyEntry entry))
        {
            entry = new DirtyEntry(entity, flags);
            _byIndex[entity.Index] = entry;
        }
        else
        {
            // Stale generation — replace.
            if (entry.Entity.Generation != entity.Generation)
            {
                RemoveIndexFromSets(entity.Index, entry.Flags);
                entry = new DirtyEntry(entity, flags);
            }
            else
            {
                entry = entry with { Flags = entry.Flags | flags };
            }

            _byIndex[entity.Index] = entry;
        }

        AddIndexToSets(entity.Index, flags);
        effective = _byIndex[entity.Index].Flags;
        _diagnostics?.DirtyMarked(entity, source, flags, effective);
    }

    public UiDirtyFlags GetFlags(UiEntity entity)
    {
        if (!_byIndex.TryGetValue(entity.Index, out DirtyEntry entry))
            return UiDirtyFlags.None;
        if (entry.Entity != entity)
            return UiDirtyFlags.None;
        return entry.Flags;
    }

    public bool Any(UiDirtyFlags mask)
    {
        if (mask == UiDirtyFlags.None)
            return false;
        for (int bit = 0; bit < 32; bit++)
        {
            if (((uint)mask & (1u << bit)) == 0)
                continue;
            if (_sets[bit].Count > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Copies live entities that have any of <paramref name="mask"/> set.
    /// Stale handles are purged.
    /// </summary>
    public void Collect(UiDirtyFlags mask, List<UiEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (mask == UiDirtyFlags.None)
            return;

        _collectSeen.Clear();
        for (int bit = 0; bit < 32; bit++)
        {
            if (((uint)mask & (1u << bit)) == 0)
                continue;
            foreach (int index in _sets[bit])
            {
                if (!_collectSeen.Add(index))
                    continue;
                if (!_byIndex.TryGetValue(index, out DirtyEntry entry))
                    continue;
                if (!_entities.IsAlive(entry.Entity) || (entry.Flags & mask) == 0)
                {
                    PurgeIndex(index);
                    continue;
                }

                destination.Add(entry.Entity);
            }
        }
    }

    public void Clear(UiEntity entity, UiDirtyFlags mask)
    {
        if (!_byIndex.TryGetValue(entity.Index, out DirtyEntry entry) || entry.Entity != entity)
            return;

        UiDirtyFlags remaining = entry.Flags & ~mask;
        RemoveIndexFromSets(entity.Index, entry.Flags & mask);
        if (remaining == UiDirtyFlags.None)
            _byIndex.Remove(entity.Index);
        else
            _byIndex[entity.Index] = entry with { Flags = remaining };
    }

    public void ClearAll(UiDirtyFlags mask)
    {
        if (mask == UiDirtyFlags.None)
            return;

        List<int> indices = [.. _byIndex.Keys];
        foreach (int index in indices)
        {
            DirtyEntry entry = _byIndex[index];
            UiDirtyFlags remaining = entry.Flags & ~mask;
            RemoveIndexFromSets(index, entry.Flags & mask);
            if (remaining == UiDirtyFlags.None)
                _byIndex.Remove(index);
            else
                _byIndex[index] = entry with { Flags = remaining };
        }
    }

    public void ClearEverything()
    {
        _byIndex.Clear();
        _collectSeen.Clear();
        for (int i = 0; i < _sets.Length; i++)
            _sets[i].Clear();
    }

    public void RemoveEntity(UiEntity entity)
    {
        if (!_byIndex.TryGetValue(entity.Index, out DirtyEntry entry))
            return;
        if (entry.Entity.Generation != entity.Generation && entry.Entity.Index == entity.Index)
        {
            // Different generation on same slot — only purge if indices match for full clear on destroy.
        }

        PurgeIndex(entity.Index);
    }

    private void PurgeIndex(int index)
    {
        if (!_byIndex.TryGetValue(index, out DirtyEntry entry))
            return;
        RemoveIndexFromSets(index, entry.Flags);
        _byIndex.Remove(index);
    }

    private void AddIndexToSets(int index, UiDirtyFlags flags)
    {
        for (int bit = 0; bit < 32; bit++)
        {
            if (((uint)flags & (1u << bit)) != 0)
                _sets[bit].Add(index);
        }
    }

    private void RemoveIndexFromSets(int index, UiDirtyFlags flags)
    {
        for (int bit = 0; bit < 32; bit++)
        {
            if (((uint)flags & (1u << bit)) != 0)
                _sets[bit].Remove(index);
        }
    }

    private readonly record struct DirtyEntry(UiEntity Entity, UiDirtyFlags Flags);
}
