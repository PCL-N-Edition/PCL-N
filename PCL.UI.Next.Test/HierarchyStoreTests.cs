// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class HierarchyStoreTests
{
    [TestMethod]
    public void AttachChild_LinksAndDepth()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity root = world.CreateEntity(scope);
        UiEntity child = world.CreateEntity(scope);
        UiEntity grand = world.CreateEntity(scope);

        world.AttachChild(root, child);
        world.AttachChild(child, grand);

        Assert.AreEqual(root, world.Hierarchy.GetNode(child).Parent);
        Assert.AreEqual(child, world.Hierarchy.GetNode(grand).Parent);
        Assert.AreEqual(0, world.Hierarchy.GetNode(root).Depth);
        Assert.AreEqual(1, world.Hierarchy.GetNode(child).Depth);
        Assert.AreEqual(2, world.Hierarchy.GetNode(grand).Depth);

        List<UiEntity> children = [];
        world.Hierarchy.EnumerateChildren(root, children);
        CollectionAssert.AreEqual(new[] { child }, children);

        List<UiEntity> ancestors = [];
        world.Hierarchy.EnumerateAncestors(grand, ancestors);
        CollectionAssert.AreEqual(new[] { child, root }, ancestors);
    }

    [TestMethod]
    public void DestroySubtree_RemovesAll()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity root = world.CreateEntity(scope);
        UiEntity a = world.CreateEntity(scope);
        UiEntity b = world.CreateEntity(scope);
        world.AttachChild(root, a);
        world.AttachChild(root, b);

        world.DestroyEntity(root);
        Assert.IsFalse(world.Entities.IsAlive(root));
        Assert.IsFalse(world.Entities.IsAlive(a));
        Assert.IsFalse(world.Entities.IsAlive(b));
        Assert.AreEqual(0, world.Entities.AliveCount);
    }
}
