// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Composition root for input, hit testing, interaction, focus and gestures.</summary>
public sealed class UiInputRuntime : IDisposable
{
    private readonly List<UiInputEvent> _normalized = [];
    private readonly List<TargetedInputEvent> _targeted = [];
    private readonly Dictionary<UiPointerKey, UiEntity> _hoveredByPointer = [];
    private readonly Dictionary<UiPointerKey, UiEntity> _pressedByPointer = [];
    private readonly Dictionary<UiEntity, int> _pressCounts = [];
    private readonly HashSet<UiPointerKey> _automaticCaptures = [];
    private readonly List<UiWheelDispatch> _wheelDispatches = [];
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
        InputRoots = new UiInputRootRegistry(world);
        HitTest = new UiHitTestIndex(world, InputRoots);
        RoutedEvents = new UiRoutedEventRouter(world);
        PointerCapture = new UiPointerCapture(world, InputRoots);
        Focus = new UiFocusManager(world, RoutedEvents, HitTest, InputRoots);
        Shortcuts = new UiShortcutRegistry(world);
        Commands = new UiCommandQueue();
        _gestures = new UiGestureRecognizer(
            world,
            RoutedEvents,
            Commands,
            InputRoots,
            gestureThresholds ?? UiGestureThresholds.Default);
        InputRoots.InputRootDestroying += OnInputRootDestroying;

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
    public UiInputRootRegistry InputRoots { get; }
    public UiHitTestIndex HitTest { get; }
    public UiRoutedEventRouter RoutedEvents { get; }
    public UiPointerCapture PointerCapture { get; }
    public UiFocusManager Focus { get; }
    public UiShortcutRegistry Shortcuts { get; }
    public UiCommandQueue Commands { get; }
    public IReadOnlyList<UiInputEvent> FrameInputEvents => _normalized;
    public IReadOnlyList<UiWheelDispatch> FrameWheelEvents => _wheelDispatches;

    public void EnqueuePointer(
        UiInputRootId inputRoot,
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
        UiScopeId scope = InputRoots.GetScope(inputRoot);
        UiPlatformEvent platformEvent = UiPlatformInput.Pointer(
            inputRoot,
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
        UiInputRootId inputRoot,
        UiKeyEventKind kind,
        UiKey key,
        UiInputModifiers modifiers = UiInputModifiers.None,
        bool isRepeat = false,
        UiTimestamp? timestamp = null)
    {
        UiScopeId scope = InputRoots.GetScope(inputRoot);
        UiPlatformEvent platformEvent = UiPlatformInput.Key(
            inputRoot,
            scope,
            kind,
            timestamp ?? World.Clock.Now,
            key,
            modifiers,
            isRepeat);
        World.EnqueuePlatformEvent(in platformEvent);
    }

    public void EnqueueWheel(
        UiInputRootId inputRoot,
        UiPoint position,
        UiPoint delta,
        UiInputModifiers modifiers = UiInputModifiers.None,
        UiTimestamp? timestamp = null)
    {
        UiScopeId scope = InputRoots.GetScope(inputRoot);
        UiPlatformEvent platformEvent = UiPlatformInput.Wheel(
            inputRoot,
            scope,
            timestamp ?? World.Clock.Now,
            position,
            delta,
            modifiers);
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
        InputRoots.InputRootDestroying -= OnInputRootDestroying;
        _gestures.Dispose();
        Shortcuts.Dispose();
        Focus.Dispose();
        PointerCapture.Dispose();
        RoutedEvents.Dispose();
        InputRoots.Dispose();
        Commands.Clear();
        _normalized.Clear();
        _targeted.Clear();
        _hoveredByPointer.Clear();
        _pressedByPointer.Clear();
        _pressCounts.Clear();
        _automaticCaptures.Clear();
        _wheelDispatches.Clear();
        _disposed = true;
    }

    internal void Normalize()
    {
        _normalized.Clear();
        _targeted.Clear();
        _wheelDispatches.Clear();
        RoutedEvents.BeginFrame();
        List<UiPlatformEvent> platformEvents = World.FrameBuffers.PlatformEvents;
        for (int i = 0; i < platformEvents.Count; i++)
        {
            if (!UiPlatformInput.TryNormalize(platformEvents[i], out UiInputEvent input))
                continue;
            UiScopeId scope = input.Kind switch
            {
                UiInputEventKind.Pointer => input.Pointer.Scope,
                UiInputEventKind.Key => input.Key.Scope,
                UiInputEventKind.Wheel => input.Wheel.Scope,
                _ => default
            };
            UiInputRootId inputRoot = input.Kind switch
            {
                UiInputEventKind.Pointer => input.Pointer.InputRoot,
                UiInputEventKind.Key => input.Key.InputRoot,
                UiInputEventKind.Wheel => input.Wheel.InputRoot,
                _ => default
            };
            if (InputRoots.TryResolve(scope, out UiInputRootId resolved) && resolved == inputRoot)
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
                UiEntity hit = HitTest.HitTest(pointer.Position, pointer.InputRoot);
                UiEntity captured = PointerCapture.GetCaptured(pointer.InputRoot, pointer.PointerId);
                _targeted.Add(new TargetedInputEvent(input, captured != UiEntity.None ? captured : hit, hit));
            }
            else if (input.Kind == UiInputEventKind.Key)
            {
                UiEntity focused = Focus.GetFocused(input.Key.InputRoot);
                _targeted.Add(new TargetedInputEvent(input, focused, focused));
            }
            else
            {
                UiEntity hit = HitTest.HitTest(input.Wheel.Position, input.Wheel.InputRoot);
                _targeted.Add(new TargetedInputEvent(input, hit, hit));
            }
        }
    }

    internal void ProcessInteraction()
    {
        for (int i = 0; i < _targeted.Count; i++)
        {
            TargetedInputEvent targeted = _targeted[i];
            if (targeted.Input.Kind == UiInputEventKind.Wheel)
            {
                UiWheelEvent wheel = targeted.Input.Wheel;
                if (targeted.Target != UiEntity.None)
                {
                    UiRoutedEventData data = new(
                        wheel.Timestamp,
                        wheel.Position,
                        wheel.Delta,
                        Modifiers: wheel.Modifiers,
                        InputRoot: wheel.InputRoot);
                    targeted.Handled = RoutedEvents.Dispatch(
                        UiRoutedEventKind.PointerWheel,
                        targeted.Target,
                        in data);
                }

                _wheelDispatches.Add(new UiWheelDispatch(wheel, targeted.Target, targeted.Handled));
                _targeted[i] = targeted;
                continue;
            }

            if (targeted.Input.Kind != UiInputEventKind.Pointer)
                continue;
            UiPointerEvent pointer = targeted.Input.Pointer;
            UiPointerKey pointerKey = new(pointer.InputRoot, pointer.PointerId);
            UiEntity captured = PointerCapture.GetCaptured(pointer.InputRoot, pointer.PointerId);
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
                    Modifiers: pointer.Modifiers,
                    InputRoot: pointer.InputRoot);
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
                    _pressedByPointer[pointerKey] = pressed;
                    int count = _pressCounts.TryGetValue(pressed, out int currentCount)
                        ? currentCount + 1
                        : 1;
                    _pressCounts[pressed] = count;
                    if (count == 1)
                        InteractionStateStore.Set(World, pressed, InteractionState.Pressed, enabled: true);
                }

                UiEntity gestureTarget = _gestures.FindGestureTarget(targeted.Target);
                if (PointerCapture.GetCaptured(pointer.InputRoot, pointer.PointerId) == UiEntity.None &&
                    (pressed != UiEntity.None || gestureTarget != UiEntity.None))
                {
                    UiEntity capture = gestureTarget != UiEntity.None ? gestureTarget : pressed;
                    if (PointerCapture.Capture(pointer.InputRoot, pointer.PointerId, capture))
                        _automaticCaptures.Add(pointerKey);
                }
            }
            else if (pointer.Kind is UiPointerEventKind.Up or UiPointerEventKind.Cancel)
            {
                if (_pressedByPointer.Remove(pointerKey, out UiEntity pressed))
                    ReleasePressed(pressed);
            }

            _targeted[i] = targeted;
        }
    }

    internal void ProcessFocusGesturesAndShortcuts(in UiFrameContext frame)
    {
        Focus.Validate(frame.Now);
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
                UiPointerKey pointerKey = new(pointer.InputRoot, pointer.PointerId);
                if (pointer.Kind is UiPointerEventKind.Up or UiPointerEventKind.Cancel &&
                    _automaticCaptures.Remove(pointerKey))
                {
                    PointerCapture.Release(pointer.InputRoot, pointer.PointerId);
                }
            }
            else if (targeted.Input.Kind == UiInputEventKind.Key)
            {
                UiKeyEvent key = targeted.Input.Key;
                ProcessKey(in key, Focus.GetFocused(key.InputRoot));
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
                Modifiers: key.Modifiers,
                InputRoot: key.InputRoot);
            handled = RoutedEvents.Dispatch(
                key.Kind == UiKeyEventKind.Down ? UiRoutedEventKind.KeyDown : UiRoutedEventKind.KeyUp,
                target,
                in data);
        }

        if (handled || key.Kind != UiKeyEventKind.Down)
            return;
        if (key.Key == UiKey.Tab)
        {
            Focus.MoveTab(key.InputRoot, (key.Modifiers & UiInputModifiers.Shift) != 0, key.Timestamp);
            return;
        }

        if (key.Key is UiKey.Left or UiKey.Up or UiKey.Right or UiKey.Down)
        {
            Focus.MoveDirectional(key.InputRoot, key.Key, key.Timestamp);
            return;
        }

        UiScopeId shortcutScope = World.Entities.TryGetScope(target, out UiScopeId targetScope)
            ? targetScope
            : InputRoots.GetScope(key.InputRoot);
        if (Shortcuts.TryResolve(shortcutScope, in key, out UiCommand shortcut))
        {
            if (target != UiEntity.None &&
                !UiInteractionPolicy.IsAllowed(World, target, UiInteractionCapability.CommandInvoke))
            {
                return;
            }
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
        UiPointerKey pointerKey = new(pointer.InputRoot, pointer.PointerId);
        _hoveredByPointer.TryGetValue(pointerKey, out UiEntity previous);
        if (previous == hitTarget)
            return;

        if (previous != UiEntity.None)
        {
            SetHoverPath(previous, enabled: false);
            if (World.Entities.IsAlive(previous))
            {
                UiRoutedEventData leave = new(
                    pointer.Timestamp,
                    pointer.Position,
                    PointerId: pointer.PointerId,
                    InputRoot: pointer.InputRoot);
                RoutedEvents.Dispatch(UiRoutedEventKind.PointerLeave, previous, in leave);
            }
        }

        if (hitTarget == UiEntity.None)
        {
            _hoveredByPointer.Remove(pointerKey);
            return;
        }

        _hoveredByPointer[pointerKey] = hitTarget;
        SetHoverPath(hitTarget, enabled: true);
        UiRoutedEventData enter = new(
            pointer.Timestamp,
            pointer.Position,
            PointerId: pointer.PointerId,
            InputRoot: pointer.InputRoot);
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
        foreach (UiPointerKey pointerKey in _hoveredByPointer
                     .Where(pair => pair.Value == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _hoveredByPointer.Remove(pointerKey);
        }

        foreach (UiPointerKey pointerKey in _pressedByPointer
                     .Where(pair => pair.Value == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _pressedByPointer.Remove(pointerKey);
            _automaticCaptures.Remove(pointerKey);
        }
        _pressCounts.Remove(entity);

        foreach (UiPointerKey pointerKey in _automaticCaptures.ToArray())
        {
            if (PointerCapture.GetCaptured(pointerKey.InputRoot, pointerKey.PointerId) == entity)
                _automaticCaptures.Remove(pointerKey);
        }
    }

    private void OnInputRootDestroying(UiInputRootId inputRoot)
    {
        foreach (UiPointerKey pointerKey in _hoveredByPointer.Keys
                     .Where(key => key.InputRoot == inputRoot)
                     .ToArray())
        {
            if (_hoveredByPointer.Remove(pointerKey, out UiEntity hovered))
                SetHoverPath(hovered, enabled: false);
        }

        foreach (UiPointerKey pointerKey in _pressedByPointer.Keys
                     .Where(key => key.InputRoot == inputRoot)
                     .ToArray())
        {
            if (_pressedByPointer.Remove(pointerKey, out UiEntity pressed))
                ReleasePressed(pressed);
        }

        _automaticCaptures.RemoveWhere(key => key.InputRoot == inputRoot);
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

public readonly record struct UiWheelDispatch(
    UiWheelEvent Event,
    UiEntity Target,
    bool Handled);

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
