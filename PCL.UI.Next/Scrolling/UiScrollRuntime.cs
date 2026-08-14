// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Default scrolling behavior shared by wheel, pan, inertial and programmatic input.
/// Offset changes update only the scroll-content transform and never invalidate layout.
/// </summary>
public sealed class UiScrollRuntime : IDisposable
{
    private const float MinimumInertiaVelocity = 12f;
    private const float SettledVelocity = 1f;
    private const float SettledDistance = 0.1f;
    private readonly UiWorld _world;
    private readonly UiInputRuntime _input;
    private readonly ScrollUpdateSystem _system;
    private readonly List<UiEntity> _scrollEntities = [];
    private readonly Dictionary<UiPointerKey, PanSession> _panSessions = [];
    private bool _disposed;

    public UiScrollRuntime(UiWorld world, UiInputRuntime input)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _system = new ScrollUpdateSystem(this);
        _world.Systems.Register(_system);
    }

    public ScrollState GetState(UiEntity viewport)
    {
        EnsureViewport(viewport);
        return _world.Components.Get<ScrollState>(viewport);
    }

    public void SetOffset(UiEntity viewport, float offset)
    {
        EnsureViewport(viewport);
        if (!float.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        ScrollState state = _world.Components.Get<ScrollState>(viewport);
        state.Offset = Math.Clamp(offset, 0f, state.MaximumOffset);
        state.Target = state.Offset;
        state.Velocity = 0f;
        state.Motion = UiScrollMotionKind.Idle;
        state.LastSampleTimestamp = _world.Clock.Now;
        Store(viewport, in state);
        _world.Scheduler.RequestReactiveFrame();
    }

    public void ScrollTo(UiEntity viewport, float offset, bool animated = true)
    {
        EnsureViewport(viewport);
        if (!float.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (!animated)
        {
            SetOffset(viewport, offset);
            return;
        }

        ScrollState state = _world.Components.Get<ScrollState>(viewport);
        state.Target = Math.Clamp(offset, 0f, state.MaximumOffset);
        state.Motion = UiScrollMotionKind.Spring;
        state.LastSampleTimestamp = _world.Clock.Now;
        Store(viewport, in state);
        _world.Scheduler.RequestContinuousFrame(UiContinuousReason.ScrollInertia);
    }

    public void ScrollBy(UiEntity viewport, float delta, bool animated = false) =>
        ScrollTo(viewport, GetState(viewport).Offset + delta, animated);

    public void Fling(UiEntity viewport, float velocity)
    {
        EnsureViewport(viewport);
        if (!float.IsFinite(velocity))
            throw new ArgumentOutOfRangeException(nameof(velocity));
        ScrollState state = _world.Components.Get<ScrollState>(viewport);
        state.Velocity = velocity;
        state.Target = state.Offset;
        state.Motion = MathF.Abs(velocity) >= MinimumInertiaVelocity
            ? UiScrollMotionKind.Inertia
            : UiScrollMotionKind.Idle;
        state.LastSampleTimestamp = _world.Clock.Now;
        Store(viewport, in state);
        if (state.Motion != UiScrollMotionKind.Idle)
            _world.Scheduler.RequestContinuousFrame(UiContinuousReason.ScrollInertia);
        else
            _world.Scheduler.RequestReactiveFrame();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.Systems.Unregister(_system);
        _world.Scheduler.ReleaseContinuousFrame(UiContinuousReason.ScrollInertia);
        _panSessions.Clear();
        _disposed = true;
    }

    internal void Update(in UiFrameContext frame)
    {
        ProcessWheel();
        ProcessPan();
        TickMotion(frame.Now);
    }

    internal void UpdateVirtualExtent(UiEntity viewport, float extent, float? anchoredOffset = null)
    {
        EnsureViewport(viewport);
        ScrollState state = _world.Components.Get<ScrollState>(viewport);
        state.Extent = Math.Max(0f, extent);
        if (anchoredOffset.HasValue && state.Motion == UiScrollMotionKind.Idle)
        {
            state.Offset = Math.Clamp(anchoredOffset.Value, 0f, state.MaximumOffset);
            state.Target = state.Offset;
        }
        else if (state.Motion == UiScrollMotionKind.Idle)
        {
            state.Offset = Math.Clamp(state.Offset, 0f, state.MaximumOffset);
            state.Target = state.Offset;
        }
        Store(viewport, in state);
    }

    private void ProcessWheel()
    {
        IReadOnlyList<UiWheelDispatch> wheels = _input.FrameWheelEvents;
        for (int i = 0; i < wheels.Count; i++)
        {
            UiWheelDispatch dispatch = wheels[i];
            if (dispatch.Handled)
                continue;
            UiEntity viewport = FindScrollAncestor(dispatch.Target);
            while (viewport != UiEntity.None)
            {
                ScrollViewport policy = _world.Components.Get<ScrollViewport>(viewport);
                float wheelDelta = policy.Orientation == UiOrientation.Vertical
                    ? dispatch.Event.Delta.Y
                    : dispatch.Event.Delta.X;
                float delta = -wheelDelta * Math.Max(0f, policy.WheelStep);
                if (MathF.Abs(delta) > float.Epsilon && TryScrollWheel(viewport, delta))
                    break;
                viewport = FindParentScrollAncestor(viewport);
            }
        }
    }

    private bool TryScrollWheel(UiEntity viewport, float delta)
    {
        ScrollState state = _world.Components.Get<ScrollState>(viewport);
        float next = Math.Clamp(state.Offset + delta, 0f, state.MaximumOffset);
        if (MathF.Abs(next - state.Offset) <= 0.001f)
            return false;
        state.Offset = next;
        state.Target = next;
        state.Velocity = 0f;
        state.Motion = UiScrollMotionKind.Idle;
        state.LastSampleTimestamp = _world.Clock.Now;
        Store(viewport, in state);
        return true;
    }

    private void ProcessPan()
    {
        IReadOnlyList<UiRoutedEventRecord> records = _input.RoutedEvents.FrameRecords;
        for (int i = 0; i < records.Count; i++)
        {
            UiRoutedEventRecord record = records[i];
            if (record.Phase != UiRoutedEventPhase.Target ||
                record.Kind is not (UiRoutedEventKind.PanStarted or UiRoutedEventKind.PanDelta or UiRoutedEventKind.PanCompleted))
            {
                continue;
            }

            UiEntity viewport = FindScrollAncestor(record.Target);
            if (viewport == UiEntity.None)
                continue;
            UiPointerKey key = new(record.Data.InputRoot, record.Data.PointerId);
            if (record.Kind == UiRoutedEventKind.PanStarted)
            {
                ScrollState state = _world.Components.Get<ScrollState>(viewport);
                state.Motion = UiScrollMotionKind.Manipulation;
                state.Velocity = 0f;
                state.LastSampleTimestamp = record.Data.Timestamp;
                Store(viewport, in state);
                _panSessions[key] = new PanSession(viewport, record.Data.Timestamp, 0f);
                continue;
            }

            if (!_panSessions.TryGetValue(key, out PanSession session) || session.Viewport != viewport)
                continue;
            if (record.Kind == UiRoutedEventKind.PanDelta)
            {
                ScrollViewport policy = _world.Components.Get<ScrollViewport>(viewport);
                float pointerDelta = policy.Orientation == UiOrientation.Vertical
                    ? record.Data.Delta.Y
                    : record.Data.Delta.X;
                ScrollState state = _world.Components.Get<ScrollState>(viewport);
                float previous = state.Offset;
                float limit = Math.Max(0f, policy.OverscrollLimit);
                state.Offset = Math.Clamp(state.Offset - pointerDelta, -limit, state.MaximumOffset + limit);
                double seconds = Math.Max(0d, record.Data.Timestamp.SecondsSince(session.LastTimestamp));
                float velocity = seconds > 0.0001d
                    ? (state.Offset - previous) / (float)seconds
                    : session.Velocity;
                state.Velocity = (session.Velocity * 0.35f) + (velocity * 0.65f);
                state.LastSampleTimestamp = record.Data.Timestamp;
                Store(viewport, in state);
                _panSessions[key] = new PanSession(viewport, record.Data.Timestamp, state.Velocity);
                continue;
            }

            CompletePan(viewport, session.Velocity, record.Data.Timestamp);
            _panSessions.Remove(key);
        }
    }

    private void CompletePan(UiEntity viewport, float velocity, UiTimestamp timestamp)
    {
        ScrollState state = _world.Components.Get<ScrollState>(viewport);
        state.Velocity = velocity;
        state.LastSampleTimestamp = timestamp;
        if (state.Offset < 0f || state.Offset > state.MaximumOffset)
        {
            state.Target = Math.Clamp(state.Offset, 0f, state.MaximumOffset);
            state.Motion = UiScrollMotionKind.Spring;
        }
        else if (MathF.Abs(velocity) >= MinimumInertiaVelocity)
        {
            state.Target = state.Offset;
            state.Motion = UiScrollMotionKind.Inertia;
        }
        else
        {
            state.Target = state.Offset;
            state.Velocity = 0f;
            state.Motion = UiScrollMotionKind.Idle;
        }
        Store(viewport, in state);
        if (state.Motion != UiScrollMotionKind.Idle)
            _world.Scheduler.RequestContinuousFrame(UiContinuousReason.ScrollInertia);
    }

    private void TickMotion(UiTimestamp now)
    {
        _scrollEntities.Clear();
        _world.Components.Pool<ScrollState>().CopyEntitiesTo(_scrollEntities);
        bool active = false;
        for (int i = 0; i < _scrollEntities.Count; i++)
        {
            UiEntity entity = _scrollEntities[i];
            if (!_world.Entities.IsAlive(entity) ||
                !_world.Components.TryGet(entity, out ScrollState state) ||
                state.Motion is UiScrollMotionKind.Idle or UiScrollMotionKind.Manipulation)
            {
                continue;
            }

            ScrollViewport policy = _world.Components.Get<ScrollViewport>(entity);
            float dt = (float)Math.Clamp(now.SecondsSince(state.LastSampleTimestamp), 0d, 0.1d);
            state.LastSampleTimestamp = now;
            if (dt > 0f)
            {
                if (state.Motion == UiScrollMotionKind.Inertia)
                    TickInertia(ref state, in policy, dt);
                else
                    TickSpring(ref state, in policy, dt);
                Store(entity, in state);
            }
            active |= state.Motion != UiScrollMotionKind.Idle;
        }

        if (active)
            _world.Scheduler.RequestContinuousFrame(UiContinuousReason.ScrollInertia);
        else
            _world.Scheduler.ReleaseContinuousFrame(UiContinuousReason.ScrollInertia);
    }

    private static void TickInertia(ref ScrollState state, in ScrollViewport policy, float dt)
    {
        float friction = Math.Max(0.0001f, policy.InertiaFriction);
        float decay = MathF.Exp(-friction * dt);
        state.Offset += state.Velocity * (1f - decay) / friction;
        state.Velocity *= decay;
        if (state.Offset < 0f || state.Offset > state.MaximumOffset)
        {
            state.Target = Math.Clamp(state.Offset, 0f, state.MaximumOffset);
            state.Motion = UiScrollMotionKind.Spring;
            return;
        }
        if (MathF.Abs(state.Velocity) < SettledVelocity)
        {
            state.Velocity = 0f;
            state.Target = state.Offset;
            state.Motion = UiScrollMotionKind.Idle;
        }
    }

    private static void TickSpring(ref ScrollState state, in ScrollViewport policy, float dt)
    {
        state.Target = Math.Clamp(state.Target, 0f, state.MaximumOffset);
        int steps = Math.Clamp((int)MathF.Ceiling(dt * 120f), 1, 12);
        float step = dt / steps;
        float strength = Math.Max(0f, policy.SpringStrength);
        float damping = Math.Max(0f, policy.SpringDamping);
        for (int i = 0; i < steps; i++)
        {
            float acceleration = ((state.Target - state.Offset) * strength) - (state.Velocity * damping);
            state.Velocity += acceleration * step;
            state.Offset += state.Velocity * step;
        }
        if (MathF.Abs(state.Target - state.Offset) <= SettledDistance &&
            MathF.Abs(state.Velocity) <= SettledVelocity)
        {
            state.Offset = state.Target;
            state.Velocity = 0f;
            state.Motion = UiScrollMotionKind.Idle;
        }
    }

    private void Store(UiEntity viewport, in ScrollState state)
    {
        _world.Set(viewport, state);
        if (!_world.Hierarchy.TryGetNode(viewport, out HierarchyNode node) || node.FirstChild == UiEntity.None)
            return;
        ScrollViewport policy = _world.Components.Get<ScrollViewport>(viewport);
        ScrollContentTransform next = policy.Orientation == UiOrientation.Vertical
            ? new ScrollContentTransform { Y = state.Offset }
            : new ScrollContentTransform { X = state.Offset };
        UiEntity content = node.FirstChild;
        if (_world.Components.TryGet(content, out ScrollContentTransform previous) && previous.Equals(next))
            return;
        _world.Set(content, next);
        _world.Dirty.Mark(content, UiDirtyFlags.Transform | UiDirtyFlags.HitTest | UiDirtyFlags.Render);
        _world.Scheduler.RequestReactiveFrame();
    }

    private UiEntity FindScrollAncestor(UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (_world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (_world.Components.Has<ScrollViewport>(current) && _world.Components.Has<ScrollState>(current))
                return current;
            if (!_world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            current = node.Parent;
        }
        return UiEntity.None;
    }

    private UiEntity FindParentScrollAncestor(UiEntity entity)
    {
        if (!_world.Hierarchy.TryGetNode(entity, out HierarchyNode node))
            return UiEntity.None;
        return FindScrollAncestor(node.Parent);
    }

    private void EnsureViewport(UiEntity entity)
    {
        _world.Entities.EnsureAlive(entity);
        if (!_world.Components.Has<ScrollViewport>(entity) || !_world.Components.Has<ScrollState>(entity))
            throw new InvalidOperationException("Entity is not a scroll viewport: " + entity);
    }

    private readonly record struct PanSession(
        UiEntity Viewport,
        UiTimestamp LastTimestamp,
        float Velocity);
}

internal sealed class ScrollUpdateSystem(UiScrollRuntime scroll) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.VirtualizationPlan;
    public string Name => "scroll.update";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = world;
        scroll.Update(in frame);
    }
}
