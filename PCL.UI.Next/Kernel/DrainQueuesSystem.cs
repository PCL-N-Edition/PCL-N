// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Phase-1 built-in system that drains platform events and state patches into
/// temporary lists so generation filtering is exercised every frame.
/// Higher phases will replace this with real input / binding systems.
/// </summary>
public sealed class DrainQueuesSystem : IUiSystem
{
    private readonly List<UiPlatformEvent> _events = [];
    private readonly List<UiStatePatch> _patches = [];

    public UiSystemPhase Phase { get; }

    public string Name { get; }

    public int LastEventCount { get; private set; }

    public int LastPatchCount { get; private set; }

    public IReadOnlyList<UiPlatformEvent> LastEvents => _events;

    public IReadOnlyList<UiStatePatch> LastPatches => _patches;

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
            _events.Clear();
            LastEventCount = world.Events.Drain(_events, world.Scopes);
            return;
        }

        _patches.Clear();
        LastPatchCount = world.Patches.Drain(_patches, world.Scopes);
    }
}
