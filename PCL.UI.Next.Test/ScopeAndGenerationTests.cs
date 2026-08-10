// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class ScopeAndGenerationTests
{
    [TestMethod]
    public void DisposeScope_DestroysOwnedEntities_AndInvalidatesScope()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId root = world.CreateRootScope();
        UiScopeId page = world.CreateScope(root);
        UiEntity entity = world.CreateEntity(page);

        Assert.IsTrue(world.DisposeScope(page));
        Assert.IsFalse(world.Scopes.IsAlive(page));
        Assert.IsFalse(world.Entities.IsAlive(entity));
        Assert.IsTrue(world.Scopes.IsAlive(root));
    }

    [TestMethod]
    public void StatePatch_WithStaleGeneration_IsDropped()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        uint gen = scope.Generation;

        world.DisposeScope(scope);
        UiScopeId next = world.CreateRootScope();
        // Same index possible after free-list reuse with bumped generation.
        world.EnqueueStatePatch(new UiStatePatch(next, requestGeneration: gen, patchKind: 1));

        var drain = new DrainQueuesSystem(UiSystemPhase.DrainStatePatches);
        world.Systems.Register(drain);
        world.Scheduler.RequestReactiveFrame();
        Assert.IsTrue(world.Update());

        // requestGeneration != live scope generation → dropped
        if (next.Index == scope.Index && next.Generation != gen)
            Assert.AreEqual(0, drain.LastPatchCount);
        else
        {
            // If free list did not reuse the same index, patch is accepted only when gens match.
            // Force explicit stale case:
            world.EnqueueStatePatch(new UiStatePatch(next, requestGeneration: next.Generation + 99, patchKind: 2));
            world.Scheduler.RequestReactiveFrame();
            world.Update();
            Assert.AreEqual(0, drain.LastPatchCount);
        }
    }

    [TestMethod]
    public void StatePatch_WithMatchingGeneration_IsAccepted()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        world.EnqueueStatePatch(new UiStatePatch(scope, scope.Generation, patchKind: 7, payload0: 42));

        var drain = new DrainQueuesSystem(UiSystemPhase.DrainStatePatches);
        world.Systems.Register(drain);
        world.Update(force: true);

        Assert.AreEqual(1, drain.LastPatchCount);
        Assert.AreEqual(7, drain.LastPatches[0].PatchKind);
        Assert.AreEqual(42, drain.LastPatches[0].Payload0);
    }
}
