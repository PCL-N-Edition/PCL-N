// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.DevTools.Test;

[TestClass]
public sealed class UiInspectorTests
{
    [TestMethod]
    public void Inspector_ExposesEntityLayoutRenderInteractionAnimationAndDirtyTrace()
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock, diagnosticsOptions: UiDiagnosticsOptions.Developer);
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(240f, 120f));
        UiScopeId application = world.CreateRootScope();
        UiScopeId window = world.CreateScope(application);
        UiInputRootId inputRoot = runtime.Input.InputRoots.Register(window);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        BlueprintInstance live = instantiator.Instantiate(
            Ui.Compile(
                Ui.Column(
                    Ui.Button("Inspect")
                        .Width(UiLength.Pixels(100f))
                        .Height(UiLength.Pixels(40f)))),
            window);
        HeadlessUiBackend backend = new();
        using UiRenderingRuntime rendering = new(
            world,
            backend,
            runtime.TextCache,
            window,
            new UiSize(240f, 120f),
            input: runtime.Input);
        Drain(world);
        UiEntity button = live.EntityAt(1);
        UiInspector inspector = new(world, runtime, rendering);

        Assert.IsTrue(inspector.TryInspectEntity(button, out UiEntityInspection entity));
        Assert.AreEqual(live.RootEntity, entity.Parent);
        CollectionAssert.Contains(entity.Components.ToArray(), nameof(LayoutStyle));
        CollectionAssert.Contains(entity.Components.ToArray(), nameof(FocusableComponent));

        Assert.IsTrue(inspector.TryInspectLayout(button, out UiLayoutInspection layout));
        Assert.AreEqual(100f, layout.LayoutRect!.Value.Width, 0.01f);
        Assert.IsNotNull(layout.LastMeasureConstraint);
        Assert.IsTrue(inspector.TryInspectRender(button, out UiRenderNodeSnapshot render));
        Assert.AreEqual(button, render.Owner);

        Assert.IsTrue(runtime.Input.Focus.Focus(button, clock.Now));
        runtime.Input.EnqueuePointer(inputRoot, UiPointerEventKind.Move, new UiPoint(10f, 10f));
        Assert.IsTrue(world.Update());
        Assert.IsTrue(inspector.TryInspectInteraction(button, out UiInteractionInspection interaction));
        Assert.AreEqual(button, interaction.Focused);
        Assert.AreEqual(button, interaction.Hovered);
        Assert.AreEqual(button, interaction.BubbleRoute[0]);

        runtime.Animation.Retarget(
            button,
            UiAnimationProperty.Opacity,
            0.25f,
            new UiAnimationSpec(UiMotion.Standard));
        List<UiAnimationSnapshot> animations = [];
        Assert.AreEqual(1, inspector.CopyAnimations(button, animations));
        Assert.AreEqual(0.25f, animations[0].Target, 0.001f);

        LayoutInvalidation.MarkMeasure(world, button, requestFrame: false);
        List<UiDiagnosticEvent> dirtyTrace = [];
        Assert.IsGreaterThan(0, inspector.CopyDirtyTrace(button, dirtyTrace));
        Assert.IsTrue(dirtyTrace.All(static item => item.Kind == UiDiagnosticEventKind.DirtyMarked));

        List<UiFrameTimeline> timelines = [];
        inspector.CopyTimelinesTo(timelines);
        Assert.IsGreaterThan(0, timelines.Count);
    }

    [TestMethod]
    public void Inspector_ExposesVirtualizationSnapshot()
    {
        UiWorld world = new(new DeterministicUiClock());
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(200f, 100f));
        UiScopeId scope = world.CreateRootScope();
        runtime.Input.InputRoots.Register(scope);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        UiEntity host = instantiator.Instantiate(
            Ui.Compile(Ui.VirtualList(20f, 2, 2)),
            scope).RootEntity;
        Drain(world);
        using UiVirtualListRegistration registration = runtime.Virtualization.Register(
            host,
            new ItemSource(100_000),
            Ui.Compile(Ui.Container().Height(UiLength.Pixels(20f))));
        Drain(world);
        UiInspector inspector = new(world, runtime);

        Assert.IsTrue(inspector.TryInspectVirtualization(host, out UiVirtualizationSnapshot snapshot));
        Assert.AreEqual(100_000, snapshot.ItemCount);
        Assert.IsLessThan(100, snapshot.RealizedCount);
    }

    [TestMethod]
    public void DirtyTrace_OpenedAfterJournalWrap_ReportsDroppedHistory()
    {
        UiDiagnosticsOptions options = UiDiagnosticsOptions.Developer with
        {
            EventCapacity = 4,
            TimelineCapacity = 1
        };
        UiWorld world = new(new DeterministicUiClock(), diagnosticsOptions: options);
        UiScopeId scope = world.CreateRootScope();
        UiEntity entity = world.CreateEntity(scope);
        world.Dirty.ClearEverything();
        for (int i = 0; i < 10; i++)
        {
            world.Dirty.Mark(entity, UiDirtyFlags.LayoutMeasure);
            world.Dirty.Clear(entity, UiDirtyFlags.LayoutMeasure);
        }
        UiInspector inspector = new(world);

        List<UiDiagnosticEvent> events = [];
        int count = inspector.CopyDirtyTrace(entity, events, out long droppedCount);

        Assert.AreEqual(4, count);
        Assert.IsGreaterThan(0L, droppedCount);
        Assert.IsTrue(events.All(static item => item.Kind == UiDiagnosticEventKind.DirtyMarked));
    }

    [TestMethod]
    public void MotionTrace_IsBoundedAndIncludesSettledSample()
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(100f, 100f));
        UiScopeId scope = world.CreateRootScope();
        UiEntity entity = world.CreateEntity(scope);
        world.Set(entity, ResolvedStyle.Default);
        using UiMotionTraceSession trace = new(world, runtime.Animation, capacity: 2);
        Drain(world);

        runtime.Animation.Retarget(
            entity,
            UiAnimationProperty.Opacity,
            0f,
            new UiAnimationSpec(UiMotion.FastFade));
        for (int i = 0; i < 20 && world.Scheduler.NeedsFrame; i++)
        {
            clock.Advance(0.025d);
            Assert.IsTrue(world.Update());
        }

        List<UiMotionTraceSample> samples = [];
        trace.CopySamplesTo(samples);
        Assert.AreEqual(2, samples.Count);
        Assert.AreEqual(0f, samples[^1].Animation.Current, 0.001f);
        Assert.IsFalse(samples[^1].Animation.IsActive);
    }

    [TestMethod]
    public void MotionTrace_ReportsAnimationJournalOverflow()
    {
        UiWorld world = new(new DeterministicUiClock());
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(100f, 100f));
        UiScopeId scope = world.CreateRootScope();
        const int overflow = 64;
        int count = UiAnimationEventJournal.DefaultCapacity + overflow;
        UiEntity[] entities = new UiEntity[count];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(scope);
            world.Set(entities[i], ResolvedStyle.Default);
        }
        Drain(world);
        using UiMotionTraceSession trace = new(
            world,
            runtime.Animation,
            capacity: UiAnimationEventJournal.DefaultCapacity + 128);

        for (int i = 0; i < entities.Length; i++)
        {
            runtime.Animation.Retarget(
                entities[i],
                UiAnimationProperty.Opacity,
                0.5f,
                new UiAnimationSpec(UiMotion.Instant));
        }
        Assert.IsTrue(world.Update());

        Assert.AreEqual(overflow, trace.DroppedAnimationEventCount);
        Assert.AreEqual(0L, trace.OverwrittenSampleCount);
        Assert.IsFalse(trace.IsComplete);
    }

    [TestMethod]
    public void MotionTrace_ReportsSampleRingOverwrite()
    {
        UiWorld world = new(new DeterministicUiClock());
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(100f, 100f));
        UiScopeId scope = world.CreateRootScope();
        UiEntity[] entities = new UiEntity[3];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(scope);
            world.Set(entities[i], ResolvedStyle.Default);
        }
        Drain(world);
        using UiMotionTraceSession trace = new(world, runtime.Animation, capacity: 2);

        for (int i = 0; i < entities.Length; i++)
        {
            runtime.Animation.Retarget(
                entities[i],
                UiAnimationProperty.Opacity,
                0.5f,
                new UiAnimationSpec(UiMotion.Instant));
        }
        Assert.IsTrue(world.Update());

        Assert.AreEqual(2, trace.Count);
        Assert.AreEqual(0L, trace.DroppedAnimationEventCount);
        Assert.AreEqual(1L, trace.OverwrittenSampleCount);
        Assert.IsFalse(trace.IsComplete);
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 24)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame);
    }

    private sealed class ItemSource(int count) : IUiVirtualItemSource
    {
        public int Count { get; } = count;
        public ulong Version => 1;
        public long GetKey(int index) => index;
        public void BindItem(int index, PresentationStore presentation) =>
            presentation.Set(1, index);
        public bool TryGetIndex(long key, out int index)
        {
            index = (int)key;
            return key >= 0 && key < Count;
        }
    }
}
