// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class DirtyTrackerAndPipelineTests
{
    private sealed class CountingSystem : IUiSystem
    {
        public CountingSystem(UiSystemPhase phase, string name, List<string>? order = null)
        {
            Phase = phase;
            Name = name;
            _order = order;
        }

        private readonly List<string>? _order;

        public UiSystemPhase Phase { get; }
        public string Name { get; }
        public int Runs { get; private set; }

        public void Update(UiWorld world, in UiFrameContext frame)
        {
            Runs++;
            _order?.Add(Name);
        }
    }

    private sealed class RequestReactiveSystem : IUiSystem
    {
        public UiSystemPhase Phase => UiSystemPhase.BindingUpdate;
        public string Name => "request-reactive";

        public void Update(UiWorld world, in UiFrameContext frame) =>
            world.Scheduler.RequestReactiveFrame();
    }

    [TestMethod]
    public void DirtyTracker_MarkCollectClear()
    {
        // Skip default drain systems noise for this test.
        UiWorld world = new(new DeterministicUiClock(), registerDefaultDrainSystems: false);
        UiScopeId scope = world.CreateRootScope();
        UiEntity entity = world.CreateEntity(scope);

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
    public void Phases_RunInOrdinalOrder()
    {
        UiWorld world = new(new DeterministicUiClock(), registerDefaultDrainSystems: false);
        List<string> order = [];
        world.Systems.Register(new CountingSystem(UiSystemPhase.BackendCommit, "late", order));
        world.Systems.Register(new CountingSystem(UiSystemPhase.DrainPlatformEvents, "early", order));
        world.Systems.Register(new CountingSystem(UiSystemPhase.LayoutMeasure, "mid", order));

        world.Update(force: true);
        CollectionAssert.AreEqual(new[] { "early", "mid", "late" }, order);
    }

    [TestMethod]
    public void SamePhase_PreservesRegistrationOrder()
    {
        UiWorld world = new(new DeterministicUiClock(), registerDefaultDrainSystems: false);
        List<string> order = [];
        // Names deliberately reverse-alphabetical so name-sort would reverse registration order.
        world.Systems.Register(new CountingSystem(UiSystemPhase.BindingUpdate, "z-first", order));
        world.Systems.Register(new CountingSystem(UiSystemPhase.BindingUpdate, "a-second", order));
        world.Systems.Register(new CountingSystem(UiSystemPhase.BindingUpdate, "m-third", order));

        world.Update(force: true);
        CollectionAssert.AreEqual(new[] { "z-first", "a-second", "m-third" }, order);
    }

    [TestMethod]
    public void Scheduler_IdleWithoutWork()
    {
        UiWorld world = new(new DeterministicUiClock(), registerDefaultDrainSystems: false);
        UiScopeId scope = world.CreateRootScope();
        world.CreateEntity(scope);
        Assert.IsTrue(world.Scheduler.NeedsFrame);
        Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame);
        Assert.IsFalse(world.Update());
    }

    [TestMethod]
    public void Scheduler_ContinuousKeepsTicking()
    {
        UiWorld world = new(new DeterministicUiClock(), registerDefaultDrainSystems: false);
        world.Scheduler.RequestContinuousFrame(UiContinuousReason.Animation);
        Assert.IsTrue(world.Update());
        Assert.IsTrue(world.Scheduler.NeedsFrame);
        world.Scheduler.ReleaseContinuousFrame(UiContinuousReason.Animation);
        world.Update();
        Assert.IsFalse(world.Scheduler.NeedsFrame);
    }

    [TestMethod]
    public void Scheduler_ContinuousLeasesAreReferenceCountedPerOwner()
    {
        UiFrameScheduler scheduler = new();
        IDisposable first = scheduler.AcquireContinuousFrame(UiContinuousReason.OverlayTimer);
        IDisposable second = scheduler.AcquireContinuousFrame(UiContinuousReason.OverlayTimer);
        Assert.IsTrue(scheduler.HasContinuous);

        first.Dispose();
        Assert.IsTrue(scheduler.HasContinuous);
        second.Dispose();
        Assert.IsFalse(scheduler.HasContinuous);
    }

    [TestMethod]
    public void Scheduler_MidFrameRequest_SurvivesToNextFrame()
    {
        UiWorld world = new(new DeterministicUiClock(), registerDefaultDrainSystems: false);
        world.Systems.Register(new RequestReactiveSystem());

        // Force frame N; system requests reactive for N+1.
        Assert.IsTrue(world.Update(force: true));
        Assert.IsTrue(world.Scheduler.NeedsFrame, "mid-frame RequestReactiveFrame must schedule N+1");

        Assert.IsTrue(world.Update(), "frame N+1 must run");
        // RequestReactiveSystem runs again → still needs another frame.
        Assert.IsTrue(world.Scheduler.NeedsFrame);
    }
}
