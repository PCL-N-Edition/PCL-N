// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Drains platform events / state patches into <see cref="UiWorld.FrameBuffers"/>
/// for subsequent systems. Buffers are cleared at the start of the matching phase.
/// </summary>
public sealed class DrainQueuesSystem : IUiSystem
{
    public UiSystemPhase Phase { get; }

    public string Name { get; }

    public int LastEventCount { get; private set; }

    public int LastPatchCount { get; private set; }

    public DrainQueuesSystem(UiSystemPhase phase)
    {
        if (phase is not (UiSystemPhase.DrainPlatformEvents or UiSystemPhase.DrainStatePatches))
            throw new ArgumentOutOfRangeException(nameof(phase));
        Phase = phase;
        Name = phase == UiSystemPhase.DrainPlatformEvents
            ? "kernel.drain-events"
            : "kernel.drain-patches";
    }

    public static void RegisterDefaults(SystemPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.Register(new DrainQueuesSystem(UiSystemPhase.DrainPlatformEvents));
        pipeline.Register(new DrainQueuesSystem(UiSystemPhase.DrainStatePatches));
    }

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (Phase == UiSystemPhase.DrainPlatformEvents)
        {
            world.FrameBuffers.ClearPlatformEvents();
            LastEventCount = world.Events.Drain(world.FrameBuffers.PlatformEvents, world.Scopes);
            return;
        }

        world.FrameBuffers.ClearStatePatches();
        LastPatchCount = world.Patches.Drain(world.FrameBuffers.StatePatches, world.Scopes);
    }
}
