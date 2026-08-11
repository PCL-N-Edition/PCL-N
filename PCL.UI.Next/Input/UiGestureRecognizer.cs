// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

internal sealed class UiGestureRecognizer : IDisposable
{
    private readonly UiWorld _world;
    private readonly UiRoutedEventRouter _router;
    private readonly UiCommandQueue _commands;
    private readonly UiGestureThresholds _thresholds;
    private readonly Dictionary<int, PointerSession> _sessions = [];
    private readonly Dictionary<UiEntity, PinchSession> _pinches = [];
    private readonly Dictionary<UiEntity, LastClick> _lastClicks = [];
    private bool _disposed;

    public UiGestureRecognizer(
        UiWorld world,
        UiRoutedEventRouter router,
        UiCommandQueue commands,
        UiGestureThresholds thresholds)
    {
        _world = world;
        _router = router;
        _commands = commands;
        _thresholds = thresholds;
        _world.EntityDestroying += OnEntityDestroying;
    }

    public void ProcessPointer(in TargetedInputEvent input)
    {
        UiPointerEvent pointer = input.Input.Pointer;
        if (input.Handled &&
            pointer.Kind != UiPointerEventKind.Down &&
            _sessions.TryGetValue(pointer.PointerId, out PointerSession? handledSession))
        {
            handledSession.SuppressClick = true;
            if (pointer.Kind == UiPointerEventKind.Move)
            {
                RefreshScheduling();
                return;
            }
        }

        switch (pointer.Kind)
        {
            case UiPointerEventKind.Down:
                Begin(pointer, input.Target, input.Handled);
                break;
            case UiPointerEventKind.Move:
                Move(pointer);
                break;
            case UiPointerEventKind.Up:
                End(pointer, input.HitTarget, canceled: false);
                break;
            case UiPointerEventKind.Cancel:
                End(pointer, UiEntity.None, canceled: true);
                break;
        }

        RefreshScheduling();
    }

    public void UpdateTimers(UiTimestamp now)
    {
        foreach (PointerSession session in _sessions.Values)
        {
            if (session.LongPressFired || session.SuppressClick ||
                (session.Gestures & UiGestureMask.LongPress) == 0 ||
                now.SecondsSince(session.DownTimestamp) < _thresholds.LongPressSeconds)
            {
                continue;
            }

            session.LongPressFired = true;
            session.SuppressClick = true;
            Dispatch(
                UiRoutedEventKind.LongPress,
                session.Target,
                new UiRoutedEventData(
                    now,
                    session.Current,
                    default,
                    session.PointerId,
                    session.Button,
                    Modifiers: session.Modifiers));
        }

        RefreshScheduling();
    }

    public void ActivateFromKeyboard(UiEntity entity, in UiKeyEvent keyEvent)
    {
        UiEntity target = FindGestureTarget(entity);
        if (target == UiEntity.None)
            return;
        bool handled = Dispatch(
            UiRoutedEventKind.Click,
            target,
            new UiRoutedEventData(
                keyEvent.Timestamp,
                Key: keyEvent.Key,
                Modifiers: keyEvent.Modifiers));
        if (!handled)
            DispatchCommand(target, UiCommandTrigger.Keyboard, keyEvent.Timestamp);
    }

    public UiEntity FindGestureTarget(UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (_world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (EffectiveGestures(current) != UiGestureMask.None)
                return current;
            if (!_world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            current = node.Parent;
        }

        return UiEntity.None;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.EntityDestroying -= OnEntityDestroying;
        _world.Scheduler.ReleaseContinuousFrame(UiContinuousReason.Gesture);
        _sessions.Clear();
        _pinches.Clear();
        _lastClicks.Clear();
        _disposed = true;
    }

    private void Begin(in UiPointerEvent pointer, UiEntity rawTarget, bool handled)
    {
        UiEntity target = FindGestureTarget(rawTarget);
        if (handled || target == UiEntity.None)
            return;

        UiGestureMask gestures = EffectiveGestures(target);
        PointerSession session = new()
        {
            PointerId = pointer.PointerId,
            Target = target,
            Gestures = gestures,
            Down = pointer.Position,
            Current = pointer.Position,
            Last = pointer.Position,
            DownTimestamp = pointer.Timestamp,
            Button = pointer.ChangedButton,
            Modifiers = pointer.Modifiers
        };
        _sessions[pointer.PointerId] = session;
        TryStartPinch(target, pointer.Timestamp);
    }

    private void Move(in UiPointerEvent pointer)
    {
        if (!_sessions.TryGetValue(pointer.PointerId, out PointerSession? session))
            return;
        session.Last = session.Current;
        session.Current = pointer.Position;
        session.Modifiers = pointer.Modifiers;

        if (_pinches.TryGetValue(session.Target, out PinchSession? pinch))
        {
            UpdatePinch(pinch, pointer.Timestamp);
            return;
        }

        UiPoint total = Subtract(session.Current, session.Down);
        UiPoint delta = Subtract(session.Current, session.Last);
        float distance = Length(total);
        if (distance > _thresholds.ClickDistance)
            session.SuppressClick = true;

        if (!session.DragStarted &&
            (session.Gestures & UiGestureMask.Drag) != 0 &&
            distance >= _thresholds.DragDistance)
        {
            session.DragStarted = true;
            session.SuppressClick = true;
            InteractionStateStore.Set(_world, session.Target, InteractionState.Dragging, enabled: true);
            Dispatch(
                UiRoutedEventKind.DragStarted,
                session.Target,
                PointerData(pointer.Timestamp, session, total));
        }

        if (session.DragStarted)
        {
            Dispatch(
                UiRoutedEventKind.DragDelta,
                session.Target,
                PointerData(pointer.Timestamp, session, delta));
        }

        if (!session.PanStarted &&
            (session.Gestures & UiGestureMask.Pan) != 0 &&
            distance >= _thresholds.DragDistance)
        {
            session.PanStarted = true;
            session.SuppressClick = true;
            Dispatch(
                UiRoutedEventKind.PanStarted,
                session.Target,
                PointerData(pointer.Timestamp, session, total));
        }

        if (session.PanStarted)
        {
            Dispatch(
                UiRoutedEventKind.PanDelta,
                session.Target,
                PointerData(pointer.Timestamp, session, delta));
        }
    }

    private void End(in UiPointerEvent pointer, UiEntity hitTarget, bool canceled)
    {
        if (!_sessions.TryGetValue(pointer.PointerId, out PointerSession? session))
            return;
        session.Last = session.Current;
        session.Current = pointer.Position;

        if (_pinches.TryGetValue(session.Target, out PinchSession? pinch))
            CompletePinch(pinch, pointer.Timestamp);

        UiPoint total = Subtract(session.Current, session.Down);
        if (session.DragStarted)
        {
            Dispatch(
                UiRoutedEventKind.DragCompleted,
                session.Target,
                PointerData(pointer.Timestamp, session, total));
            InteractionStateStore.Set(_world, session.Target, InteractionState.Dragging, enabled: false);
        }

        if (session.PanStarted)
        {
            Dispatch(
                UiRoutedEventKind.PanCompleted,
                session.Target,
                PointerData(pointer.Timestamp, session, total));
        }

        if (!canceled && session.Button == UiPointerButton.Primary &&
            !session.SuppressClick && !session.LongPressFired &&
            (session.Gestures & UiGestureMask.Click) != 0 &&
            FindGestureTarget(hitTarget) == session.Target)
        {
            bool handled = Dispatch(
                UiRoutedEventKind.Click,
                session.Target,
                PointerData(pointer.Timestamp, session, total));
            if (!handled)
                DispatchCommand(session.Target, UiCommandTrigger.Pointer, pointer.Timestamp);
            TryDoubleClick(session, pointer.Timestamp);
        }

        _sessions.Remove(pointer.PointerId);
    }

    private void TryDoubleClick(PointerSession session, UiTimestamp timestamp)
    {
        if ((session.Gestures & UiGestureMask.DoubleClick) == 0)
            return;
        if (_lastClicks.TryGetValue(session.Target, out LastClick last) &&
            timestamp.SecondsSince(last.Timestamp) <= _thresholds.DoubleClickSeconds &&
            Length(Subtract(session.Current, last.Position)) <= _thresholds.ClickDistance)
        {
            Dispatch(
                UiRoutedEventKind.DoubleClick,
                session.Target,
                PointerData(timestamp, session, default));
            _lastClicks.Remove(session.Target);
            return;
        }

        _lastClicks[session.Target] = new LastClick(timestamp, session.Current);
    }

    private void TryStartPinch(UiEntity target, UiTimestamp timestamp)
    {
        if ((EffectiveGestures(target) & UiGestureMask.Pinch) == 0 || _pinches.ContainsKey(target))
            return;
        PointerSession[] matching = _sessions.Values.Where(session => session.Target == target).Take(2).ToArray();
        if (matching.Length < 2)
            return;
        float distance = Length(Subtract(matching[1].Current, matching[0].Current));
        if (distance <= float.Epsilon)
            distance = 1f;
        matching[0].SuppressClick = true;
        matching[1].SuppressClick = true;
        PinchSession pinch = new()
        {
            Target = target,
            Pointer0 = matching[0].PointerId,
            Pointer1 = matching[1].PointerId,
            InitialDistance = distance,
            LastCenter = Midpoint(matching[0].Current, matching[1].Current)
        };
        _pinches[target] = pinch;
        Dispatch(
            UiRoutedEventKind.PinchStarted,
            target,
            new UiRoutedEventData(timestamp, pinch.LastCenter));
    }

    private void UpdatePinch(PinchSession pinch, UiTimestamp timestamp)
    {
        if (!_sessions.TryGetValue(pinch.Pointer0, out PointerSession? first) ||
            !_sessions.TryGetValue(pinch.Pointer1, out PointerSession? second))
        {
            return;
        }

        UiPoint center = Midpoint(first.Current, second.Current);
        float distance = Length(Subtract(second.Current, first.Current));
        float scale = distance / pinch.InitialDistance;
        UiPoint delta = Subtract(center, pinch.LastCenter);
        pinch.LastCenter = center;
        Dispatch(
            UiRoutedEventKind.PinchDelta,
            pinch.Target,
            new UiRoutedEventData(timestamp, center, delta, Scale: scale));
    }

    private void CompletePinch(PinchSession pinch, UiTimestamp timestamp)
    {
        Dispatch(
            UiRoutedEventKind.PinchCompleted,
            pinch.Target,
            new UiRoutedEventData(timestamp, pinch.LastCenter));
        if (_sessions.TryGetValue(pinch.Pointer0, out PointerSession? first))
            first.SuppressClick = true;
        if (_sessions.TryGetValue(pinch.Pointer1, out PointerSession? second))
            second.SuppressClick = true;
        _pinches.Remove(pinch.Target);
    }

    private UiGestureMask EffectiveGestures(UiEntity entity)
    {
        UiGestureMask result = _world.Components.TryGet(entity, out GestureComponent component)
            ? component.Enabled
            : UiGestureMask.None;
        if (_world.Components.TryGet(entity, out BehaviorComponent behavior) &&
            (behavior.Flags & UiBehavior.Clickable) != 0)
        {
            result |= UiGestureMask.Click | UiGestureMask.DoubleClick;
        }

        return result;
    }

    private bool Dispatch(UiRoutedEventKind kind, UiEntity target, UiRoutedEventData data) =>
        _router.Dispatch(kind, target, in data);

    private void DispatchCommand(UiEntity source, UiCommandTrigger trigger, UiTimestamp timestamp)
    {
        if (!_world.Components.TryGet(source, out CommandBindingComponent binding) || binding.CommandId == 0)
            return;
        UiScopeId scope = _world.Entities.GetScope(source);
        UiCommandInvocation invocation = new(
            new UiCommand(binding.CommandId),
            source,
            scope,
            trigger,
            timestamp);
        _commands.Enqueue(in invocation);
    }

    private void RefreshScheduling()
    {
        bool pendingLongPress = _sessions.Values.Any(session =>
            !session.LongPressFired &&
            !session.SuppressClick &&
            (session.Gestures & UiGestureMask.LongPress) != 0);
        if (pendingLongPress)
            _world.Scheduler.RequestContinuousFrame(UiContinuousReason.Gesture);
        else
            _world.Scheduler.ReleaseContinuousFrame(UiContinuousReason.Gesture);
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        foreach (int pointerId in _sessions
                     .Where(pair => pair.Value.Target == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _sessions.Remove(pointerId);
        }
        _pinches.Remove(entity);
        _lastClicks.Remove(entity);
        RefreshScheduling();
    }

    private static UiRoutedEventData PointerData(
        UiTimestamp timestamp,
        PointerSession session,
        UiPoint delta) =>
        new(
            timestamp,
            session.Current,
            delta,
            session.PointerId,
            session.Button,
            Modifiers: session.Modifiers);

    private static UiPoint Subtract(UiPoint left, UiPoint right) =>
        new(left.X - right.X, left.Y - right.Y);

    private static UiPoint Midpoint(UiPoint left, UiPoint right) =>
        new((left.X + right.X) * 0.5f, (left.Y + right.Y) * 0.5f);

    private static float Length(UiPoint value) =>
        MathF.Sqrt((value.X * value.X) + (value.Y * value.Y));

    private sealed class PointerSession
    {
        public int PointerId;
        public UiEntity Target;
        public UiGestureMask Gestures;
        public UiPoint Down;
        public UiPoint Current;
        public UiPoint Last;
        public UiTimestamp DownTimestamp;
        public UiPointerButton Button;
        public UiInputModifiers Modifiers;
        public bool SuppressClick;
        public bool LongPressFired;
        public bool DragStarted;
        public bool PanStarted;
    }

    private sealed class PinchSession
    {
        public UiEntity Target;
        public int Pointer0;
        public int Pointer1;
        public float InitialDistance;
        public UiPoint LastCenter;
    }

    private readonly record struct LastClick(UiTimestamp Timestamp, UiPoint Position);
}
