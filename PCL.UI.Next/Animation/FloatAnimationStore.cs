// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

internal readonly record struct AnimationChannelKey(UiEntity Entity, UiAnimationProperty Property);

/// <summary>SoA float-channel store. Only active indices are visited during Tick.</summary>
internal sealed class FloatAnimationStore
{
    private const int InitialCapacity = 64;
    private const float SameTargetTolerance = 0.00001f;

    private readonly UiWorld _world;
    private readonly UiMotionRegistry _motions;
    private readonly Dictionary<AnimationChannelKey, int> _byKey = [];
    private readonly Stack<int> _free = [];

    private UiEntity[] _entities = new UiEntity[InitialCapacity];
    private UiAnimationProperty[] _properties = new UiAnimationProperty[InitialCapacity];
    private float[] _current = new float[InitialCapacity];
    private float[] _target = new float[InitialCapacity];
    private float[] _velocity = new float[InitialCapacity];
    private float[] _start = new float[InitialCapacity];
    private float[] _elapsed = new float[InitialCapacity];
    private float[] _duration = new float[InitialCapacity];
    private UiEasing[] _easing = new UiEasing[InitialCapacity];
    private float[] _springResponse = new float[InitialCapacity];
    private float[] _springDamping = new float[InitialCapacity];
    private float[] _decayFriction = new float[InitialCapacity];
    private float[] _positionTolerance = new float[InitialCapacity];
    private float[] _velocityTolerance = new float[InitialCapacity];
    private UiAnimationSolverKind[] _solver = new UiAnimationSolverKind[InitialCapacity];
    private UiAnimationContinuity[] _continuity = new UiAnimationContinuity[InitialCapacity];
    private UiMotionToken[] _motion = new UiMotionToken[InitialCapacity];
    private UiAnimationFlags[] _flags = new UiAnimationFlags[InitialCapacity];
    private UiAnimationOwnerReason[] _owner = new UiAnimationOwnerReason[InitialCapacity];
    private UiScopeId[] _scope = new UiScopeId[InitialCapacity];
    private uint[] _handleGeneration = new uint[InitialCapacity];
    private uint[] _targetGeneration = new uint[InitialCapacity];
    private bool[] _alive = new bool[InitialCapacity];
    private double[] _lastSampleTimestamp = new double[InitialCapacity];
    private int[] _active = new int[InitialCapacity];
    private int[] _activePosition = new int[InitialCapacity];
    private int _activeCount;
    private int _highWater = 1;
    private int _channelCount;

    public FloatAnimationStore(UiWorld world, UiMotionRegistry motions)
    {
        _world = world;
        _motions = motions;
    }

    public int ChannelCount => _channelCount;

    public int ActiveCount => _activeCount;

    public event Action<UiAnimationSettled>? Settled;

    public UiAnimationHandle Retarget(
        UiEntity entity,
        UiAnimationProperty property,
        float target,
        in UiAnimationSpec spec,
        bool animationsEnabled,
        bool reducedMotion)
    {
        _world.Entities.EnsureAlive(entity);
        if (property == UiAnimationProperty.None)
            throw new ArgumentOutOfRangeException(nameof(property));
        target = AnimationPropertyRegistry.Constrain(property, target);
        int index = GetOrCreate(entity, property);
        if (ApproximatelyEqual(_target[index], target) &&
            (_activePosition[index] != 0 || ApproximatelyEqual(_current[index], target)))
        {
            return Handle(index);
        }

        UiMotionDefinition definition = _motions.Resolve(
            spec.Motion,
            spec.Flags,
            animationsEnabled,
            reducedMotion);
        UiAnimationContinuity continuity = spec.HasContinuityOverride
            ? spec.Continuity
            : definition.Continuity;
        float previousVelocity = _velocity[index];
        float distance = MathF.Abs(target - _current[index]);

        _target[index] = target;
        _motion[index] = spec.Motion;
        _flags[index] = spec.Flags;
        _owner[index] = spec.Owner;
        _continuity[index] = continuity;
        _solver[index] = definition.Solver;
        _easing[index] = definition.Easing;
        _springResponse[index] = definition.SpringResponse;
        _springDamping[index] = definition.SpringDampingRatio;
        _decayFriction[index] = definition.DecayFriction;
        _positionTolerance[index] = definition.PositionTolerance;
        _velocityTolerance[index] = definition.VelocityTolerance;
        _targetGeneration[index] = NextGeneration(_targetGeneration[index]);

        if (definition.Solver == UiAnimationSolverKind.Immediate)
        {
            _current[index] = target;
            _velocity[index] = 0f;
            _start[index] = target;
            _elapsed[index] = 0f;
            Deactivate(index);
            AnimationPropertyRegistry.WriteCurrent(_world, entity, property, target);
            PublishSettled(index);
            return Handle(index);
        }

        _start[index] = _current[index];
        _elapsed[index] = 0f;
        _duration[index] = definition.DurationSeconds;
        if (definition.Solver == UiAnimationSolverKind.Tween &&
            continuity == UiAnimationContinuity.PreserveSpeed &&
            MathF.Abs(previousVelocity) > definition.VelocityTolerance)
        {
            _duration[index] = Math.Clamp(distance / MathF.Abs(previousVelocity), 0.04f, 1f);
        }

        if (continuity is not (UiAnimationContinuity.PreserveVelocity or UiAnimationContinuity.MergeVelocity) ||
            definition.Solver == UiAnimationSolverKind.Tween)
        {
            _velocity[index] = 0f;
        }

        _lastSampleTimestamp[index] = _world.Clock.Now.Seconds;
        Activate(index);
        return Handle(index);
    }

    public UiAnimationHandle SetDirect(
        UiEntity entity,
        UiAnimationProperty property,
        float current,
        float velocity,
        UiAnimationOwnerReason owner)
    {
        _world.Entities.EnsureAlive(entity);
        if (property == UiAnimationProperty.None)
            throw new ArgumentOutOfRangeException(nameof(property));
        int index = GetOrCreate(entity, property);
        current = AnimationPropertyRegistry.Constrain(property, current);
        _current[index] = current;
        _target[index] = current;
        _velocity[index] = float.IsFinite(velocity) ? velocity : 0f;
        _start[index] = current;
        _elapsed[index] = 0f;
        _solver[index] = UiAnimationSolverKind.Direct;
        _continuity[index] = UiAnimationContinuity.PreserveVelocity;
        _motion[index] = UiMotion.Instant;
        _owner[index] = owner;
        _targetGeneration[index] = NextGeneration(_targetGeneration[index]);
        _lastSampleTimestamp[index] = _world.Clock.Now.Seconds;
        Deactivate(index);
        AnimationPropertyRegistry.WriteCurrent(_world, entity, property, current);
        return Handle(index);
    }

    public UiAnimationHandle StartDecay(
        UiEntity entity,
        UiAnimationProperty property,
        float velocity,
        in UiAnimationSpec spec,
        bool animationsEnabled,
        bool reducedMotion)
    {
        _world.Entities.EnsureAlive(entity);
        int index = GetOrCreate(entity, property);
        UiMotionDefinition definition = _motions.Resolve(
            spec.Motion,
            spec.Flags,
            animationsEnabled,
            reducedMotion);
        if (definition.Solver == UiAnimationSolverKind.Immediate)
        {
            _target[index] = _current[index];
            _velocity[index] = 0f;
            _solver[index] = UiAnimationSolverKind.Immediate;
            _continuity[index] = UiAnimationContinuity.ContinueFromCurrent;
            _motion[index] = spec.Motion;
            _flags[index] = spec.Flags;
            _owner[index] = spec.Owner;
            _targetGeneration[index] = NextGeneration(_targetGeneration[index]);
            _lastSampleTimestamp[index] = _world.Clock.Now.Seconds;
            Deactivate(index);
            PublishSettled(index);
            return Handle(index);
        }

        float nextVelocity = float.IsFinite(velocity) ? velocity : 0f;
        UiAnimationContinuity continuity = spec.HasContinuityOverride
            ? spec.Continuity
            : definition.Continuity;
        if (continuity == UiAnimationContinuity.MergeVelocity)
            nextVelocity += _velocity[index];
        _velocity[index] = nextVelocity;
        _target[index] = _current[index];
        _solver[index] = UiAnimationSolverKind.Decay;
        _continuity[index] = continuity;
        _motion[index] = spec.Motion;
        _flags[index] = spec.Flags;
        _owner[index] = spec.Owner;
        _decayFriction[index] = Math.Max(0.0001f, definition.DecayFriction);
        _positionTolerance[index] = definition.PositionTolerance;
        _velocityTolerance[index] = definition.VelocityTolerance;
        _targetGeneration[index] = NextGeneration(_targetGeneration[index]);
        _lastSampleTimestamp[index] = _world.Clock.Now.Seconds;
        if (MathF.Abs(nextVelocity) > _velocityTolerance[index])
            Activate(index);
        return Handle(index);
    }

    public bool Cancel(UiEntity entity, UiAnimationProperty property, UiAnimationCancelMode mode)
    {
        if (!_byKey.TryGetValue(new AnimationChannelKey(entity, property), out int index))
            return false;
        if (mode == UiAnimationCancelMode.Discard)
        {
            RemoveChannel(index);
            return true;
        }

        if (mode == UiAnimationCancelMode.SnapToTarget)
            _current[index] = _target[index];
        _velocity[index] = 0f;
        _lastSampleTimestamp[index] = _world.Clock.Now.Seconds;
        Deactivate(index);
        AnimationPropertyRegistry.WriteCurrent(_world, entity, property, _current[index]);
        return true;
    }

    public void Tick(UiTimestamp now)
    {
        int activePosition = 0;
        while (activePosition < _activeCount)
        {
            int index = _active[activePosition];
            if (!_alive[index] ||
                !_world.Entities.IsAlive(_entities[index]) ||
                !_world.Scopes.IsAlive(_scope[index]))
            {
                RemoveChannel(index);
                continue;
            }

            double elapsed = now.Seconds - _lastSampleTimestamp[index];
            float channelDelta = (float)Math.Max(0d, elapsed);
            if (elapsed > 0d)
                _lastSampleTimestamp[index] = now.Seconds;
            float simulationDelta = Math.Min(channelDelta, 1f);

            bool settled = _solver[index] switch
            {
                UiAnimationSolverKind.Tween => TickTween(index, channelDelta),
                UiAnimationSolverKind.Spring => TickSpring(index, simulationDelta),
                UiAnimationSolverKind.Decay => TickDecay(index, simulationDelta),
                _ => true
            };
            AnimationPropertyRegistry.WriteCurrent(
                _world,
                _entities[index],
                _properties[index],
                _current[index]);
            if (!settled)
            {
                activePosition++;
                continue;
            }

            if (_solver[index] == UiAnimationSolverKind.Decay)
                _target[index] = _current[index];
            else
                _current[index] = _target[index];
            _velocity[index] = 0f;
            AnimationPropertyRegistry.WriteCurrent(
                _world,
                _entities[index],
                _properties[index],
                _current[index]);
            Deactivate(index);
            PublishSettled(index);
        }
    }

    public bool TryGetSnapshot(
        UiEntity entity,
        UiAnimationProperty property,
        out UiAnimationSnapshot snapshot)
    {
        if (!_byKey.TryGetValue(new AnimationChannelKey(entity, property), out int index) || !_alive[index])
        {
            snapshot = default;
            return false;
        }

        snapshot = new UiAnimationSnapshot(
            Handle(index),
            entity,
            property,
            _current[index],
            _target[index],
            _velocity[index],
            _solver[index],
            _continuity[index],
            _motion[index],
            _targetGeneration[index],
            _scope[index],
            _owner[index],
            _activePosition[index] != 0);
        return true;
    }

    public bool TryGetSnapshot(UiAnimationHandle handle, out UiAnimationSnapshot snapshot)
    {
        if (!IsAlive(handle))
        {
            snapshot = default;
            return false;
        }
        return TryGetSnapshot(_entities[handle.Index], _properties[handle.Index], out snapshot);
    }

    public bool IsCurrent(in UiAnimationSettled settled) =>
        IsAlive(settled.Channel) &&
        _targetGeneration[settled.Channel.Index] == settled.TargetGeneration;

    public void RemoveEntity(UiEntity entity)
    {
        for (int index = 1; index < _highWater; index++)
        {
            if (_alive[index] && _entities[index] == entity)
                RemoveChannel(index);
        }
    }

    public void ApplyMotionPolicy(bool animationsEnabled, bool reducedMotion)
    {
        for (int index = 1; index < _highWater; index++)
        {
            if (!_alive[index] || _activePosition[index] == 0)
                continue;
            UiMotionDefinition definition = _motions.Resolve(
                _motion[index],
                _flags[index],
                animationsEnabled,
                reducedMotion);
            if (definition.Solver == UiAnimationSolverKind.Immediate)
            {
                _current[index] = _target[index];
                _velocity[index] = 0f;
                AnimationPropertyRegistry.WriteCurrent(
                    _world,
                    _entities[index],
                    _properties[index],
                    _current[index]);
                Deactivate(index);
                PublishSettled(index);
                continue;
            }

            _duration[index] = definition.DurationSeconds;
            _springResponse[index] = definition.SpringResponse;
            _springDamping[index] = definition.SpringDampingRatio;
            _decayFriction[index] = definition.DecayFriction;
        }
    }

    public void Clear()
    {
        _byKey.Clear();
        _free.Clear();
        Array.Clear(_alive);
        Array.Clear(_activePosition);
        Array.Clear(_active);
        _activeCount = 0;
        _channelCount = 0;
        _highWater = 1;
    }

    private int GetOrCreate(UiEntity entity, UiAnimationProperty property)
    {
        AnimationChannelKey key = new(entity, property);
        if (_byKey.TryGetValue(key, out int existing) && _alive[existing])
            return existing;

        int index = _free.TryPop(out int free) ? free : _highWater++;
        EnsureCapacity(index + 1);
        uint generation = _handleGeneration[index];
        if (generation == 0)
            generation = 1;
        _handleGeneration[index] = generation;
        _alive[index] = true;
        _entities[index] = entity;
        _properties[index] = property;
        _scope[index] = _world.Entities.GetScope(entity);
        float current = AnimationPropertyRegistry.ReadCurrent(_world, entity, property);
        AnimationPropertyRegistry.EnsureVisual(_world, entity);
        _current[index] = current;
        _target[index] = current;
        _start[index] = current;
        _velocity[index] = 0f;
        _targetGeneration[index] = 0;
        _lastSampleTimestamp[index] = _world.Clock.Now.Seconds;
        _positionTolerance[index] = 0.001f;
        _velocityTolerance[index] = 0.001f;
        _byKey[key] = index;
        _channelCount++;
        return index;
    }

    private bool TickTween(int index, float deltaSeconds)
    {
        float duration = _duration[index];
        if (duration <= 0f)
            return true;
        _elapsed[index] = Math.Min(duration, _elapsed[index] + deltaSeconds);
        float progress = _elapsed[index] / duration;
        float eased = _easing[index].Evaluate(progress);
        float distance = _target[index] - _start[index];
        _current[index] = AnimationPropertyRegistry.Constrain(
            _properties[index],
            _start[index] + (distance * eased));
        _velocity[index] = progress >= 1f
            ? 0f
            : distance * _easing[index].Derivative(progress) / duration;
        return progress >= 1f;
    }

    private bool TickSpring(int index, float deltaSeconds)
    {
        float response = Math.Max(0.001f, _springResponse[index]);
        float damping = Math.Max(0f, _springDamping[index]);
        SolveSpring(
            _current[index],
            _velocity[index],
            _target[index],
            response,
            damping,
            deltaSeconds,
            out float position,
            out float velocity);
        _current[index] = AnimationPropertyRegistry.Constrain(_properties[index], position);
        _velocity[index] = velocity;
        return MathF.Abs(_target[index] - _current[index]) <= _positionTolerance[index] &&
               MathF.Abs(_velocity[index]) <= _velocityTolerance[index];
    }

    private bool TickDecay(int index, float deltaSeconds)
    {
        float friction = Math.Max(0.0001f, _decayFriction[index]);
        float initialVelocity = _velocity[index];
        float decay = MathF.Exp(-friction * deltaSeconds);
        float nextVelocity = initialVelocity * decay;
        _current[index] = AnimationPropertyRegistry.Constrain(
            _properties[index],
            _current[index] + ((initialVelocity - nextVelocity) / friction));
        _velocity[index] = nextVelocity;
        return MathF.Abs(nextVelocity) <= _velocityTolerance[index];
    }

    private static void SolveSpring(
        float position,
        float velocity,
        float target,
        float response,
        float dampingRatio,
        float deltaSeconds,
        out float nextPosition,
        out float nextVelocity)
    {
        if (deltaSeconds <= 0f)
        {
            nextPosition = position;
            nextVelocity = velocity;
            return;
        }

        float omega = 2f * MathF.PI / response;
        float displacement = position - target;
        if (dampingRatio < 0.999f)
        {
            float dampedOmega = omega * MathF.Sqrt(1f - (dampingRatio * dampingRatio));
            float envelope = MathF.Exp(-dampingRatio * omega * deltaSeconds);
            float coefficient = (velocity + (dampingRatio * omega * displacement)) / dampedOmega;
            float cosine = MathF.Cos(dampedOmega * deltaSeconds);
            float sine = MathF.Sin(dampedOmega * deltaSeconds);
            float nextDisplacement = envelope * ((displacement * cosine) + (coefficient * sine));
            nextVelocity = envelope *
                ((-dampingRatio * omega * ((displacement * cosine) + (coefficient * sine))) +
                 (-displacement * dampedOmega * sine) +
                 (coefficient * dampedOmega * cosine));
            nextPosition = target + nextDisplacement;
            return;
        }

        if (dampingRatio <= 1.001f)
        {
            float coefficient = velocity + (omega * displacement);
            float envelope = MathF.Exp(-omega * deltaSeconds);
            float nextDisplacement = (displacement + (coefficient * deltaSeconds)) * envelope;
            nextVelocity = (velocity - (omega * coefficient * deltaSeconds)) * envelope;
            nextPosition = target + nextDisplacement;
            return;
        }

        float root = MathF.Sqrt((dampingRatio * dampingRatio) - 1f);
        float r1 = -omega * (dampingRatio - root);
        float r2 = -omega * (dampingRatio + root);
        float coefficient1 = (velocity - (r2 * displacement)) / (r1 - r2);
        float coefficient2 = displacement - coefficient1;
        float term1 = coefficient1 * MathF.Exp(r1 * deltaSeconds);
        float term2 = coefficient2 * MathF.Exp(r2 * deltaSeconds);
        nextPosition = target + term1 + term2;
        nextVelocity = (r1 * term1) + (r2 * term2);
    }

    private void Activate(int index)
    {
        if (_activePosition[index] != 0)
            return;
        EnsureActiveCapacity(_activeCount + 1);
        _active[_activeCount] = index;
        _activePosition[index] = _activeCount + 1;
        _activeCount++;
    }

    private void Deactivate(int index)
    {
        int packed = _activePosition[index];
        if (packed == 0)
            return;
        int position = packed - 1;
        int lastPosition = _activeCount - 1;
        int moved = _active[lastPosition];
        _active[position] = moved;
        _activePosition[moved] = position + 1;
        _active[lastPosition] = 0;
        _activePosition[index] = 0;
        _activeCount = lastPosition;
    }

    private void RemoveChannel(int index)
    {
        if (!_alive[index])
            return;
        Deactivate(index);
        _byKey.Remove(new AnimationChannelKey(_entities[index], _properties[index]));
        _alive[index] = false;
        _entities[index] = UiEntity.None;
        _properties[index] = UiAnimationProperty.None;
        _scope[index] = UiScopeId.None;
        _lastSampleTimestamp[index] = 0d;
        _handleGeneration[index] = NextGeneration(_handleGeneration[index]);
        _free.Push(index);
        _channelCount--;
    }

    private void PublishSettled(int index)
    {
        Settled?.Invoke(new UiAnimationSettled(
            Handle(index),
            _entities[index],
            _properties[index],
            _targetGeneration[index],
            _target[index],
            _scope[index]));
    }

    private UiAnimationHandle Handle(int index) => new(index, _handleGeneration[index]);

    private bool IsAlive(UiAnimationHandle handle) =>
        !handle.IsNone &&
        handle.Index > 0 &&
        handle.Index < _highWater &&
        _alive[handle.Index] &&
        _handleGeneration[handle.Index] == handle.Generation;

    private void EnsureCapacity(int required)
    {
        if (required <= _entities.Length)
            return;
        int capacity = _entities.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref _entities, capacity);
        Array.Resize(ref _properties, capacity);
        Array.Resize(ref _current, capacity);
        Array.Resize(ref _target, capacity);
        Array.Resize(ref _velocity, capacity);
        Array.Resize(ref _start, capacity);
        Array.Resize(ref _elapsed, capacity);
        Array.Resize(ref _duration, capacity);
        Array.Resize(ref _easing, capacity);
        Array.Resize(ref _springResponse, capacity);
        Array.Resize(ref _springDamping, capacity);
        Array.Resize(ref _decayFriction, capacity);
        Array.Resize(ref _positionTolerance, capacity);
        Array.Resize(ref _velocityTolerance, capacity);
        Array.Resize(ref _solver, capacity);
        Array.Resize(ref _continuity, capacity);
        Array.Resize(ref _motion, capacity);
        Array.Resize(ref _flags, capacity);
        Array.Resize(ref _owner, capacity);
        Array.Resize(ref _scope, capacity);
        Array.Resize(ref _handleGeneration, capacity);
        Array.Resize(ref _targetGeneration, capacity);
        Array.Resize(ref _alive, capacity);
        Array.Resize(ref _lastSampleTimestamp, capacity);
        Array.Resize(ref _activePosition, capacity);
    }

    private void EnsureActiveCapacity(int required)
    {
        if (required <= _active.Length)
            return;
        int capacity = _active.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref _active, capacity);
    }

    private static bool ApproximatelyEqual(float left, float right) =>
        MathF.Abs(left - right) <= SameTargetTolerance;

    private static uint NextGeneration(uint generation)
    {
        uint next = unchecked(generation + 1);
        return next == 0 ? 1 : next;
    }
}
