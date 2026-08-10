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

        world.DisposeScope(scope);
        UiScopeId next = world.CreateRootScope();

        // Explicitly stale requestGeneration against a live scope handle.
        world.EnqueueStatePatch(new UiStatePatch(next, requestGeneration: next.Generation + 99, patchKind: 2));
        world.Update(force: true);
        Assert.AreEqual(0, world.FrameBuffers.StatePatches.Count);
    }

    [TestMethod]
    public void StatePatch_WithMatchingGeneration_IsAccepted()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        world.EnqueueStatePatch(new UiStatePatch(scope, scope.Generation, patchKind: 7, payload0: 42));

        world.Update(force: true);

        Assert.AreEqual(1, world.FrameBuffers.StatePatches.Count);
        Assert.AreEqual(7, world.FrameBuffers.StatePatches[0].PatchKind);
        Assert.AreEqual(42, world.FrameBuffers.StatePatches[0].Payload0);
    }
}
