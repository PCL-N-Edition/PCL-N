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

        ComponentPool<Sample> pool = world.Components.Pool<Sample>();
        pool.Add(a, new Sample { Value = 1 });
        pool.Add(b, new Sample { Value = 2 });
        pool.Add(c, new Sample { Value = 3 });
        Assert.AreEqual(3, pool.Count);
        Assert.AreEqual(2, pool.Get(b).Value);

        Assert.IsTrue(pool.Remove(a));
        Assert.IsFalse(pool.Has(a));
        Assert.AreEqual(2, pool.Count);
        Assert.AreEqual(2, pool.Get(b).Value);
        Assert.AreEqual(3, pool.Get(c).Value);

        pool.Set(b, new Sample { Value = 20 });
        Assert.AreEqual(20, pool.Get(b).Value);
    }

    [TestMethod]
    public void DestroyEntity_RemovesComponents()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity entity = world.CreateEntity(scope);
        world.Components.Pool<Sample>().Add(entity, new Sample { Value = 42 });
        world.DestroyEntity(entity);
        Assert.IsFalse(world.Components.Pool<Sample>().Has(entity));
    }
}
