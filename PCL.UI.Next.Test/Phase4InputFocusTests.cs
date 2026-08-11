// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class Phase4InputFocusTests
{
    [TestMethod]
    public void HitTest_UsesReverseSiblingRenderOrder()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Overlay(
                Ui.Button("Back").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(50)),
                Ui.Button("Front").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(50))));
        Drain(context.World);

        UiEntity back = FindKind(context.World, live, UiNodeKind.Button, occurrence: 0);
        UiEntity front = FindKind(context.World, live, UiNodeKind.Button, occurrence: 1);
        Assert.AreNotEqual(back, front);
        Assert.AreEqual(front, context.Runtime.Input.HitTest.HitTest(new UiPoint(25, 25), context.Scope));
    }

    [TestMethod]
    public void RoutedPointerEvent_VisitsCaptureTargetAndBubbleInOrder()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Container(Ui.Button("Go").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(50))));
        Drain(context.World);
        UiEntity root = live.RootEntity;
        UiEntity button = FindKind(context.World, live, UiNodeKind.Button);
        List<string> route = [];
        using IDisposable rootHandler = context.Runtime.Input.RoutedEvents.Register(
            root,
            UiRoutedEventKind.PointerDown,
            e => route.Add("root:" + e.Phase));
        using IDisposable buttonHandler = context.Runtime.Input.RoutedEvents.Register(
            button,
            UiRoutedEventKind.PointerDown,
            e => route.Add("button:" + e.Phase));

        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Down,
            Center(context.World, button),
            changedButton: UiPointerButton.Primary,
            buttons: UiPointerButtons.Primary);
        RunInputFrame(context.World);

        CollectionAssert.AreEqual(
            new[] { "root:Capture", "button:Target", "root:Bubble" },
            route);
    }

    [TestMethod]
    public void ButtonClick_UpdatesHoverFocusAndDispatchesCommand()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiCommand launch = new(42, "Launch");
        BlueprintInstance live = context.Instantiate(
            Ui.Button("Launch")
                .Command(launch)
                .Width(UiLength.Pixels(120))
                .Height(UiLength.Pixels(48)));
        Drain(context.World);
        UiEntity button = FindKind(context.World, live, UiNodeKind.Button);
        UiPoint point = Center(context.World, button);

        context.Runtime.Input.EnqueuePointer(context.Scope, UiPointerEventKind.Move, point);
        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Down,
            point,
            changedButton: UiPointerButton.Primary,
            buttons: UiPointerButtons.Primary);
        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Up,
            point,
            changedButton: UiPointerButton.Primary);
        RunInputFrame(context.World);

        InteractionState state = context.World.Components.Get<InteractionStateComponent>(button).Value;
        Assert.IsTrue((state & InteractionState.Hovered) != 0);
        Assert.IsTrue((state & InteractionState.Focused) != 0);
        Assert.IsFalse((state & InteractionState.Pressed) != 0);
        Assert.AreEqual(button, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Assert.IsTrue(context.Runtime.Input.Commands.TryDequeue(out UiCommandInvocation command));
        Assert.AreEqual(launch, command.Command);
        Assert.AreEqual(button, command.Source);
        Assert.AreEqual(UiCommandTrigger.Pointer, command.Trigger);
    }

    [TestMethod]
    public void HandledClick_DoesNotDispatchBoundCommand()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Button("Blocked")
                .Command(new UiCommand(43))
                .Width(UiLength.Pixels(100))
                .Height(UiLength.Pixels(40)));
        Drain(context.World);
        UiEntity button = FindKind(context.World, live, UiNodeKind.Button);
        using IDisposable handler = context.Runtime.Input.RoutedEvents.Register(
            button,
            UiRoutedEventKind.Click,
            e => e.Handled = true,
            UiRoutedEventPhase.Target);

        Click(context, button);

        Assert.AreEqual(0, context.Runtime.Input.Commands.Count);
    }

    [TestMethod]
    public void HandledPointerDown_SuppressesDefaultPressFocusAndGesture()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Button("Handled")
                .Command(new UiCommand(44))
                .Width(UiLength.Pixels(100))
                .Height(UiLength.Pixels(40)));
        Drain(context.World);
        UiEntity button = live.RootEntity;
        using IDisposable handler = context.Runtime.Input.RoutedEvents.Register(
            button,
            UiRoutedEventKind.PointerDown,
            e => e.Handled = true,
            UiRoutedEventPhase.Target);

        Click(context, button);

        InteractionState state = context.World.Components.Get<InteractionStateComponent>(button).Value;
        Assert.IsFalse((state & InteractionState.Pressed) != 0);
        Assert.IsFalse((state & InteractionState.Focused) != 0);
        Assert.AreEqual(UiEntity.None, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Assert.AreEqual(0, context.Runtime.Input.Commands.Count);
    }

    [TestMethod]
    public void PointerCapture_RoutesOutsideHitToCapturedEntityUntilReleased()
    {
        using TestContext context = Create(new UiSize(240, 80));
        BlueprintInstance live = context.Instantiate(
            Ui.Row(
                Ui.Button("A").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(50)),
                Ui.Button("B").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(50)))
                .Gap(10));
        Drain(context.World);
        UiEntity first = FindKind(context.World, live, UiNodeKind.Button, 0);
        UiEntity second = FindKind(context.World, live, UiNodeKind.Button, 1);
        Assert.IsTrue(context.Runtime.Input.PointerCapture.Capture(7, first));

        context.Runtime.Input.EnqueuePointer(context.Scope, UiPointerEventKind.Move, Center(context.World, second), 7);
        RunInputFrame(context.World);
        UiRoutedEventRecord capturedMove = context.Runtime.Input.RoutedEvents.FrameRecords
            .First(record => record.Kind == UiRoutedEventKind.PointerMove && record.Phase == UiRoutedEventPhase.Target);
        Assert.AreEqual(first, capturedMove.Target);

        Assert.IsTrue(context.Runtime.Input.PointerCapture.Release(7));
        context.Runtime.Input.EnqueuePointer(context.Scope, UiPointerEventKind.Move, Center(context.World, second), 7);
        RunInputFrame(context.World);
        UiRoutedEventRecord normalMove = context.Runtime.Input.RoutedEvents.FrameRecords
            .First(record => record.Kind == UiRoutedEventKind.PointerMove && record.Phase == UiRoutedEventPhase.Target);
        Assert.AreEqual(second, normalMove.Target);
    }

    [TestMethod]
    public void AutomaticCapture_AppliesToLaterBatchedEventsOutsideBounds()
    {
        using TestContext context = Create(new UiSize(120, 80));
        BlueprintInstance live = context.Instantiate(
            Ui.Container()
                .Gestures(UiGestureMask.Drag)
                .Width(UiLength.Pixels(80))
                .Height(UiLength.Pixels(40)));
        Drain(context.World);
        UiEntity target = live.RootEntity;

        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Down,
            new UiPoint(10, 10),
            changedButton: UiPointerButton.Primary,
            buttons: UiPointerButtons.Primary);
        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Move,
            new UiPoint(200, 200),
            buttons: UiPointerButtons.Primary);
        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Up,
            new UiPoint(200, 200),
            changedButton: UiPointerButton.Primary);
        RunInputFrame(context.World);

        Assert.IsTrue(context.Runtime.Input.RoutedEvents.FrameRecords.Any(record =>
            record.Kind == UiRoutedEventKind.PointerMove &&
            record.Phase == UiRoutedEventPhase.Target &&
            record.Target == target));
        Assert.AreEqual(UiEntity.None, context.Runtime.Input.PointerCapture.GetCaptured(0));
    }

    [TestMethod]
    public void ViewportResize_RefreshesRetainedHitTestBounds()
    {
        using TestContext context = Create(new UiSize(200, 80));
        context.Instantiate(
            Ui.Button("Wide")
                .Width(UiLength.Pixels(180))
                .Height(UiLength.Pixels(40)));
        Drain(context.World);
        Assert.AreNotEqual(
            UiEntity.None,
            context.Runtime.Input.HitTest.HitTest(new UiPoint(150, 20), context.Scope));

        context.Runtime.SetViewport(new UiSize(100, 80));
        Drain(context.World);

        Assert.AreEqual(
            UiEntity.None,
            context.Runtime.Input.HitTest.HitTest(new UiPoint(150, 20), context.Scope));
    }

    [TestMethod]
    public void Focus_TabShiftTabAndArrowNavigateDeterministically()
    {
        using TestContext context = Create(new UiSize(320, 80));
        BlueprintInstance live = context.Instantiate(
            Ui.Row(
                Ui.Button("A").TabIndex(0).Width(UiLength.Pixels(80)).Height(UiLength.Pixels(40)),
                Ui.Button("B").TabIndex(1).Width(UiLength.Pixels(80)).Height(UiLength.Pixels(40)),
                Ui.Button("C").TabIndex(2).Width(UiLength.Pixels(80)).Height(UiLength.Pixels(40)))
                .Gap(10));
        Drain(context.World);
        UiEntity first = FindKind(context.World, live, UiNodeKind.Button, 0);
        UiEntity second = FindKind(context.World, live, UiNodeKind.Button, 1);
        UiEntity third = FindKind(context.World, live, UiNodeKind.Button, 2);

        Key(context, UiKey.Tab);
        Assert.AreEqual(first, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Key(context, UiKey.Tab);
        Assert.AreEqual(second, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Key(context, UiKey.Tab, UiInputModifiers.Shift);
        Assert.AreEqual(first, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Key(context, UiKey.Right);
        Assert.AreEqual(second, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Key(context, UiKey.Right);
        Assert.AreEqual(third, context.Runtime.Input.Focus.GetFocused(context.Scope));
    }

    [TestMethod]
    public void FocusTrap_RestrictsTraversalAndRestoresPreviousFocus()
    {
        using TestContext context = Create(new UiSize(320, 160));
        BlueprintInstance live = context.Instantiate(
            Ui.Column(
                Ui.Button("Outside").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(40)),
                Ui.Row(
                        Ui.Button("Inside A").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(40)),
                        Ui.Button("Inside B").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(40)))
                    .FocusScope(trap: true)
                    .Gap(8))
                .Gap(10));
        Drain(context.World);
        UiEntity outside = FindKind(context.World, live, UiNodeKind.Button, 0);
        UiEntity insideA = FindKind(context.World, live, UiNodeKind.Button, 1);
        UiEntity insideB = FindKind(context.World, live, UiNodeKind.Button, 2);
        UiEntity trap = FindKind(context.World, live, UiNodeKind.Row);
        Assert.IsTrue(context.Runtime.Input.Focus.Focus(outside, context.Clock.Now));

        Assert.IsTrue(context.Runtime.Input.Focus.ActivateScope(trap, context.Clock.Now));
        Assert.AreEqual(insideA, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Key(context, UiKey.Tab);
        Assert.AreEqual(insideB, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Key(context, UiKey.Tab);
        Assert.AreEqual(insideA, context.Runtime.Input.Focus.GetFocused(context.Scope));

        Assert.IsTrue(context.Runtime.Input.Focus.DeactivateScope(trap, context.Clock.Now));
        Assert.AreEqual(outside, context.Runtime.Input.Focus.GetFocused(context.Scope));
    }

    [TestMethod]
    public void NestedFocusScopes_RestoreOuterTrapThenOriginalFocus()
    {
        using TestContext context = Create(new UiSize(320, 200));
        BlueprintInstance live = context.Instantiate(
            Ui.Column(
                Ui.Button("Outside").Height(UiLength.Pixels(32)),
                Ui.Column(
                        Ui.Button("Outer").Height(UiLength.Pixels(32)),
                        Ui.Column(Ui.Button("Inner").Height(UiLength.Pixels(32)))
                            .FocusScope(trap: true))
                    .FocusScope(trap: true)));
        Drain(context.World);
        UiEntity outside = FindKind(context.World, live, UiNodeKind.Button, 0);
        UiEntity outerButton = FindKind(context.World, live, UiNodeKind.Button, 1);
        UiEntity innerButton = FindKind(context.World, live, UiNodeKind.Button, 2);
        UiEntity outerScope = FindKind(context.World, live, UiNodeKind.Column, 1);
        UiEntity innerScope = FindKind(context.World, live, UiNodeKind.Column, 2);
        Assert.IsTrue(context.Runtime.Input.Focus.Focus(outside, context.Clock.Now));

        Assert.IsTrue(context.Runtime.Input.Focus.ActivateScope(outerScope, context.Clock.Now));
        Assert.AreEqual(outerButton, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Assert.IsTrue(context.Runtime.Input.Focus.ActivateScope(innerScope, context.Clock.Now));
        Assert.AreEqual(innerButton, context.Runtime.Input.Focus.GetFocused(context.Scope));

        Assert.IsTrue(context.Runtime.Input.Focus.DeactivateScope(innerScope, context.Clock.Now));
        Assert.AreEqual(outerButton, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Assert.IsTrue(context.Runtime.Input.Focus.DeactivateScope(outerScope, context.Clock.Now));
        Assert.AreEqual(outside, context.Runtime.Input.Focus.GetFocused(context.Scope));
    }

    [TestMethod]
    public void KeyboardEnter_ActivatesFocusedCommand()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Button("Run")
                .Command(new UiCommand(77))
                .Width(UiLength.Pixels(100))
                .Height(UiLength.Pixels(40)));
        Drain(context.World);
        UiEntity button = FindKind(context.World, live, UiNodeKind.Button);
        Assert.IsTrue(context.Runtime.Input.Focus.Focus(button, context.Clock.Now));

        Key(context, UiKey.Enter);

        Assert.IsTrue(context.Runtime.Input.Commands.TryDequeue(out UiCommandInvocation invocation));
        Assert.AreEqual(77, invocation.Command.Id);
        Assert.AreEqual(UiCommandTrigger.Keyboard, invocation.Trigger);
        Assert.IsTrue(context.Runtime.Input.RoutedEvents.FrameRecords.Any(record =>
            record.Kind == UiRoutedEventKind.Click && record.Target == button));
    }

    [TestMethod]
    public void BatchedTabThenEnter_UsesFocusEstablishedEarlierInSameFrame()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Button("Run")
                .Command(new UiCommand(78))
                .Width(UiLength.Pixels(100))
                .Height(UiLength.Pixels(40)));
        Drain(context.World);

        context.Runtime.Input.EnqueueKey(context.Scope, UiKeyEventKind.Down, UiKey.Tab);
        context.Runtime.Input.EnqueueKey(context.Scope, UiKeyEventKind.Down, UiKey.Enter);
        RunInputFrame(context.World);

        Assert.AreEqual(live.RootEntity, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Assert.IsTrue(context.Runtime.Input.Commands.TryDequeue(out UiCommandInvocation invocation));
        Assert.AreEqual(78, invocation.Command.Id);
        Assert.AreEqual(UiCommandTrigger.Keyboard, invocation.Trigger);
    }

    [TestMethod]
    public void Shortcut_DispatchesCommandThroughCentralRegistry()
    {
        using TestContext context = Create(new UiSize(100, 100));
        context.Instantiate(Ui.Container());
        Drain(context.World);
        using IDisposable registration = context.Runtime.Input.Shortcuts.Register(
            context.Scope,
            new UiKeyGesture(UiKey.F, UiInputModifiers.Control),
            new UiCommand(88, "Find"));

        Key(context, UiKey.F, UiInputModifiers.Control);

        Assert.IsTrue(context.Runtime.Input.Commands.TryDequeue(out UiCommandInvocation invocation));
        Assert.AreEqual(88, invocation.Command.Id);
        Assert.AreEqual(UiCommandTrigger.Shortcut, invocation.Trigger);
    }

    [TestMethod]
    public void Gesture_DoubleClickEmitsClickAndDoubleClick()
    {
        using TestContext context = Create(new UiSize(150, 80));
        BlueprintInstance live = context.Instantiate(
            Ui.Button("Double").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(40)));
        Drain(context.World);
        UiEntity button = FindKind(context.World, live, UiNodeKind.Button);

        Click(context, button);
        context.Clock.Advance(0.1);
        Click(context, button);

        Assert.IsTrue(context.Runtime.Input.RoutedEvents.FrameRecords.Any(record =>
            record.Kind == UiRoutedEventKind.Click && record.Target == button));
        Assert.IsTrue(context.Runtime.Input.RoutedEvents.FrameRecords.Any(record =>
            record.Kind == UiRoutedEventKind.DoubleClick && record.Target == button));
    }

    [TestMethod]
    public void Gesture_LongPressUsesContinuousSchedulerAndSuppressesClick()
    {
        using TestContext context = Create(new UiSize(150, 80));
        BlueprintInstance live = context.Instantiate(
            Ui.Container()
                .Gestures(UiGestureMask.LongPress | UiGestureMask.Click)
                .Width(UiLength.Pixels(100))
                .Height(UiLength.Pixels(40)));
        Drain(context.World);
        UiEntity target = live.RootEntity;
        UiPoint point = Center(context.World, target);

        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Down,
            point,
            changedButton: UiPointerButton.Primary,
            buttons: UiPointerButtons.Primary);
        RunInputFrame(context.World);
        Assert.IsTrue((context.World.Scheduler.ContinuousReasons & UiContinuousReason.Gesture) != 0);

        context.Clock.Advance(0.7);
        Assert.IsTrue(context.World.Update());
        Assert.IsTrue(context.Runtime.Input.RoutedEvents.FrameRecords.Any(record =>
            record.Kind == UiRoutedEventKind.LongPress && record.Target == target));
        Assert.IsFalse((context.World.Scheduler.ContinuousReasons & UiContinuousReason.Gesture) != 0);

        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Up,
            point,
            changedButton: UiPointerButton.Primary);
        RunInputFrame(context.World);
        Assert.IsFalse(context.Runtime.Input.RoutedEvents.FrameRecords.Any(record =>
            record.Kind == UiRoutedEventKind.Click && record.Target == target));
    }

    [TestMethod]
    public void Gesture_DragAndPanEmitStartDeltaAndComplete()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Container()
                .Gestures(UiGestureMask.Drag | UiGestureMask.Pan)
                .Width(UiLength.Pixels(160))
                .Height(UiLength.Pixels(80)));
        Drain(context.World);
        UiEntity target = live.RootEntity;

        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Down,
            new UiPoint(10, 10),
            changedButton: UiPointerButton.Primary,
            buttons: UiPointerButtons.Primary);
        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Move,
            new UiPoint(40, 20),
            buttons: UiPointerButtons.Primary);
        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Up,
            new UiPoint(50, 25),
            changedButton: UiPointerButton.Primary);
        RunInputFrame(context.World);

        UiRoutedEventKind[] kinds = context.Runtime.Input.RoutedEvents.FrameRecords
            .Where(record => record.Target == target && record.Phase == UiRoutedEventPhase.Target)
            .Select(record => record.Kind)
            .ToArray();
        CollectionAssert.Contains(kinds, UiRoutedEventKind.DragStarted);
        CollectionAssert.Contains(kinds, UiRoutedEventKind.DragDelta);
        CollectionAssert.Contains(kinds, UiRoutedEventKind.DragCompleted);
        CollectionAssert.Contains(kinds, UiRoutedEventKind.PanStarted);
        CollectionAssert.Contains(kinds, UiRoutedEventKind.PanDelta);
        CollectionAssert.Contains(kinds, UiRoutedEventKind.PanCompleted);
    }

    [TestMethod]
    public void Gesture_PinchEmitsScaleAndCompletion()
    {
        using TestContext context = Create(new UiSize(200, 120));
        BlueprintInstance live = context.Instantiate(
            Ui.Container()
                .Gestures(UiGestureMask.Pinch)
                .Width(UiLength.Pixels(180))
                .Height(UiLength.Pixels(100)));
        Drain(context.World);
        UiEntity target = live.RootEntity;

        context.Runtime.Input.EnqueuePointer(context.Scope, UiPointerEventKind.Down, new UiPoint(30, 30), 1);
        context.Runtime.Input.EnqueuePointer(context.Scope, UiPointerEventKind.Down, new UiPoint(60, 30), 2);
        context.Runtime.Input.EnqueuePointer(context.Scope, UiPointerEventKind.Move, new UiPoint(90, 30), 2);
        context.Runtime.Input.EnqueuePointer(context.Scope, UiPointerEventKind.Up, new UiPoint(90, 30), 2);
        context.Runtime.Input.EnqueuePointer(context.Scope, UiPointerEventKind.Up, new UiPoint(30, 30), 1);
        RunInputFrame(context.World);

        Assert.IsTrue(context.Runtime.Input.RoutedEvents.FrameRecords.Any(record =>
            record.Kind == UiRoutedEventKind.PinchStarted && record.Target == target));
        UiRoutedEventRecord delta = context.Runtime.Input.RoutedEvents.FrameRecords.First(record =>
            record.Kind == UiRoutedEventKind.PinchDelta && record.Target == target);
        Assert.IsTrue(delta.Data.Scale > 1f);
        Assert.IsTrue(context.Runtime.Input.RoutedEvents.FrameRecords.Any(record =>
            record.Kind == UiRoutedEventKind.PinchCompleted && record.Target == target));
    }

    [TestMethod]
    public void DestroyingEntity_ClearsFocusAndPointerCapture()
    {
        using TestContext context = Create(new UiSize(100, 60));
        BlueprintInstance live = context.Instantiate(
            Ui.Button("Gone").Width(UiLength.Pixels(80)).Height(UiLength.Pixels(40)));
        Drain(context.World);
        UiEntity button = live.RootEntity;
        Assert.IsTrue(context.Runtime.Input.Focus.Focus(button, context.Clock.Now));
        Assert.IsTrue(context.Runtime.Input.PointerCapture.Capture(3, button));

        context.Instantiator.Destroy(live);

        Assert.AreEqual(UiEntity.None, context.Runtime.Input.Focus.GetFocused(context.Scope));
        Assert.AreEqual(UiEntity.None, context.Runtime.Input.PointerCapture.GetCaptured(3));
    }

    [TestMethod]
    public void Authoring_CompilesP4InteractionComponents()
    {
        using TestContext context = Create(new UiSize(100, 60));
        BlueprintInstance live = context.Instantiate(
            Ui.Container()
                .HitTestVisible()
                .TabIndex(7)
                .FocusScope(trap: true, restorePrevious: false)
                .Gestures(UiGestureMask.Drag));

        UiEntity entity = live.RootEntity;
        Assert.IsTrue(context.World.Components.Has<HitTestableComponent>(entity));
        Assert.AreEqual(7, context.World.Components.Get<FocusableComponent>(entity).TabIndex);
        FocusScopeComponent scope = context.World.Components.Get<FocusScopeComponent>(entity);
        Assert.IsTrue(scope.IsTrap);
        Assert.IsFalse(scope.RestorePreviousFocus);
        Assert.AreEqual(UiGestureMask.Drag, context.World.Components.Get<GestureComponent>(entity).Enabled);
    }

    private static TestContext Create(UiSize viewport)
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        DeterministicTextEngine text = new();
        UiPhase4Runtime runtime = new(world, text, viewport);
        UiScopeId scope = world.CreateRootScope();
        PresentationStore store = new();
        BlueprintInstantiator instantiator = new(world, store);
        return new TestContext(clock, world, runtime, scope, instantiator);
    }

    private static void Click(TestContext context, UiEntity entity)
    {
        UiPoint point = Center(context.World, entity);
        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Down,
            point,
            changedButton: UiPointerButton.Primary,
            buttons: UiPointerButtons.Primary);
        context.Runtime.Input.EnqueuePointer(
            context.Scope,
            UiPointerEventKind.Up,
            point,
            changedButton: UiPointerButton.Primary);
        RunInputFrame(context.World);
    }

    private static void Key(
        TestContext context,
        UiKey key,
        UiInputModifiers modifiers = UiInputModifiers.None)
    {
        context.Runtime.Input.EnqueueKey(context.Scope, UiKeyEventKind.Down, key, modifiers);
        RunInputFrame(context.World);
    }

    private static UiPoint Center(UiWorld world, UiEntity entity)
    {
        UiRect rect = world.Components.Get<LayoutRect>(entity).Value;
        return new UiPoint(rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f));
    }

    private static UiEntity FindKind(
        UiWorld world,
        BlueprintInstance live,
        UiNodeKind kind,
        int occurrence = 0)
    {
        int found = 0;
        for (int i = 0; i < live.Blueprint.NodeCount; i++)
        {
            UiEntity entity = live.EntityAt(i);
            if (!world.Entities.IsAlive(entity) ||
                !world.Components.TryGet(entity, out NodeKindComponent component) ||
                component.Kind != kind)
            {
                continue;
            }

            if (found++ == occurrence)
                return entity;
        }

        return UiEntity.None;
    }

    private static void RunInputFrame(UiWorld world)
    {
        Assert.IsTrue(world.Scheduler.NeedsFrame);
        Assert.IsTrue(world.Update());
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 12)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame, "P4 runtime did not settle to idle.");
    }

    private sealed class TestContext : IDisposable
    {
        public TestContext(
            DeterministicUiClock clock,
            UiWorld world,
            UiPhase4Runtime runtime,
            UiScopeId scope,
            BlueprintInstantiator instantiator)
        {
            Clock = clock;
            World = world;
            Runtime = runtime;
            Scope = scope;
            Instantiator = instantiator;
        }

        public DeterministicUiClock Clock { get; }
        public UiWorld World { get; }
        public UiPhase4Runtime Runtime { get; }
        public UiScopeId Scope { get; }
        public BlueprintInstantiator Instantiator { get; }

        public BlueprintInstance Instantiate(UiNode root) =>
            Instantiator.Instantiate(Ui.Compile(root), Scope);

        public void Dispose() => Runtime.Dispose();
    }
}
