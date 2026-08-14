// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class DiagnosticsTests
{
    [TestMethod]
    public void DiagnosticJournal_IsBoundedAndReadersReportDropsIndependently()
    {
        UiDiagnosticsOptions options = new(
            EventCapacity: 3,
            TimelineCapacity: 1,
            MinimumLevel: UiDiagnosticLevel.Info,
            Features: UiDiagnosticFeatures.Lifecycle);
        UiWorld world = new(new DeterministicUiClock(), diagnosticsOptions: options);
        UiDiagnosticEventReader slow = world.Diagnostics.Events.CreateReader(UiDiagnosticReaderStart.NextPublished);
        UiScopeId scope = world.CreateRootScope();
        for (int i = 0; i < 5; i++)
            world.CreateEntity(scope);
        UiDiagnosticEventReader current = world.Diagnostics.Events.CreateReader();
        UiDiagnosticEventReader beginning = world.Diagnostics.Events.CreateReader(
            UiDiagnosticReaderStart.Beginning);

        List<UiDiagnosticEvent> slowEvents = [];
        List<UiDiagnosticEvent> currentEvents = [];
        List<UiDiagnosticEvent> beginningEvents = [];
        slow.Drain(slowEvents);
        current.Drain(currentEvents);
        beginning.Drain(beginningEvents);

        Assert.AreEqual(3, slowEvents.Count);
        Assert.AreEqual(3L, slow.DroppedCount);
        Assert.AreEqual(3, currentEvents.Count);
        Assert.AreEqual(0L, current.DroppedCount);
        Assert.AreEqual(3, beginningEvents.Count);
        Assert.AreEqual(3L, beginning.DroppedCount);
        Assert.AreEqual(slowEvents[0].Sequence, currentEvents[0].Sequence);
        Assert.AreEqual(slowEvents[0].Sequence, beginningEvents[0].Sequence);
    }

    [TestMethod]
    public void DirtyTrace_PreservesAncestorPropagationChain()
    {
        UiWorld world = new(
            new DeterministicUiClock(),
            diagnosticsOptions: UiDiagnosticsOptions.Developer);
        UiScopeId scope = world.CreateRootScope();
        UiEntity root = world.CreateEntity(scope);
        UiEntity parent = world.CreateEntity(scope);
        UiEntity leaf = world.CreateEntity(scope);
        world.AttachChild(root, parent);
        world.AttachChild(parent, leaf);
        world.Dirty.ClearEverything();
        UiDiagnosticEventReader reader = world.Diagnostics.Events.CreateReader(UiDiagnosticReaderStart.NextPublished);

        LayoutInvalidation.MarkMeasure(world, leaf, requestFrame: false);

        List<UiDiagnosticEvent> events = [];
        reader.Drain(events);
        UiDiagnosticEvent[] dirty = events
            .Where(static item => item.Kind == UiDiagnosticEventKind.DirtyMarked)
            .ToArray();
        Assert.AreEqual(3, dirty.Length);
        Assert.AreEqual(leaf, dirty[0].Entity);
        Assert.AreEqual(UiEntity.None, dirty[0].RelatedEntity);
        Assert.AreEqual(parent, dirty[1].Entity);
        Assert.AreEqual(leaf, dirty[1].RelatedEntity);
        Assert.AreEqual(root, dirty[2].Entity);
        Assert.AreEqual(parent, dirty[2].RelatedEntity);
    }

    [TestMethod]
    public void FrameTimeline_CapturesOrderedSystemTimingsAndBoundedHistory()
    {
        UiDiagnosticsOptions options = UiDiagnosticsOptions.Developer with { TimelineCapacity = 2 };
        DeterministicUiClock clock = new();
        UiWorld world = new(clock, diagnosticsOptions: options);
        ProbeSystem probe = new();
        world.Systems.Register(probe);
        UiScopeId scope = world.CreateRootScope();
        world.CreateEntity(scope);

        for (int i = 0; i < 3; i++)
        {
            clock.Advance(0.016d);
            Assert.IsTrue(world.Update(force: true));
        }

        List<UiFrameTimeline> timelines = [];
        world.Diagnostics.CopyTimelinesTo(timelines);
        Assert.AreEqual(2, timelines.Count);
        Assert.AreEqual(2L, timelines[0].FrameIndex);
        Assert.AreEqual(3L, timelines[1].FrameIndex);
        Assert.AreEqual(1, timelines[1].EntityCount);
        Assert.IsGreaterThanOrEqualTo(0L, timelines[1].AllocatedBytes);
        bool foundProbe = false;
        ReadOnlySpan<UiSystemTiming> systems = timelines[1].Systems.Span;
        for (int i = 0; i < systems.Length; i++)
            foundProbe |= systems[i].SystemName == "test.probe";
        Assert.IsTrue(foundProbe);
        Assert.AreEqual(3, probe.UpdateCount);
    }

    private sealed class ProbeSystem : IUiSystem
    {
        public UiSystemPhase Phase => UiSystemPhase.BindingUpdate;
        public string Name => "test.probe";
        public int UpdateCount { get; private set; }
        public void Update(UiWorld world, in UiFrameContext frame)
        {
            _ = world;
            _ = frame;
            UpdateCount++;
        }
    }
}
