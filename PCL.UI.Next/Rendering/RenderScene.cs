// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Runtime-owned retained scene. Stored state is the last state emitted to the backend;
/// mutations are generated only when a field changes.
/// </summary>
public sealed class RenderScene
{
    private readonly Dictionary<UiEntity, RenderNodeId> _byEntity = [];
    private readonly List<Entry> _entries = [default];
    private readonly Stack<int> _free = [];
    private int _count;

    public int NodeCount => _count;

    public bool TryGetNode(UiEntity entity, out RenderNodeId node) =>
        _byEntity.TryGetValue(entity, out node) && IsAlive(node);

    public bool TryGetNode(RenderNodeId node, out UiRenderNodeSnapshot snapshot)
    {
        if (!TryGet(node, out Entry entry))
        {
            snapshot = default;
            return false;
        }

        snapshot = ToSnapshot(node, entry.State);
        return true;
    }

    public void CopyNodesTo(List<UiRenderNodeSnapshot> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        for (int i = 1; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Alive)
                continue;
            RenderNodeId id = new(i, entry.Generation);
            destination.Add(ToSnapshot(id, entry.State));
        }
    }

    internal RenderNodeId Apply(in RenderNodeState desired, List<RenderMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (_byEntity.TryGetValue(desired.Owner, out RenderNodeId existing) &&
            TryGet(existing, out Entry existingEntry))
        {
            Update(existing, existingEntry, in desired, mutations);
            return existing;
        }

        int index = Allocate();
        Entry entry = _entries[index];
        entry.Alive = true;
        entry.State = desired;
        _entries[index] = entry;
        RenderNodeId node = new(index, entry.Generation);
        _byEntity[desired.Owner] = node;
        _count++;

        mutations.Add(RenderMutation.Create(node, desired.Owner, desired.Kind));
        mutations.Add(RenderMutation.SetParent(node, desired.Parent));
        mutations.Add(RenderMutation.SetZOrder(node, desired.ZOrder));
        mutations.Add(RenderMutation.SetBounds(node, desired.Bounds));
        mutations.Add(RenderMutation.SetTransform(node, desired.Transform));
        mutations.Add(RenderMutation.SetOpacity(node, desired.Opacity));
        mutations.Add(RenderMutation.SetBrush(node, desired.Brush));
        mutations.Add(RenderMutation.SetCornerRadius(node, desired.CornerRadius));
        if (desired.Kind == UiRenderNodeKind.Text)
            mutations.Add(RenderMutation.SetTextLayout(node, desired.TextLayout));
        return node;
    }

    internal bool RemoveEntity(UiEntity entity, List<RenderMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (!_byEntity.TryGetValue(entity, out RenderNodeId node) ||
            !TryGet(node, out Entry entry))
        {
            return false;
        }

        Destroy(node, entry, mutations);
        return true;
    }

    internal void RemoveMissing(HashSet<UiEntity> retained, List<RenderMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(mutations);
        int remaining;
        do
        {
            remaining = 0;
            bool removed = false;
            for (int i = 1; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (!entry.Alive || retained.Contains(entry.State.Owner))
                    continue;
                remaining++;
                RenderNodeId node = new(i, entry.Generation);
                if (HasMissingChild(node, retained))
                    continue;
                Destroy(node, entry, mutations);
                remaining--;
                removed = true;
            }

            if (remaining > 0 && !removed)
                throw new InvalidOperationException("Render scene contains a parent cycle.");
        }
        while (remaining > 0);
    }

    private void Update(
        RenderNodeId node,
        Entry entry,
        in RenderNodeState desired,
        List<RenderMutation> mutations)
    {
        RenderNodeState current = entry.State;
        if (current.Kind != desired.Kind)
            mutations.Add(RenderMutation.SetNodeKind(node, desired.Kind));
        if (current.Parent != desired.Parent)
            mutations.Add(RenderMutation.SetParent(node, desired.Parent));
        if (current.ZOrder != desired.ZOrder)
            mutations.Add(RenderMutation.SetZOrder(node, desired.ZOrder));
        if (current.Bounds != desired.Bounds)
            mutations.Add(RenderMutation.SetBounds(node, desired.Bounds));
        if (current.Transform != desired.Transform)
            mutations.Add(RenderMutation.SetTransform(node, desired.Transform));
        if (!current.Opacity.Equals(desired.Opacity))
            mutations.Add(RenderMutation.SetOpacity(node, desired.Opacity));
        if (current.Brush != desired.Brush)
            mutations.Add(RenderMutation.SetBrush(node, desired.Brush));
        if (!current.CornerRadius.Equals(desired.CornerRadius))
            mutations.Add(RenderMutation.SetCornerRadius(node, desired.CornerRadius));
        if (desired.Kind == UiRenderNodeKind.Text && current.TextLayout != desired.TextLayout)
            mutations.Add(RenderMutation.SetTextLayout(node, desired.TextLayout));

        entry.State = desired;
        _entries[node.Index] = entry;
    }

    private void Destroy(RenderNodeId node, Entry entry, List<RenderMutation> mutations)
    {
        mutations.Add(RenderMutation.Destroy(node));
        _byEntity.Remove(entry.State.Owner);
        entry.Alive = false;
        entry.State = default;
        entry.Generation = NextGeneration(entry.Generation);
        _entries[node.Index] = entry;
        _free.Push(node.Index);
        _count--;
    }

    private bool HasMissingChild(RenderNodeId parent, HashSet<UiEntity> retained)
    {
        for (int i = 1; i < _entries.Count; i++)
        {
            Entry candidate = _entries[i];
            if (candidate.Alive &&
                candidate.State.Parent == parent &&
                !retained.Contains(candidate.State.Owner))
            {
                return true;
            }
        }
        return false;
    }

    private int Allocate()
    {
        if (_free.Count > 0)
            return _free.Pop();
        _entries.Add(new Entry { Generation = 1 });
        return _entries.Count - 1;
    }

    private bool IsAlive(RenderNodeId node) => TryGet(node, out _);

    private bool TryGet(RenderNodeId node, out Entry entry)
    {
        if (node.IsNone || node.Index >= _entries.Count)
        {
            entry = default;
            return false;
        }

        entry = _entries[node.Index];
        return entry.Alive && entry.Generation == node.Generation;
    }

    private static UiRenderNodeSnapshot ToSnapshot(RenderNodeId id, RenderNodeState state) =>
        new(
            id,
            state.Owner,
            state.Kind,
            state.Parent,
            state.ZOrder,
            state.Bounds,
            state.Transform,
            state.Opacity,
            state.Brush,
            state.CornerRadius,
            state.TextLayout);

    private static uint NextGeneration(uint generation)
    {
        uint next = unchecked(generation + 1);
        return next == 0 ? 1 : next;
    }

    private struct Entry
    {
        public uint Generation { get; set; }
        public bool Alive { get; set; }
        public RenderNodeState State { get; set; }
    }
}
