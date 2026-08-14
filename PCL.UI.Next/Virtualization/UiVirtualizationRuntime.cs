// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Realizes a bounded viewport window over an arbitrarily large logical item source.
/// Item slots keep their entities and blueprint instances while their presentation data is rebound.
/// </summary>
public sealed class UiVirtualizationRuntime : IDisposable
{
    private readonly UiWorld _world;
    private readonly UiScrollRuntime _scroll;
    private readonly VirtualizationUpdateSystem _system;
    private readonly Dictionary<UiEntity, ListState> _states = [];
    private readonly List<ListState> _stateScratch = [];
    private bool _disposed;

    public UiVirtualizationRuntime(UiWorld world, UiScrollRuntime scroll)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _scroll = scroll ?? throw new ArgumentNullException(nameof(scroll));
        _system = new VirtualizationUpdateSystem(this);
        _world.Systems.Register(_system);
        _world.EntityDestroying += OnEntityDestroying;
    }

    public UiVirtualListRegistration Register(
        UiEntity host,
        IUiVirtualItemSource source,
        UiBlueprint itemTemplate)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(itemTemplate);
        EnsureVirtualList(host);
        if (_states.ContainsKey(host))
            throw new InvalidOperationException("A virtual item source is already registered for " + host);
        if (_world.Hierarchy.TryGetNode(host, out HierarchyNode hostNode) && hostNode.FirstChild != UiEntity.None)
            throw new InvalidOperationException("A virtual list host cannot contain static children.");
        if (source.Count < 0)
            throw new InvalidOperationException("A virtual item source cannot report a negative count.");

        Virtualization policy = _world.Components.Get<Virtualization>(host);
        ScrollViewport viewport = _world.Components.Get<ScrollViewport>(host);
        UiEntity content = CreateContent(host, viewport.Orientation, source.Count * policy.EstimatedItemExtent);
        ListState state = new(
            host,
            content,
            source,
            itemTemplate,
            viewport.Orientation,
            policy,
            new VariableExtentIndex(source.Count, policy.EstimatedItemExtent),
            BuildKeys(source, source.Count));
        _states.Add(host, state);
        UpdateContentExtent(state, state.Extents.TotalExtent, anchoredOffset: null);
        LayoutInvalidation.MarkMeasure(_world, host);
        return new UiVirtualListRegistration(() => Unregister(host, state));
    }

    public bool TryGetSnapshot(UiEntity host, out UiVirtualizationSnapshot snapshot)
    {
        if (_states.TryGetValue(host, out ListState? state))
        {
            snapshot = state.Snapshot;
            return true;
        }
        snapshot = default;
        return false;
    }

    /// <summary>Requests a plan pass after the registered source advances its Version.</summary>
    public void Invalidate(UiEntity host)
    {
        ThrowIfDisposed();
        if (!_states.ContainsKey(host))
            throw new InvalidOperationException("Virtual list is not registered: " + host);
        _world.Scheduler.RequestReactiveFrame();
    }

    public bool TryGetRealizedEntity(UiEntity host, int logicalIndex, out UiEntity entity)
    {
        if (_states.TryGetValue(host, out ListState? state))
        {
            for (int i = 0; i < state.Slots.Count; i++)
            {
                ItemSlot slot = state.Slots[i];
                if (slot.IsRealized && slot.LogicalIndex == logicalIndex && slot.Instance.IsAlive)
                {
                    entity = slot.Instance.RootEntity;
                    return _world.Entities.IsAlive(entity);
                }
            }
        }
        entity = UiEntity.None;
        return false;
    }

    public void ScrollIntoView(
        UiEntity host,
        int logicalIndex,
        UiScrollAlignment alignment = UiScrollAlignment.Nearest,
        bool animated = true)
    {
        if (!_states.TryGetValue(host, out ListState? state))
            throw new InvalidOperationException("Virtual list is not registered: " + host);
        if ((uint)logicalIndex >= (uint)state.Extents.Count)
            throw new ArgumentOutOfRangeException(nameof(logicalIndex));
        ScrollState scroll = _scroll.GetState(host);
        float start = state.Extents.GetOffset(logicalIndex);
        float end = start + state.Extents.GetExtent(logicalIndex);
        float target = alignment switch
        {
            UiScrollAlignment.Start => start,
            UiScrollAlignment.Center => start - ((scroll.Viewport - (end - start)) * 0.5f),
            UiScrollAlignment.End => end - scroll.Viewport,
            _ when start < scroll.Offset => start,
            _ when end > scroll.Offset + scroll.Viewport => end - scroll.Viewport,
            _ => scroll.Offset
        };
        _scroll.ScrollTo(host, target, animated);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.Systems.Unregister(_system);
        _world.EntityDestroying -= OnEntityDestroying;
        foreach (ListState state in _states.Values.ToArray())
            DestroyState(state, destroyContent: _world.Entities.IsAlive(state.Content));
        _states.Clear();
        _disposed = true;
    }

    internal void Update()
    {
        _stateScratch.Clear();
        _stateScratch.AddRange(_states.Values);
        for (int i = 0; i < _stateScratch.Count; i++)
        {
            ListState state = _stateScratch[i];
            if (!_world.Entities.IsAlive(state.Host))
                continue;
            RefreshSource(state);
            MeasureRealized(state);
            PlanRealization(state);
        }
    }

    private void RefreshSource(ListState state)
    {
        int count = state.Source.Count;
        if (count < 0)
            throw new InvalidOperationException("A virtual item source cannot report a negative count.");
        if (state.Source.Version == state.SourceVersion && count == state.Extents.Count)
            return;

        ScrollState scroll = _scroll.GetState(state.Host);
        int oldAnchor = state.Extents.FindIndexAtOffset(scroll.Offset);
        long anchorKey = oldAnchor >= 0 ? state.Keys[oldAnchor] : 0;
        float localOffset = oldAnchor >= 0 ? scroll.Offset - state.Extents.GetOffset(oldAnchor) : 0f;

        state.Extents.Reset(count, state.Policy.EstimatedItemExtent);
        long[] nextKeys = BuildKeys(state.Source, count);
        for (int i = 0; i < count; i++)
        {
            long key = nextKeys[i];
            if (state.MeasuredByKey.TryGetValue(key, out float extent))
                state.Extents.SetMeasuredExtent(i, extent);
        }
        for (int i = 0; i < state.Slots.Count; i++)
            state.Slots[i].LogicalIndex = -1;
        state.Keys = nextKeys;
        state.SourceVersion = state.Source.Version;

        float? anchored = null;
        if (oldAnchor >= 0 && state.Source.TryGetIndex(anchorKey, out int nextAnchor) &&
            (uint)nextAnchor < (uint)count)
        {
            anchored = state.Extents.GetOffset(nextAnchor) + localOffset;
        }
        UpdateContentExtent(state, state.Extents.TotalExtent, anchored);
    }

    private void MeasureRealized(ListState state)
    {
        ScrollState scroll = _scroll.GetState(state.Host);
        int anchor = state.Extents.FindIndexAtOffset(scroll.Offset);
        float anchorLocal = anchor >= 0 ? scroll.Offset - state.Extents.GetOffset(anchor) : 0f;
        bool changed = false;
        for (int i = 0; i < state.Slots.Count; i++)
        {
            ItemSlot slot = state.Slots[i];
            if (!slot.IsRealized || !slot.Instance.IsAlive ||
                (uint)slot.LogicalIndex >= (uint)state.Extents.Count ||
                !_world.Components.TryGet(slot.Instance.RootEntity, out DesiredSize desired))
            {
                continue;
            }
            float extent = state.Orientation == UiOrientation.Vertical
                ? desired.Value.Height
                : desired.Value.Width;
            if (!float.IsFinite(extent) || extent <= 0f)
                continue;
            state.MeasuredByKey[slot.Key] = extent;
            changed |= state.Extents.SetMeasuredExtent(slot.LogicalIndex, extent);
        }
        if (!changed)
            return;
        float? anchored = anchor >= 0 ? state.Extents.GetOffset(anchor) + anchorLocal : null;
        UpdateContentExtent(state, state.Extents.TotalExtent, anchored);
    }

    private void PlanRealization(ListState state)
    {
        int count = state.Extents.Count;
        ScrollState scroll = _scroll.GetState(state.Host);
        int visibleStart;
        int visibleEnd;
        if (count == 0)
        {
            visibleStart = 0;
            visibleEnd = 0;
        }
        else
        {
            visibleStart = state.Extents.FindIndexAtOffset(scroll.Offset);
            float viewportEnd = scroll.Offset + Math.Max(scroll.Viewport, state.Policy.EstimatedItemExtent);
            visibleEnd = Math.Min(count, state.Extents.FindIndexAtOffset(viewportEnd) + 1);
        }
        int realizedStart = Math.Max(0, visibleStart - state.Policy.OverscanBefore);
        int realizedEnd = Math.Min(count, visibleEnd + state.Policy.OverscanAfter);

        for (int i = 0; i < state.Slots.Count; i++)
            state.Slots[i].UsedThisPlan = false;
        for (int index = realizedStart; index < realizedEnd; index++)
        {
            ItemSlot slot = FindSlot(state, index) ?? RentSlot(state, index);
            BindAndPosition(state, slot, index);
            slot.UsedThisPlan = true;
        }
        int realizedCount = 0;
        for (int i = 0; i < state.Slots.Count; i++)
        {
            ItemSlot slot = state.Slots[i];
            if (slot.UsedThisPlan)
            {
                realizedCount++;
                continue;
            }
            SetRealized(slot, false);
        }

        state.Snapshot = new UiVirtualizationSnapshot(
            count,
            visibleStart,
            visibleEnd,
            realizedStart,
            realizedEnd,
            realizedCount,
            state.Slots.Count - realizedCount,
            state.Extents.TotalExtent);
    }

    private ItemSlot? FindSlot(ListState state, int logicalIndex)
    {
        for (int i = 0; i < state.Slots.Count; i++)
        {
            ItemSlot slot = state.Slots[i];
            if (!slot.UsedThisPlan && slot.IsRealized && slot.LogicalIndex == logicalIndex)
                return slot;
        }
        return null;
    }

    private ItemSlot RentSlot(ListState state, int logicalIndex)
    {
        for (int i = 0; i < state.Slots.Count; i++)
        {
            if (!state.Slots[i].UsedThisPlan)
                return state.Slots[i];
        }

        PresentationStore presentation = new();
        BlueprintInstantiator instantiator = new(_world, presentation, registerPipelineSystem: false);
        state.Source.BindItem(logicalIndex, presentation);
        BlueprintInstance instance = instantiator.Instantiate(
            state.ItemTemplate,
            _world.Entities.GetScope(state.Host));
        _world.AttachChild(state.Content, instance.RootEntity);
        ItemSlot created = new(presentation, instantiator, instance);
        created.LogicalIndex = logicalIndex;
        created.Key = state.Keys[logicalIndex];
        state.Slots.Add(created);
        return created;
    }

    private void BindAndPosition(ListState state, ItemSlot slot, int logicalIndex)
    {
        long key = state.Keys[logicalIndex];
        bool rebound = false;
        if (!slot.IsRealized || slot.LogicalIndex != logicalIndex || slot.Key != key)
        {
            state.Source.BindItem(logicalIndex, slot.Presentation);
            slot.Instantiator.Update(slot.Instance);
            slot.LogicalIndex = logicalIndex;
            slot.Key = key;
            rebound = true;
        }

        UiEntity root = slot.Instance.RootEntity;
        float offset = state.Extents.GetOffset(logicalIndex);
        float extent = state.Extents.GetExtent(logicalIndex);
        AbsolutePlacement placement = state.Orientation == UiOrientation.Vertical
            ? new AbsolutePlacement { Top = offset }
            : new AbsolutePlacement { Left = offset };
        bool placementChanged = !_world.Components.TryGet(root, out AbsolutePlacement previousPlacement) ||
                                !previousPlacement.Equals(placement);
        if (placementChanged)
            _world.Set(root, placement);
        LayoutStyle layout = _world.Components.TryGet(root, out LayoutStyle current)
            ? current
            : LayoutStyle.Default;
        LayoutStyle previousLayout = layout;
        if (state.Orientation == UiOrientation.Vertical)
            layout.Width = UiLength.Percent(1f);
        else
            layout.Height = UiLength.Percent(1f);
        bool layoutChanged = !previousLayout.Equals(layout);
        if (layoutChanged)
            _world.Set(root, layout);
        VirtualItemSlot metadata = new()
        {
            LogicalIndex = logicalIndex,
            Key = key,
            Offset = offset,
            Extent = extent,
            IsRealized = true
        };
        bool metadataChanged = !_world.Components.TryGet(root, out VirtualItemSlot previousMetadata) ||
                               !previousMetadata.Equals(metadata);
        if (metadataChanged)
            _world.Set(root, metadata);
        slot.IsRealized = true;
        if (rebound || layoutChanged)
            LayoutInvalidation.MarkMeasure(_world, root, requestFrame: false);
        if (placementChanged)
            LayoutInvalidation.MarkArrange(_world, state.Content, requestFrame: false);
        if (metadataChanged)
            _world.Dirty.Mark(root, UiDirtyFlags.HitTest | UiDirtyFlags.Render);
        if (rebound || layoutChanged || placementChanged)
            _world.Scheduler.RequestReactiveFrame();
    }

    private void SetRealized(ItemSlot slot, bool realized)
    {
        if (slot.IsRealized == realized || !slot.Instance.IsAlive)
            return;
        slot.IsRealized = realized;
        UiEntity root = slot.Instance.RootEntity;
        if (!_world.Entities.IsAlive(root))
            return;
        VirtualItemSlot metadata = _world.Components.TryGet(root, out VirtualItemSlot current)
            ? current
            : default;
        metadata.IsRealized = realized;
        _world.Set(root, metadata);
        _world.Dirty.Mark(root, UiDirtyFlags.HitTest | UiDirtyFlags.Render);
    }

    private UiEntity CreateContent(UiEntity host, UiOrientation orientation, float extent)
    {
        UiEntity content = _world.CreateEntity(_world.Entities.GetScope(host), asHierarchyRoot: false);
        _world.AttachChild(host, content);
        _world.Set(content, new NodeKindComponent { Kind = UiNodeKind.Container });
        LayoutStyle layout = LayoutStyle.Default;
        if (orientation == UiOrientation.Vertical)
        {
            layout.Width = UiLength.Percent(1f);
            layout.Height = UiLength.Pixels(Math.Max(0f, extent));
        }
        else
        {
            layout.Width = UiLength.Pixels(Math.Max(0f, extent));
            layout.Height = UiLength.Percent(1f);
        }
        _world.Set(content, layout);
        _world.Set(content, new VirtualizingLayout { Orientation = orientation });
        _world.Dirty.Mark(content, UiDirtyFlags.Style | UiDirtyFlags.Render);
        return content;
    }

    private void UpdateContentExtent(ListState state, float extent, float? anchoredOffset)
    {
        if (!_world.Entities.IsAlive(state.Content))
            return;
        LayoutStyle layout = _world.Components.Get<LayoutStyle>(state.Content);
        if (state.Orientation == UiOrientation.Vertical)
            layout.Height = UiLength.Pixels(extent);
        else
            layout.Width = UiLength.Pixels(extent);
        _world.Set(state.Content, layout);
        _scroll.UpdateVirtualExtent(state.Host, extent, anchoredOffset);
        LayoutInvalidation.MarkMeasure(_world, state.Content);
    }

    private void Unregister(UiEntity host, ListState expected)
    {
        if (!_states.TryGetValue(host, out ListState? current) || !ReferenceEquals(current, expected))
            return;
        _states.Remove(host);
        DestroyState(current, destroyContent: _world.Entities.IsAlive(current.Content));
    }

    private void DestroyState(ListState state, bool destroyContent)
    {
        for (int i = 0; i < state.Slots.Count; i++)
            state.Slots[i].Instantiator.Destroy(state.Slots[i].Instance);
        state.Slots.Clear();
        if (destroyContent && _world.Entities.IsAlive(state.Content))
            _world.DestroyEntity(state.Content);
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        if (!_states.Remove(entity, out ListState? state))
            return;
        DestroyState(state, destroyContent: false);
    }

    private void EnsureVirtualList(UiEntity host)
    {
        _world.Entities.EnsureAlive(host);
        if (!_world.Components.Has<Virtualization>(host) ||
            !_world.Components.Has<ScrollViewport>(host) ||
            !_world.Components.Has<ScrollState>(host))
        {
            throw new InvalidOperationException("Entity is not a virtual list host: " + host);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static long[] BuildKeys(IUiVirtualItemSource source, int count)
    {
        long[] keys = new long[count];
        HashSet<long> unique = new(count);
        for (int i = 0; i < count; i++)
        {
            long key = source.GetKey(i);
            if (!unique.Add(key))
                throw new InvalidOperationException("Virtual item keys must be unique within a source version: " + key);
            keys[i] = key;
        }
        return keys;
    }

    private sealed class ListState(
        UiEntity host,
        UiEntity content,
        IUiVirtualItemSource source,
        UiBlueprint itemTemplate,
        UiOrientation orientation,
        Virtualization policy,
        VariableExtentIndex extents,
        long[] keys)
    {
        public UiEntity Host { get; } = host;
        public UiEntity Content { get; } = content;
        public IUiVirtualItemSource Source { get; } = source;
        public UiBlueprint ItemTemplate { get; } = itemTemplate;
        public UiOrientation Orientation { get; } = orientation;
        public Virtualization Policy { get; } = policy;
        public VariableExtentIndex Extents { get; } = extents;
        public long[] Keys { get; set; } = keys;
        public ulong SourceVersion { get; set; } = source.Version;
        public Dictionary<long, float> MeasuredByKey { get; } = [];
        public List<ItemSlot> Slots { get; } = [];
        public UiVirtualizationSnapshot Snapshot { get; set; }
    }

    private sealed class ItemSlot(
        PresentationStore presentation,
        BlueprintInstantiator instantiator,
        BlueprintInstance instance)
    {
        public PresentationStore Presentation { get; } = presentation;
        public BlueprintInstantiator Instantiator { get; } = instantiator;
        public BlueprintInstance Instance { get; } = instance;
        public int LogicalIndex { get; set; } = -1;
        public long Key { get; set; }
        public bool IsRealized { get; set; }
        public bool UsedThisPlan { get; set; }
    }
}

internal sealed class VirtualizationUpdateSystem(UiVirtualizationRuntime virtualization) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.VirtualizationPlan;
    public string Name => "virtualization.update";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = world;
        _ = frame;
        virtualization.Update();
    }
}
