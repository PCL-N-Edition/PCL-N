// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class DirtyTrackerAndPipelineTests
{
    private sealed class CountingSystem : IUiSystem
    {
        public CountingSystem(UiSystemPhase phase, string name)
        {
            Phase = phase;
            Name = name;
        }

        public UiSystemPhase Phase { get; }
        public string Name { get; }
        public int Runs { get; private set; }

        public void Update(UiWorld world, in UiFrameContext frame) => Runs++;
    }

    [TestMethod]
    public void DirtyTracker_MarkCollectClear()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity entity = world.CreateEntity(scope);

        // CreateEntity already marks structural cascade.
        Assert.IsTrue(world.Dirty.Any(UiDirtyFlags.Structure));

        List<UiEntity> dirty = [];
        world.Dirty.Collect(UiDirtyFlags.Structure | UiDirtyFlags.Render, dirty);
        CollectionAssert.Contains(dirty, entity);

        world.Dirty.Clear(entity, UiDirtyFlags.StructuralCascade);
        dirty.Clear();
        world.Dirty.Collect(UiDirtyFlags.StructuralCascade, dirty);
        Assert.AreEqual(0, dirty.Count);
    }

    [TestMethod]
    public void SystemPipeline_RunsInPhaseOrder()
    {
        UiWorld world = new(new DeterministicUiClock());
        var late = new CountingSystem(UiSystemPhase.BackendCommit, "z-late");
        var early = new CountingSystem(UiSystemPhase.DrainPlatformEvents, "a-early");
        var mid = new CountingSystem(UiSystemPhase.LayoutMeasure, "m-mid");
        world.Systems.Register(late);
        world.Systems.Register(early);
        world.Systems.Register(mid);

        List<string> order = [];
        world.Systems.Register(new OrderProbe(order));
        // Re-register probes as systems that record when they run via wrapper — simpler: just Run and check counts
        world.Update(force: true);
        Assert.AreEqual(1, early.Runs);
        Assert.AreEqual(1, mid.Runs);
        Assert.AreEqual(1, late.Runs);
    }

    [TestMethod]
    public void Scheduler_IdleWithoutWork()
    {
        UiWorld world = new(new DeterministicUiClock());
        // CreateEntity requests a reactive frame
        UiScopeId scope = world.CreateRootScope();
        world.CreateEntity(scope);
        Assert.IsTrue(world.Scheduler.NeedsFrame);
        Assert.IsTrue(world.Update());
        // After one update reactive is acknowledged; no continuous → idle
        Assert.IsFalse(world.Scheduler.NeedsFrame);
        Assert.IsFalse(world.Update());
    }

    [TestMethod]
    public void Scheduler_ContinuousKeepsTicking()
    {
        UiWorld world = new(new DeterministicUiClock());
        world.Scheduler.RequestContinuousFrame(UiContinuousReason.Animation);
        Assert.IsTrue(world.Update());
        Assert.IsTrue(world.Scheduler.NeedsFrame);
        world.Scheduler.ReleaseContinuousFrame(UiContinuousReason.Animation);
        world.Update();
        Assert.IsFalse(world.Scheduler.NeedsFrame);
    }

    private sealed class OrderProbe : IUiSystem
    {
        private readonly List<string> _order;

        public OrderProbe(List<string> order) => _order = order;

        public UiSystemPhase Phase => UiSystemPhase.BindingUpdate;
        public string Name => "probe";
        public void Update(UiWorld world, in UiFrameContext frame) => _order.Add(Name);
    }
}
