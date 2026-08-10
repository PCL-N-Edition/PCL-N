// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class EntityRegistryTests
{
    [TestMethod]
    public void Create_IsAlive_And_Destroy_BumpsGeneration()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        UiEntity entity = world.CreateEntity(scope);

        Assert.IsTrue(world.Entities.IsAlive(entity));
        Assert.AreEqual(1, world.Entities.AliveCount);

        world.DestroyEntity(entity);
        Assert.IsFalse(world.Entities.IsAlive(entity));
        Assert.AreEqual(0, world.Entities.AliveCount);

        UiEntity reused = world.CreateEntity(scope);
        Assert.AreEqual(entity.Index, reused.Index);
        Assert.AreNotEqual(entity.Generation, reused.Generation);
        Assert.IsFalse(world.Entities.IsAlive(entity));
        Assert.IsTrue(world.Entities.IsAlive(reused));
    }

    [TestMethod]
    public void StaleHandle_IsRejected()
    {
        EntityRegistry registry = new();
        ScopeRegistry scopes = new();
        UiScopeId scope = scopes.CreateRoot();
        UiEntity a = registry.Create(scope);
        Assert.IsTrue(registry.Destroy(a));
        Assert.IsFalse(registry.Destroy(a));
        Assert.IsFalse(registry.IsAlive(a));
    }
}
