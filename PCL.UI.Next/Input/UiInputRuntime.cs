// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Composition root for P4 input, hit testing, interaction, focus and gestures.</summary>
public sealed class UiInputRuntime : IDisposable
{
    private readonly List<UiInputEvent> _normalized = [];
    private readonly List<TargetedInputEvent> _targeted = [];
    private readonly Dictionary<int, UiEntity> _hoveredByPointer = [];
    private readonly Dictionary<int, UiEntity> _pressedByPointer = [];
    private readonly Dictionary<UiEntity, int> _pressCounts = [];
    private readonly HashSet<int> _automaticCaptures = [];
    private readonly InputNormalizeSystem _normalizeSystem;
    private readonly InputHitTestSystem _hitTestSystem;
    private readonly InteractionSystem _interactionSystem;
    private readonly FocusGestureShortcutSystem _focusSystem;
    private readonly HitTestUpdateSystem _hitTestUpdateSystem;
    private readonly UiGestureRecognizer _gestures;
    private bool _disposed;

    public UiInputRuntime(
        UiWorld world,
        UiGestureThresholds? gestureThresholds = null)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        World.EntityDestroying += OnEntityDestroying;
        HitTest = new UiHitTestIndex(world);
        RoutedEvents = new UiRoutedEventRouter(world);
        PointerCapture = new UiPointerCapture(world);
        Focus = new UiFocusManager(world, RoutedEvents, HitTest);
        Shortcuts = new UiShortcutRegistry(world);
        Commands = new UiCommandQueue();
        _gestures = new UiGestureRecognizer(
            world,
            RoutedEvents,
            Commands,
            gestureThresholds ?? UiGestureThresholds.Default);

        _normalizeSystem = new InputNormalizeSystem(this);
        _hitTestSystem = new InputHitTestSystem(this);
        _interactionSystem = new InteractionSystem(this);
        _focusSystem = new FocusGestureShortcutSystem(this);
        _hitTestUpdateSystem = new HitTestUpdateSystem(this);
        world.Systems.Register(_normalizeSystem);
        world.Systems.Register(_hitTestSystem);
        world.Systems.Register(_interactionSystem);
        world.Systems.Register(_focusSystem);
        world.Systems.Register(_hitTestUpdateSystem);
        world.Scheduler.RequestReactiveFrame();
    }

    public UiWorld World { get; }
    public UiHitTestIndex HitTest { get; }
    public UiRoutedEventRouter RoutedEvents { get; }
    public UiPointerCapture PointerCapture { get; }
    public UiFocusManager Focus { get; }
    public UiShortcutRegistry Shortcuts { get; }
    public UiCommandQueue Commands { get; }
    public IReadOnlyList<UiInputEvent> FrameInputEvents => _normalized;

    public void EnqueuePointer(
        UiScopeId scope,
        UiPointerEventKind kind,
        UiPoint position,
        int pointerId = 0,
        UiPointerButton changedButton = UiPointerButton.None,
        UiPointerButtons buttons = UiPointerButtons.None,
        UiInputModifiers modifiers = UiInputModifiers.None,
        UiTimestamp? timestamp = null)
    {
        if (pointerId < 0)
            throw new ArgumentOutOfRangeException(nameof(pointerId));
        UiPlatformEvent platformEvent = UiPlatformInput.Pointer(
            scope,
            kind,
            timestamp ?? World.Clock.Now,
            position,
            pointerId,
            changedButton,
            buttons,
            modifiers);
        World.EnqueuePlatformEvent(in platformEvent);
    }

    public void EnqueueKey(
        UiScopeId scope,
        UiKeyEventKind kind,
        UiKey key,
        UiInputModifiers modifiers = UiInputModifiers.None,
        bool isRepeat = false,
        UiTimestamp? timestamp = null)
    {
        UiPlatformEvent platformEvent = UiPlatformInput.Key(
            scope,
            kind,
            timestamp ?? World.Clock.Now,
            key,
            modifiers,
            isRepeat);
        World.EnqueuePlatformEvent(in platformEvent);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        World.Systems.Unregister(_hitTestUpdateSystem);
        World.Systems.Unregister(_focusSystem);
        World.Systems.Unregister(_interactionSystem);
        World.Systems.Unregister(_hitTestSystem);
        World.Systems.Unregister(_normalizeSystem);
        World.EntityDestroying -= OnEntityDestroying;
        _gestures.Dispose();
        Shortcuts.Dispose();
        Focus.Dispose();
        PointerCapture.Dispose();
        RoutedEvents.Dispose();
        Commands.Clear();
        _normalized.Clear();
        _targeted.Clear();
        _hoveredByPointer.Clear();
        _pressedByPointer.Clear();
        _pressCounts.Clear();
        _automaticCaptures.Clear();
        _disposed = true;
    }

    internal void Normalize()
    {
        _normalized.Clear();
        _targeted.Clear();
        RoutedEvents.BeginFrame();
        List<UiPlatformEvent> platformEvents = World.FrameBuffers.PlatformEvents;
        for (int i = 0; i < platformEvents.Count; i++)
        {
            if (UiPlatformInput.TryNormalize(platformEvents[i], out UiInputEvent input))
                _normalized.Add(input);
        }
    }

    internal void ResolveTargets()
    {
        _targeted.Clear();
        for (int i = 0; i < _normalized.Count; i++)
        {
            UiInputEvent input = _normalized[i];
            if (input.Kind == UiInputEventKind.Pointer)
            {
                UiPointerEvent pointer = input.Pointer;
                UiEntity hit = HitTest.HitTest(pointer.Position, pointer.Scope);
                UiEntity captured = PointerCapture.GetCaptured(pointer.PointerId);
                _targeted.Add(new TargetedInputEvent(input, captured != UiEntity.None ? captured : hit, hit));
            }
            else
            {
                UiEntity focused = Focus.GetFocused(input.Key.Scope);
                _targeted.Add(new TargetedInputEvent(input, focused, focused));
            }
        }
    }

    internal void ProcessInteraction()
    {
        for (int i = 0; i < _targeted.Count; i++)
        {
            TargetedInputEvent targeted = _targeted[i];
            if (targeted.Input.Kind != UiInputEventKind.Pointer)
                continue;
            UiPointerEvent pointer = targeted.Input.Pointer;
            UiEntity captured = PointerCapture.GetCaptured(pointer.PointerId);
            targeted.Target = captured != UiEntity.None ? captured : targeted.HitTarget;
            UpdateHover(pointer, targeted.HitTarget);
            if (targeted.Target != UiEntity.None)
            {
                UiRoutedEventData data = new(
                    pointer.Timestamp,
                    pointer.Position,
                    default,
                    pointer.PointerId,
                    pointer.ChangedButton,
                    Modifiers: pointer.Modifiers);
                targeted.Handled = RoutedEvents.Dispatch(ToRoutedKind(pointer.Kind), targeted.Target, in data);
            }

            if (pointer.Kind == UiPointerEventKind.Down)
            {
                if (targeted.Handled)
                {
                    _targeted[i] = targeted;
                    continue;
                }
                UiEntity pressed = FindBehaviorAncestor(targeted.Target, UiBehavior.Pressable);
                if (pressed != UiEntity.None)
                {
                    _pressedByPointer[pointer.PointerId] = pressed;
                    int count = _pressCounts.TryGetValue(pressed, out int currentCount)
                        ? currentCount + 1
                        : 1;
                    _pressCounts[pressed] = count;
                    if (count == 1)
                        InteractionStateStore.Set(World, pressed, InteractionState.Pressed, enabled: true);
                }

                UiEntity gestureTarget = _gestures.FindGestureTarget(targeted.Target);
                if (PointerCapture.GetCaptured(pointer.PointerId) == UiEntity.None &&
                    (pressed != UiEntity.None || gestureTarget != UiEntity.None))
                {
                    UiEntity capture = gestureTarget != UiEntity.None ? gestureTarget : pressed;
                    if (PointerCapture.Capture(pointer.PointerId, capture))
                        _automaticCaptures.Add(pointer.PointerId);
                }
            }
            else if (pointer.Kind is UiPointerEventKind.Up or UiPointerEventKind.Cancel)
            {
                if (_pressedByPointer.Remove(pointer.PointerId, out UiEntity pressed))
                    ReleasePressed(pressed);
            }

            _targeted[i] = targeted;
        }
    }

    internal void ProcessFocusGesturesAndShortcuts(in UiFrameContext frame)
    {
        for (int i = 0; i < _targeted.Count; i++)
        {
            TargetedInputEvent targeted = _targeted[i];
            if (targeted.Input.Kind == UiInputEventKind.Pointer)
            {
                UiPointerEvent pointer = targeted.Input.Pointer;
                if (pointer.Kind == UiPointerEventKind.Down)
                {
                    if (!targeted.Handled)
                    {
                        UiEntity focusable = Focus.FindFocusableAncestor(targeted.Target);
                        if (focusable != UiEntity.None)
                            Focus.Focus(focusable, pointer.Timestamp);
                    }
                }

                _gestures.ProcessPointer(in targeted);
                if (pointer.Kind is UiPointerEventKind.Up or UiPointerEventKind.Cancel &&
                    _automaticCaptures.Remove(pointer.PointerId))
                {
                    PointerCapture.Release(pointer.PointerId);
                }
            }
            else
            {
                UiKeyEvent key = targeted.Input.Key;
                ProcessKey(in key, Focus.GetFocused(key.Scope));
            }
        }

        _gestures.UpdateTimers(frame.Now);
    }

    internal void UpdateHitTest() => HitTest.Update();

    private void ProcessKey(in UiKeyEvent key, UiEntity target)
    {
        bool handled = false;
        if (target != UiEntity.None)
        {
            UiRoutedEventData data = new(
                key.Timestamp,
                Key: key.Key,
                Modifiers: key.Modifiers);
            handled = RoutedEvents.Dispatch(
                key.Kind == UiKeyEventKind.Down ? UiRoutedEventKind.KeyDown : UiRoutedEventKind.KeyUp,
                target,
                in data);
        }

        if (handled || key.Kind != UiKeyEventKind.Down)
            return;
        if (key.Key == UiKey.Tab)
        {
            Focus.MoveTab(key.Scope, (key.Modifiers & UiInputModifiers.Shift) != 0, key.Timestamp);
            return;
        }

        if (key.Key is UiKey.Left or UiKey.Up or UiKey.Right or UiKey.Down)
        {
            Focus.MoveDirectional(key.Scope, key.Key, key.Timestamp);
            return;
        }

        UiScopeId shortcutScope = World.Entities.TryGetScope(target, out UiScopeId targetScope)
            ? targetScope
            : key.Scope;
        if (Shortcuts.TryResolve(shortcutScope, in key, out UiCommand shortcut))
        {
            UiCommandInvocation invocation = new(
                shortcut,
                target,
                key.Scope,
                UiCommandTrigger.Shortcut,
                key.Timestamp);
            Commands.Enqueue(in invocation);
            return;
        }

        if (key.Key is UiKey.Enter or UiKey.Space && target != UiEntity.None)
            _gestures.ActivateFromKeyboard(target, in key);
    }

    private void UpdateHover(in UiPointerEvent pointer, UiEntity hitTarget)
    {
        if (pointer.PointerId != 0)
            return;
        if (pointer.Kind == UiPointerEventKind.Cancel)
            hitTarget = UiEntity.None;
        _hoveredByPointer.TryGetValue(pointer.PointerId, out UiEntity previous);
        if (previous == hitTarget)
            return;

        if (previous != UiEntity.None)
        {
            SetHoverPath(previous, enabled: false);
            if (World.Entities.IsAlive(previous))
            {
                UiRoutedEventData leave = new(pointer.Timestamp, pointer.Position, PointerId: pointer.PointerId);
                RoutedEvents.Dispatch(UiRoutedEventKind.PointerLeave, previous, in leave);
            }
        }

        if (hitTarget == UiEntity.None)
        {
            _hoveredByPointer.Remove(pointer.PointerId);
            return;
        }

        _hoveredByPointer[pointer.PointerId] = hitTarget;
        SetHoverPath(hitTarget, enabled: true);
        UiRoutedEventData enter = new(pointer.Timestamp, pointer.Position, PointerId: pointer.PointerId);
        RoutedEvents.Dispatch(UiRoutedEventKind.PointerEnter, hitTarget, in enter);
    }

    private void SetHoverPath(UiEntity entity, bool enabled)
    {
        UiEntity current = entity;
        int guard = 0;
        while (World.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (World.Components.TryGet(current, out BehaviorComponent behavior) &&
                (behavior.Flags & UiBehavior.Hoverable) != 0)
            {
                InteractionStateStore.Set(World, current, InteractionState.Hovered, enabled);
            }

            if (!World.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            current = node.Parent;
        }
    }

    private UiEntity FindBehaviorAncestor(UiEntity entity, UiBehavior behavior)
    {
        UiEntity current = entity;
        int guard = 0;
        while (World.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (World.Components.TryGet(current, out BehaviorComponent component) &&
                (component.Flags & behavior) != 0)
            {
                return current;
            }

            if (!World.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            current = node.Parent;
        }

        return UiEntity.None;
    }

    private void ReleasePressed(UiEntity entity)
    {
        if (!_pressCounts.TryGetValue(entity, out int count))
            return;
        if (count > 1)
        {
            _pressCounts[entity] = count - 1;
            return;
        }

        _pressCounts.Remove(entity);
        InteractionStateStore.Set(World, entity, InteractionState.Pressed, enabled: false);
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        foreach (int pointerId in _hoveredByPointer
                     .Where(pair => pair.Value == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _hoveredByPointer.Remove(pointerId);
        }

        foreach (int pointerId in _pressedByPointer
                     .Where(pair => pair.Value == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _pressedByPointer.Remove(pointerId);
            _automaticCaptures.Remove(pointerId);
        }
        _pressCounts.Remove(entity);

        foreach (int pointerId in _automaticCaptures.ToArray())
        {
            if (PointerCapture.GetCaptured(pointerId) == entity)
                _automaticCaptures.Remove(pointerId);
        }
    }

    private static UiRoutedEventKind ToRoutedKind(UiPointerEventKind kind) => kind switch
    {
        UiPointerEventKind.Move => UiRoutedEventKind.PointerMove,
        UiPointerEventKind.Down => UiRoutedEventKind.PointerDown,
        UiPointerEventKind.Up => UiRoutedEventKind.PointerUp,
        UiPointerEventKind.Cancel => UiRoutedEventKind.PointerCancel,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal struct TargetedInputEvent
{
    public TargetedInputEvent(UiInputEvent input, UiEntity target, UiEntity hitTarget)
    {
        Input = input;
        Target = target;
        HitTarget = hitTarget;
    }

    public UiInputEvent Input { get; }
    public UiEntity Target { get; set; }
    public UiEntity HitTarget { get; }
    public bool Handled { get; set; }
}

internal sealed class InputNormalizeSystem(UiInputRuntime input) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.InputNormalize;
    public string Name => "input.normalize";
    public void Update(UiWorld world, in UiFrameContext frame) => input.Normalize();
}

internal sealed class InputHitTestSystem(UiInputRuntime input) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.HitTest;
    public string Name => "input.hit-test";
    public void Update(UiWorld world, in UiFrameContext frame) => input.ResolveTargets();
}

internal sealed class InteractionSystem(UiInputRuntime input) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.Interaction;
    public string Name => "input.interaction";
    public void Update(UiWorld world, in UiFrameContext frame) => input.ProcessInteraction();
}

internal sealed class FocusGestureShortcutSystem(UiInputRuntime input) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.FocusGestureShortcut;
    public string Name => "input.focus-gesture-shortcut";
    public void Update(UiWorld world, in UiFrameContext frame) =>
        input.ProcessFocusGesturesAndShortcuts(in frame);
}

internal sealed class HitTestUpdateSystem(UiInputRuntime input) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.ClipHitTestUpdate;
    public string Name => "input.hit-test-update";
    public void Update(UiWorld world, in UiFrameContext frame) => input.UpdateHitTest();
}
