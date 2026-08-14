// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class OverlayTests
{
    [TestMethod]
    public void Popup_UsesChildScopePlacementAndRestoresFocus()
    {
        using TestContext context = Create();
        BlueprintInstance main = context.Instantiate(
            Ui.Column(
                    Ui.Button("Before").Height(UiLength.Pixels(36)),
                    Ui.Button("Anchor").Height(UiLength.Pixels(36)))
                .Width(UiLength.Pixels(180)));
        Drain(context.World);
        UiEntity before = main.EntityAt(1);
        UiEntity anchor = main.EntityAt(3);
        Assert.IsTrue(context.Runtime.Input.Focus.Focus(before, context.Clock.Now));

        UiOverlayHandle popup = context.Overlays.OpenPopup(
            Ui.Compile(Ui.Button("Popup").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(30))),
            anchor);
        Drain(context.World);

        Assert.IsTrue(context.Overlays.TryGetOverlay(popup, out UiOverlaySnapshot snapshot));
        Assert.IsTrue(context.World.Scopes.TryGetParent(snapshot.Scope, out UiScopeId scopeParent));
        Assert.AreEqual(context.WindowScope, scopeParent);
        UiRect anchorRect = context.World.Components.Get<LayoutRect>(anchor).Value;
        UiRect popupRect = context.World.Components.Get<LayoutRect>(snapshot.RootEntity).Value;
        Assert.IsGreaterThanOrEqualTo(anchorRect.Bottom, popupRect.Y);
        Assert.AreEqual(snapshot.RootEntity, context.Runtime.Input.Focus.GetFocused(context.InputRoot));

        Assert.IsTrue(context.Overlays.Close(popup));
        Drain(context.World);
        Assert.IsFalse(context.World.Scopes.IsAlive(snapshot.Scope));
        Assert.AreEqual(before, context.Runtime.Input.Focus.GetFocused(context.InputRoot));
    }

    [TestMethod]
    public void Modal_BarrierBlocksUnderlyingPointerAndTrapsTabFocus()
    {
        using TestContext context = Create();
        UiCommand backgroundCommand = new(12);
        BlueprintInstance main = context.Instantiate(
            Ui.Button("Background")
                .Command(backgroundCommand)
                .Width(UiLength.Pixels(180))
                .Height(UiLength.Pixels(80)));
        Drain(context.World);
        Assert.IsTrue(context.Runtime.Input.Focus.Focus(main.RootEntity, context.Clock.Now));

        UiOverlayHandle modal = context.Overlays.ShowModal(
            Ui.Compile(
                Ui.Column(
                        Ui.Button("Accept"),
                        Ui.Button("Cancel"))
                    .Width(UiLength.Pixels(140))
                    .Height(UiLength.Pixels(90))));
        Drain(context.World);
        Assert.IsTrue(context.Overlays.TryGetOverlay(modal, out UiOverlaySnapshot snapshot));
        Assert.AreEqual(snapshot.BarrierEntity, context.Runtime.Input.HitTest.HitTest(new UiPoint(4, 4), context.InputRoot));

        context.Runtime.Input.EnqueuePointer(
            context.InputRoot,
            UiPointerEventKind.Down,
            new UiPoint(4, 4),
            changedButton: UiPointerButton.Primary,
            buttons: UiPointerButtons.Primary);
        Assert.IsTrue(context.World.Update());
        context.Runtime.Input.EnqueuePointer(
            context.InputRoot,
            UiPointerEventKind.Up,
            new UiPoint(4, 4),
            changedButton: UiPointerButton.Primary);
        Assert.IsTrue(context.World.Update());
        Assert.IsFalse(context.Runtime.Input.Commands.TryDequeue(out _));
        Assert.IsTrue(context.Overlays.TryGetOverlay(modal, out _));

        context.Runtime.Input.EnqueueKey(context.InputRoot, UiKeyEventKind.Down, UiKey.Tab);
        Assert.IsTrue(context.World.Update());
        UiEntity focused = context.Runtime.Input.Focus.GetFocused(context.InputRoot);
        Assert.IsTrue(IsDescendantOrSelf(context.World, snapshot.RootEntity, focused));

        Assert.IsTrue(context.Overlays.Close(modal));
        Drain(context.World);
        Assert.AreEqual(main.RootEntity, context.Runtime.Input.Focus.GetFocused(context.InputRoot));
    }

    [TestMethod]
    public void Popup_OutsidePointerDismissesWithoutReachingBackground()
    {
        using TestContext context = Create();
        BlueprintInstance main = context.Instantiate(
            Ui.Button("Anchor").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(40)));
        Drain(context.World);
        UiOverlayHandle popup = context.Overlays.OpenPopup(
            Ui.Compile(Ui.Button("Popup").Width(UiLength.Pixels(80)).Height(UiLength.Pixels(30))),
            main.RootEntity);
        Drain(context.World);
        Assert.IsTrue(context.Overlays.TryGetOverlay(popup, out UiOverlaySnapshot snapshot));

        UiPoint outside = new(230, 110);
        Assert.AreEqual(snapshot.BarrierEntity, context.Runtime.Input.HitTest.HitTest(outside, context.InputRoot));
        context.Runtime.Input.EnqueuePointer(
            context.InputRoot,
            UiPointerEventKind.Down,
            outside,
            changedButton: UiPointerButton.Primary,
            buttons: UiPointerButtons.Primary);
        Assert.IsTrue(context.World.Update());

        Assert.IsFalse(context.Overlays.TryGetOverlay(popup, out _));
        Assert.IsFalse(context.World.Scopes.IsAlive(snapshot.Scope));
    }

    [TestMethod]
    public void Tooltip_HonorsDelayAndRemainsInputPassThrough()
    {
        using TestContext context = Create();
        BlueprintInstance main = context.Instantiate(
            Ui.Button("Hover").Width(UiLength.Pixels(100)).Height(UiLength.Pixels(40)));
        using UiTooltipRegistration tooltip = context.Overlays.AttachTooltip(
            main.RootEntity,
            Ui.Compile(Ui.Button("Tooltip").Width(UiLength.Pixels(90)).Height(UiLength.Pixels(28))),
            UiTooltipOptions.Default with { DelaySeconds = 0.5d });
        Drain(context.World);
        UiRect owner = context.World.Components.Get<LayoutRect>(main.RootEntity).Value;
        UiPoint pointer = new(owner.X + 10f, owner.Y + 10f);

        context.Runtime.Input.EnqueuePointer(context.InputRoot, UiPointerEventKind.Move, pointer);
        Assert.IsTrue(context.World.Update());
        Assert.IsTrue(context.World.Scheduler.HasContinuous);
        context.Clock.Advance(0.4d);
        Assert.IsTrue(context.World.Update());
        Assert.IsTrue(tooltip.ActiveOverlay.IsNone);
        context.Clock.Advance(0.2d);
        Assert.IsTrue(context.World.Update());
        Assert.IsFalse(tooltip.ActiveOverlay.IsNone);
        Drain(context.World);

        Assert.IsTrue(context.Overlays.TryGetOverlay(tooltip.ActiveOverlay, out UiOverlaySnapshot snapshot));
        UiRect tooltipRect = context.World.Components.Get<LayoutRect>(snapshot.RootEntity).Value;
        UiPoint tooltipCenter = new(
            tooltipRect.X + tooltipRect.Width * 0.5f,
            tooltipRect.Y + tooltipRect.Height * 0.5f);
        UiEntity hit = context.Runtime.Input.HitTest.HitTest(tooltipCenter, context.InputRoot);
        Assert.IsFalse(IsDescendantOrSelf(context.World, snapshot.RootEntity, hit));

        context.Runtime.Input.EnqueuePointer(context.InputRoot, UiPointerEventKind.Move, new UiPoint(220, 110));
        Assert.IsTrue(context.World.Update());
        Drain(context.World);
        Assert.IsTrue(tooltip.ActiveOverlay.IsNone);
        Assert.IsFalse(context.World.Scopes.IsAlive(snapshot.Scope));
    }

    [TestMethod]
    public void OwnerScopeDisposal_ClosesPopupAndInvalidatesHandle()
    {
        using TestContext context = Create();
        UiScopeId pageScope = context.World.CreateScope(context.WindowScope);
        BlueprintInstance page = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Button("Anchor")),
            pageScope);
        Drain(context.World);
        UiOverlayHandle popup = context.Overlays.OpenPopup(
            Ui.Compile(Ui.Button("Popup")),
            page.RootEntity);
        Drain(context.World);
        Assert.IsTrue(context.Overlays.TryGetOverlay(popup, out UiOverlaySnapshot snapshot));

        Assert.IsTrue(context.World.DisposeScope(pageScope));
        Drain(context.World);

        Assert.IsFalse(context.World.Scopes.IsAlive(snapshot.Scope));
        Assert.IsFalse(context.Overlays.TryGetOverlay(popup, out _));
        Assert.AreEqual(0, context.Overlays.OverlayCount);
    }

    private static bool IsDescendantOrSelf(UiWorld world, UiEntity ancestor, UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (current == ancestor)
                return true;
            if (!world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            current = node.Parent;
        }
        return false;
    }

    private static TestContext Create()
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiSize viewport = new(240, 120);
        UiInteractiveRuntime runtime = new(world, new DeterministicTextEngine(), viewport);
        UiScopeId applicationScope = world.CreateRootScope();
        UiScopeId windowScope = world.CreateScope(applicationScope);
        UiInputRootId inputRoot = runtime.Input.InputRoots.Register(windowScope);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        UiOverlayRuntime overlays = new(world, runtime, instantiator, windowScope);
        return new TestContext(clock, world, runtime, overlays, windowScope, inputRoot, instantiator);
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 24)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame, "Runtime did not settle to idle.");
    }

    private sealed record TestContext(
        DeterministicUiClock Clock,
        UiWorld World,
        UiInteractiveRuntime Runtime,
        UiOverlayRuntime Overlays,
        UiScopeId WindowScope,
        UiInputRootId InputRoot,
        BlueprintInstantiator Instantiator) : IDisposable
    {
        public BlueprintInstance Instantiate(UiNode node) =>
            Instantiator.Instantiate(Ui.Compile(node), WindowScope);

        public void Dispose()
        {
            Overlays.Dispose();
            Runtime.Dispose();
        }
    }
}
