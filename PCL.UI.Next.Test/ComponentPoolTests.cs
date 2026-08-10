// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class ComponentPoolTests
{
    private struct Sample
    {
        public int Value;
    }

    [TestMethod]
    public void Add_Get_Remove_And_SwapRemove()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity a = world.CreateEntity(scope);
        UiEntity b = world.CreateEntity(scope);
        UiEntity c = world.CreateEntity(scope);

        world.Add(a, new Sample { Value = 1 });
        world.Add(b, new Sample { Value = 2 });
        world.Add(c, new Sample { Value = 3 });
        Assert.AreEqual(3, world.Components.Pool<Sample>().Count);
        Assert.AreEqual(2, world.Components.Get<Sample>(b).Value);

        Assert.IsTrue(world.Remove<Sample>(a));
        Assert.IsFalse(world.Components.Has<Sample>(a));
        Assert.AreEqual(2, world.Components.Pool<Sample>().Count);
        Assert.AreEqual(2, world.Components.Get<Sample>(b).Value);
        Assert.AreEqual(3, world.Components.Get<Sample>(c).Value);

        world.Set(b, new Sample { Value = 20 });
        Assert.AreEqual(20, world.Components.Get<Sample>(b).Value);
    }

    [TestMethod]
    public void DestroyEntity_RemovesComponents()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity entity = world.CreateEntity(scope);
        world.Add(entity, new Sample { Value = 42 });
        world.DestroyEntity(entity);
        Assert.IsFalse(world.Components.Has<Sample>(entity));
    }

    [TestMethod]
    public void StaleEntity_CannotAddComponent()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity stale = world.CreateEntity(scope);
        world.DestroyEntity(stale);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            world.Add(stale, new Sample { Value = 1 }));
    }

    [TestMethod]
    public void StaleEntity_CannotSetComponent()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity stale = world.CreateEntity(scope);
        world.DestroyEntity(stale);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            world.Set(stale, new Sample { Value = 1 }));
    }

    [TestMethod]
    public void SlotReuse_DoesNotCreateOrphanDenseEntry()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity a = world.CreateEntity(scope);
        world.Add(a, new Sample { Value = 1 });
        world.DestroyEntity(a);

        UiEntity b = world.CreateEntity(scope);
        Assert.AreEqual(a.Index, b.Index);
        Assert.AreNotEqual(a.Generation, b.Generation);

        // Stale A must not mutate storage.
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            world.Add(a, new Sample { Value = 99 }));

        world.Add(b, new Sample { Value = 2 });
        Assert.AreEqual(1, world.Components.Pool<Sample>().Count);
        Assert.IsTrue(world.Components.Has<Sample>(b));
        Assert.IsFalse(world.Components.Has<Sample>(a));
        Assert.AreEqual(2, world.Components.Get<Sample>(b).Value);

        // Dense must only contain the live handle.
        ReadOnlySpan<UiEntity> entities = world.Components.Pool<Sample>().Entities;
        Assert.AreEqual(1, entities.Length);
        Assert.AreEqual(b, entities[0]);
    }
}
