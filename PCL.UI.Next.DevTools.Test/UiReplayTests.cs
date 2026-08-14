// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.DevTools.Test;

[TestClass]
public sealed class UiReplayTests
{
    [TestMethod]
    public void ReplayLog_BinaryRoundTripPreservesAllEntryKinds()
    {
        UiScopeId scope = new(4, 7);
        UiInputRootId inputRoot = new(2, 3);
        UiPlatformEvent platformEvent = new(
            scope,
            UiPlatformEventKind.PointerDown,
            new UiTimestamp(1.25d),
            1,
            2,
            3,
            4,
            inputRoot,
            5);
        UiStatePatch patch = new(scope, 7, 42, 8, 9, 10);
        UiReplayEntry[] entries =
        [
            UiReplayEntry.FromPlatformEvent(in platformEvent),
            UiReplayEntry.FromStatePatch(in patch),
            UiReplayEntry.ViewportChanged(new UiSize(800f, 600f)),
            UiReplayEntry.ResourceReady(12, 13),
            UiReplayEntry.ClockTick(new UiTimestamp(2d))
        ];
        UiReplayLog source = new(entries);
        using MemoryStream stream = new();

        source.Save(stream);
        stream.Position = 0;
        UiReplayLog loaded = UiReplayLog.Load(stream);

        Assert.AreEqual(UiReplayLog.CurrentVersion, loaded.Version);
        Assert.AreEqual(entries.Length, loaded.Entries.Length);
        Assert.AreEqual(5, loaded.Entries.Span[0].PlatformEvent.Payload4);
        Assert.AreEqual(10L, loaded.Entries.Span[1].StatePatch.PayloadLong);
        Assert.AreEqual(new UiSize(800f, 600f), loaded.Entries.Span[2].Viewport);
        Assert.AreEqual(13u, loaded.Entries.Span[3].ResourceGeneration);
        Assert.AreEqual(new UiTimestamp(2d), loaded.Entries.Span[4].Timestamp);
    }

    [TestMethod]
    public void Replay_ReproducesPointerCommandStatePatchAndFrameBoundary()
    {
        using ReplayContext source = CreateContext();
        UiReplayLog log;
        using (UiReplayRecorder recorder = new(source.World, source.Runtime))
        {
            UiStatePatch patch = new(source.Scope, source.Scope.Generation, 81, 5, 6, 7);
            source.World.EnqueueStatePatch(in patch);
            source.Runtime.Input.EnqueuePointer(
                source.InputRoot,
                UiPointerEventKind.Down,
                new UiPoint(10f, 10f),
                changedButton: UiPointerButton.Primary,
                buttons: UiPointerButtons.Primary);
            source.Runtime.Input.EnqueuePointer(
                source.InputRoot,
                UiPointerEventKind.Up,
                new UiPoint(10f, 10f),
                changedButton: UiPointerButton.Primary);
            source.Clock.Advance(0.016d);
            Assert.IsTrue(source.World.Update());
            log = recorder.Complete();
        }
        Assert.IsTrue(source.Runtime.Input.Commands.TryDequeue(out UiCommandInvocation sourceCommand));

        using ReplayContext target = CreateContext();
        UiReplayRunner runner = new(target.World, target.Clock, target.Runtime);
        int frames = runner.Replay(log);

        Assert.AreEqual(1, frames);
        Assert.IsTrue(target.Runtime.Input.Commands.TryDequeue(out UiCommandInvocation replayedCommand));
        Assert.AreEqual(sourceCommand.Command, replayedCommand.Command);
        Assert.AreEqual(UiCommandTrigger.Pointer, replayedCommand.Trigger);
        Assert.AreEqual(1, target.World.FrameBuffers.StatePatches.Count);
        Assert.AreEqual(81, target.World.FrameBuffers.StatePatches[0].PatchKind);
        Assert.AreEqual(0.016d, target.Clock.Now.Seconds, 0.000_001d);
    }

    [TestMethod]
    public void Replay_RequiresExplicitResourceHandlerAndRejectsOverflowedRecording()
    {
        UiReplayLog resourceLog = new([UiReplayEntry.ResourceReady(9, 2)]);
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiReplayRunner missingHandler = new(world, clock);
        Assert.ThrowsExactly<InvalidOperationException>(() => missingHandler.Replay(resourceLog));

        int resourceId = 0;
        uint generation = 0;
        UiReplayRunner runner = new(
            world,
            clock,
            resourceReady: (id, currentGeneration) =>
            {
                resourceId = id;
                generation = currentGeneration;
            });
        Assert.AreEqual(0, runner.Replay(resourceLog));
        Assert.AreEqual(9, resourceId);
        Assert.AreEqual(2u, generation);

        using UiReplayRecorder recorder = new(world, capacity: 1);
        UiScopeId scope = world.CreateRootScope();
        UiPlatformEvent first = new(scope, UiPlatformEventKind.ThemeChanged, clock.Now);
        UiPlatformEvent second = new(scope, UiPlatformEventKind.DpiChanged, clock.Now);
        world.EnqueuePlatformEvent(in first);
        world.EnqueuePlatformEvent(in second);
        Assert.IsTrue(recorder.IsOverflowed);
        Assert.ThrowsExactly<InvalidOperationException>(() => recorder.Complete());
    }

    [TestMethod]
    public void ReplayLog_RejectsInvalidMagic()
    {
        using MemoryStream stream = new([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.ThrowsExactly<InvalidDataException>(() => UiReplayLog.Load(stream));
    }

    private static ReplayContext CreateContext()
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(200f, 100f));
        UiScopeId scope = world.CreateRootScope();
        UiInputRootId inputRoot = runtime.Input.InputRoots.Register(scope);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        instantiator.Instantiate(
            Ui.Compile(
                Ui.Button("Replay")
                    .Command(new UiCommand(404))
                    .Width(UiLength.Pixels(100f))
                    .Height(UiLength.Pixels(40f))),
            scope);
        Drain(world);
        return new ReplayContext(clock, world, runtime, scope, inputRoot);
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 24)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame);
    }

    private sealed record ReplayContext(
        DeterministicUiClock Clock,
        UiWorld World,
        UiInteractiveRuntime Runtime,
        UiScopeId Scope,
        UiInputRootId InputRoot) : IDisposable
    {
        public void Dispose() => Runtime.Dispose();
    }
}

