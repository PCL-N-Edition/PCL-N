// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class GenerationFuzzTests
{
    private struct Tag
    {
        public int N;
    }

    [TestMethod]
    public void Fuzz_CreateDestroy_NoStaleHandleAlive()
    {
        var rng = new Random(42);
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        List<UiEntity> live = [];
        List<UiEntity> dead = [];

        for (int i = 0; i < 2000; i++)
        {
            int op = rng.Next(0, 5);
            switch (op)
            {
                case 0:
                case 1:
                {
                    UiEntity e = world.CreateEntity(scope);
                    world.Components.Pool<Tag>().Set(e, new Tag { N = i });
                    live.Add(e);
                    break;
                }
                case 2 when live.Count > 0:
                {
                    int idx = rng.Next(live.Count);
                    UiEntity e = live[idx];
                    world.DestroyEntity(e);
                    // Subtree destroy may kill descendants still tracked as live.
                    for (int j = live.Count - 1; j >= 0; j--)
                    {
                        if (world.Entities.IsAlive(live[j]))
                            continue;
                        dead.Add(live[j]);
                        live.RemoveAt(j);
                    }

                    break;
                }
                case 3 when live.Count >= 2:
                {
                    UiEntity parent = live[rng.Next(live.Count)];
                    UiEntity child = live[rng.Next(live.Count)];
                    if (parent != child)
                    {
                        try { world.AttachChild(parent, child); }
                        catch (InvalidOperationException) { /* cycles / self */ }
                    }

                    break;
                }
                case 4 when live.Count > 0:
                {
                    UiEntity e = live[rng.Next(live.Count)];
                    world.Dirty.Mark(e, UiDirtyFlags.Render | UiDirtyFlags.Transform);
                    break;
                }
            }

            foreach (UiEntity e in live)
                Assert.IsTrue(world.Entities.IsAlive(e), "live handle must stay valid");
            foreach (UiEntity e in dead)
                Assert.IsFalse(world.Entities.IsAlive(e), "destroyed handle must stay dead");
        }
    }

    [TestMethod]
    public void Host_TryStart_BlockedWhileUnimplemented()
    {
        var host = NextUiRenderRuntime.CreateHost(new DeterministicUiClock());
        Assert.IsFalse(host.TryStart(NextUiRenderMode.Ecs, out string reason));
        StringAssert.Contains(reason, "not implemented");
        Assert.IsFalse(host.IsRunning);
    }

    [TestMethod]
    public void Host_CreateWorldForTests_Works()
    {
        var host = new UiRuntimeHost(new DeterministicUiClock());
        UiWorld world = host.CreateWorldForTests();
        UiScopeId scope = world.CreateRootScope();
        Assert.IsTrue(world.Entities.IsAlive(world.CreateEntity(scope)));
    }
}
