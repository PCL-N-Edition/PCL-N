// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class ScrollVirtualizationTests
{
    [TestMethod]
    public void WheelScroll_UpdatesContentTransformWithoutLayoutInvalidation()
    {
        using TestContext context = CreateScrollable();
        UiEntity host = context.Instance.RootEntity;
        UiEntity content = context.Instance.EntityAt(1);

        context.Runtime.Input.EnqueueWheel(
            context.InputRoot,
            new UiPoint(10f, 10f),
            new UiPoint(0f, -1f));
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(48f, context.Runtime.Scroll.GetState(host).Offset, 0.01f);
        Assert.AreEqual(48f, context.World.Components.Get<ScrollContentTransform>(content).Y, 0.01f);
        Assert.AreEqual(0, context.Runtime.Layout.LastMeasureCount);
        Assert.AreEqual(UiDirtyFlags.None,
            context.World.Dirty.GetFlags(host) & (UiDirtyFlags.LayoutMeasure | UiDirtyFlags.LayoutArrange));
    }

    [TestMethod]
    public void ScrollClip_RejectsHitOutsideViewport()
    {
        using TestContext context = CreateScrollable(buttons: true);
        UiEntity secondButton = context.Instance.EntityAt(4);

        Assert.AreEqual(UiEntity.None, context.Runtime.Input.HitTest.HitTest(new UiPoint(20f, 120f), context.InputRoot));
        Assert.AreEqual(secondButton, context.Runtime.Input.HitTest.HitTest(new UiPoint(20f, 90f), context.InputRoot));
    }

    [TestMethod]
    public void FlingAfterLongIdle_DoesNotConsumeIdleTime()
    {
        using TestContext context = CreateScrollable();
        UiEntity host = context.Instance.RootEntity;
        context.Clock.Advance(30d);

        context.Runtime.Scroll.Fling(host, 240f);
        Assert.IsTrue(context.World.Update());
        Assert.AreEqual(0f, context.Runtime.Scroll.GetState(host).Offset, 0.001f);
        Assert.AreEqual(UiScrollMotionKind.Inertia, context.Runtime.Scroll.GetState(host).Motion);

        context.Clock.Advance(0.016d);
        Assert.IsTrue(context.World.Update());
        Assert.IsGreaterThan(0f, context.Runtime.Scroll.GetState(host).Offset);
    }

    [TestMethod]
    public void Inertia_ReleasesContinuousSchedulingWhenSettled()
    {
        using TestContext context = CreateScrollable();
        context.Runtime.Scroll.Fling(context.Instance.RootEntity, 180f);

        int guard = 0;
        while (context.World.Scheduler.NeedsFrame && guard++ < 1_000)
        {
            context.Clock.Advance(0.016d);
            Assert.IsTrue(context.World.Update());
        }

        Assert.IsLessThan(1_000, guard);
        Assert.AreEqual(UiScrollMotionKind.Idle, context.Runtime.Scroll.GetState(context.Instance.RootEntity).Motion);
        Assert.IsFalse(context.World.Scheduler.HasContinuous);
    }

    [TestMethod]
    public void VariableExtentIndex_UpdatesAndFindsOffsetsInLogarithmicIndex()
    {
        VariableExtentIndex index = new(100_000, 20f);
        Assert.AreEqual(2_000_000f, index.TotalExtent, 0.01f);
        Assert.AreEqual(50_000, index.FindIndexAtOffset(1_000_000f));

        Assert.IsTrue(index.SetMeasuredExtent(10, 50f));
        Assert.AreEqual(250f, index.GetOffset(11), 0.01f);
        Assert.AreEqual(10, index.FindIndexAtOffset(205f));
        Assert.IsTrue(index.IsMeasured(10));
    }

    private static TestContext CreateScrollable(bool buttons = false)
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiInteractiveRuntime runtime = new(world, new DeterministicTextEngine(), new UiSize(200f, 100f));
        UiScopeId applicationScope = world.CreateRootScope();
        UiScopeId windowScope = world.CreateScope(applicationScope);
        UiInputRootId inputRoot = runtime.Input.InputRoots.Register(windowScope);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        UiNode[] children = buttons
            ? [Ui.Button("One").Height(UiLength.Pixels(80f)), Ui.Button("Two").Height(UiLength.Pixels(80f)), Ui.Button("Three").Height(UiLength.Pixels(80f))]
            : [Ui.Container().Height(UiLength.Pixels(80f)), Ui.Container().Height(UiLength.Pixels(80f)), Ui.Container().Height(UiLength.Pixels(80f))];
        BlueprintInstance instance = instantiator.Instantiate(
            Ui.Compile(Ui.Scroll(Ui.Column(children))),
            windowScope);
        Drain(world);
        return new TestContext(clock, world, runtime, inputRoot, instance);
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 16)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame, "Runtime did not settle to idle.");
    }

    private sealed record TestContext(
        DeterministicUiClock Clock,
        UiWorld World,
        UiInteractiveRuntime Runtime,
        UiInputRootId InputRoot,
        BlueprintInstance Instance) : IDisposable
    {
        public void Dispose() => Runtime.Dispose();
    }
}
