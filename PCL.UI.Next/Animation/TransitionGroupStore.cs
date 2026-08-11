// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

internal sealed class TransitionGroupStore
{
    private readonly UiWorld _world;
    private readonly FloatAnimationStore _animations;
    private readonly List<GroupState?> _groups = [null];
    private readonly Stack<int> _free = [];
    private readonly List<int> _active = [];
    private readonly List<UiTransitionGroupCompleted> _completed = [];
    private uint[] _generations = new uint[8];

    public TransitionGroupStore(UiWorld world, FloatAnimationStore animations)
    {
        _world = world;
        _animations = animations;
    }

    public int ActiveCount => _active.Count;

    public IReadOnlyList<UiTransitionGroupCompleted> FrameCompletions => _completed;

    public void BeginFrame() => _completed.Clear();

    public UiTransitionGroupId Create(UiScopeId scope, ReadOnlySpan<UiAnimationHandle> channels)
    {
        if (!_world.Scopes.IsAlive(scope))
            throw new InvalidOperationException("Scope is not alive: " + scope);
        if (channels.IsEmpty)
            throw new ArgumentException("A transition group requires at least one channel.", nameof(channels));

        GroupRequirement[] requirements = new GroupRequirement[channels.Length];
        int unsettled = 0;
        for (int i = 0; i < channels.Length; i++)
        {
            UiAnimationHandle channel = channels[i];
            for (int previous = 0; previous < i; previous++)
            {
                if (channels[previous] == channel)
                {
                    throw new ArgumentException(
                        "A transition group cannot contain the same channel more than once.",
                        nameof(channels));
                }
            }
            if (!_animations.TryGetSnapshot(channel, out UiAnimationSnapshot snapshot))
                throw new InvalidOperationException("Animation channel is stale: " + channel);
            if (!IsScopeOrDescendant(scope, snapshot.Scope))
                throw new InvalidOperationException("Animation channel does not belong to the transition group scope.");
            bool settled = !snapshot.IsActive &&
                           MathF.Abs(snapshot.Current - snapshot.Target) <= 0.00001f &&
                           MathF.Abs(snapshot.Velocity) <= 0.00001f;
            requirements[i] = new GroupRequirement(
                channel,
                snapshot.Entity,
                snapshot.TargetGeneration,
                settled);
            if (!settled)
                unsettled++;
        }

        int index = _free.TryPop(out int free) ? free : _groups.Count;
        EnsureGenerationCapacity(index + 1);
        if (_generations[index] == 0)
            _generations[index] = 1;
        UiTransitionGroupId id = new(index, _generations[index]);
        GroupState state = new(id, scope, requirements, unsettled);
        if (index == _groups.Count)
            _groups.Add(state);
        else
            _groups[index] = state;
        if (unsettled == 0)
        {
            _completed.Add(new UiTransitionGroupCompleted(id, scope));
            RemoveSlot(index);
        }
        else
            _active.Add(index);
        return id;
    }

    public bool IsAlive(UiTransitionGroupId group) =>
        !group.IsNone &&
        group.Index > 0 &&
        group.Index < _groups.Count &&
        _generations[group.Index] == group.Generation &&
        _groups[group.Index] is not null;

    public void ProcessSettlements()
    {
        IReadOnlyList<UiAnimationSettled> settlements = _animations.FrameSettlements;
        int activeIndex = 0;
        while (activeIndex < _active.Count)
        {
            int groupIndex = _active[activeIndex];
            GroupState? state = _groups[groupIndex];
            if (state is null || !_world.Scopes.IsAlive(state.Scope) || !RequirementsAreCurrent(state))
            {
                RemoveAtActivePosition(activeIndex);
                RemoveSlot(groupIndex);
                continue;
            }

            for (int settlementIndex = 0; settlementIndex < settlements.Count; settlementIndex++)
            {
                UiAnimationSettled settled = settlements[settlementIndex];
                for (int requirementIndex = 0; requirementIndex < state.Requirements.Length; requirementIndex++)
                {
                    ref GroupRequirement requirement = ref state.Requirements[requirementIndex];
                    if (requirement.Settled ||
                        requirement.Channel != settled.Channel ||
                        requirement.TargetGeneration != settled.TargetGeneration)
                    {
                        continue;
                    }

                    requirement.Settled = true;
                    state.Unsettled--;
                    break;
                }
            }

            if (state.Unsettled > 0)
            {
                activeIndex++;
                continue;
            }

            _completed.Add(new UiTransitionGroupCompleted(state.Id, state.Scope));
            RemoveAtActivePosition(activeIndex);
            RemoveSlot(groupIndex);
        }
    }

    public void InvalidateRetarget(UiAnimationHandle channel, uint targetGeneration)
    {
        for (int activeIndex = _active.Count - 1; activeIndex >= 0; activeIndex--)
        {
            int groupIndex = _active[activeIndex];
            GroupState? state = _groups[groupIndex];
            if (state is null)
                continue;
            for (int i = 0; i < state.Requirements.Length; i++)
            {
                GroupRequirement requirement = state.Requirements[i];
                if (requirement.Channel != channel || requirement.TargetGeneration == targetGeneration)
                    continue;
                RemoveAtActivePosition(activeIndex);
                RemoveSlot(groupIndex);
                break;
            }
        }
    }

    public void InvalidateChannel(UiAnimationHandle channel)
    {
        for (int activeIndex = _active.Count - 1; activeIndex >= 0; activeIndex--)
        {
            int groupIndex = _active[activeIndex];
            GroupState? state = _groups[groupIndex];
            if (state is null || !ContainsChannel(state, channel))
                continue;
            RemoveAtActivePosition(activeIndex);
            RemoveSlot(groupIndex);
        }
    }

    public void RemoveEntity(UiEntity entity)
    {
        for (int activeIndex = _active.Count - 1; activeIndex >= 0; activeIndex--)
        {
            int groupIndex = _active[activeIndex];
            GroupState? state = _groups[groupIndex];
            if (state is null || !ContainsEntity(state, entity))
                continue;
            RemoveAtActivePosition(activeIndex);
            RemoveSlot(groupIndex);
        }
    }

    public void Clear()
    {
        _groups.Clear();
        _groups.Add(null);
        _free.Clear();
        _active.Clear();
        _completed.Clear();
    }

    private bool RequirementsAreCurrent(GroupState state)
    {
        for (int i = 0; i < state.Requirements.Length; i++)
        {
            GroupRequirement requirement = state.Requirements[i];
            if (!_animations.TryGetSnapshot(requirement.Channel, out UiAnimationSnapshot snapshot) ||
                snapshot.TargetGeneration != requirement.TargetGeneration)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ContainsEntity(GroupState state, UiEntity entity)
    {
        for (int i = 0; i < state.Requirements.Length; i++)
        {
            if (state.Requirements[i].Entity == entity)
                return true;
        }
        return false;
    }

    private static bool ContainsChannel(GroupState state, UiAnimationHandle channel)
    {
        for (int i = 0; i < state.Requirements.Length; i++)
        {
            if (state.Requirements[i].Channel == channel)
                return true;
        }
        return false;
    }

    private bool IsScopeOrDescendant(UiScopeId ancestor, UiScopeId scope)
    {
        UiScopeId current = scope;
        int guard = 0;
        while (_world.Scopes.IsAlive(current) && guard++ < 1_000_000)
        {
            if (current == ancestor)
                return true;
            if (!_world.Scopes.TryGetParent(current, out current) || current.IsNone)
                break;
        }
        return false;
    }

    private void RemoveAtActivePosition(int activeIndex)
    {
        int last = _active.Count - 1;
        _active[activeIndex] = _active[last];
        _active.RemoveAt(last);
    }

    private void RemoveSlot(int groupIndex)
    {
        if (groupIndex <= 0 || groupIndex >= _groups.Count || _groups[groupIndex] is null)
            return;
        _groups[groupIndex] = null;
        _generations[groupIndex] = NextGeneration(_generations[groupIndex]);
        _free.Push(groupIndex);
    }

    private void EnsureGenerationCapacity(int required)
    {
        if (required <= _generations.Length)
            return;
        int capacity = _generations.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref _generations, capacity);
    }

    private static uint NextGeneration(uint generation)
    {
        uint next = unchecked(generation + 1);
        return next == 0 ? 1 : next;
    }

    private sealed class GroupState(
        UiTransitionGroupId id,
        UiScopeId scope,
        GroupRequirement[] requirements,
        int unsettled)
    {
        public UiTransitionGroupId Id { get; } = id;
        public UiScopeId Scope { get; } = scope;
        public GroupRequirement[] Requirements { get; } = requirements;
        public int Unsettled { get; set; } = unsettled;
    }

    private struct GroupRequirement(
        UiAnimationHandle channel,
        UiEntity entity,
        uint targetGeneration,
        bool settled)
    {
        public UiAnimationHandle Channel { get; } = channel;
        public UiEntity Entity { get; } = entity;
        public uint TargetGeneration { get; } = targetGeneration;
        public bool Settled { get; set; } = settled;
    }
}
