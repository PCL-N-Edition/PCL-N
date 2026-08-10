// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.Ecs;

/// <summary>
/// Process-wide host for the experimental ECS UI pipeline.
/// Construction is allowed for unit tests; <see cref="TryStart"/> refuses until
/// <see cref="NextRenderAvailability.IsImplemented"/> is true.
/// </summary>
public sealed class EcsUiRenderHost
{
    private readonly List<IEcsSystem> _systems = [];
    private bool _running;
    private long _frameIndex;

    public EcsWorld World { get; } = new();

    public bool IsRunning => _running;

    public IReadOnlyList<IEcsSystem> Systems => _systems;

    public void RegisterSystem(IEcsSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (_running)
            throw new InvalidOperationException("Cannot register systems while the ECS UI host is running.");
        _systems.Add(system);
    }

    /// <summary>
    /// Attempts to start the ECS UI loop. Returns false while the feature is not implemented
    /// or when the effective mode is classic.
    /// </summary>
    public bool TryStart(NextUiRenderMode requestedMode, out string reason)
    {
        NextUiRenderMode effective = NextRenderAvailability.ResolveEffectiveMode(requestedMode);
        if (effective != NextUiRenderMode.Ecs)
        {
            reason = NextRenderAvailability.IsImplemented
                ? "classic UI path selected"
                : "ECS UI render architecture is not implemented yet";
            return false;
        }

        if (_systems.Count == 0)
        {
            reason = "no ECS systems registered";
            return false;
        }

        _running = true;
        _frameIndex = 0;
        reason = "ok";
        return true;
    }

    public void Stop()
    {
        _running = false;
        World.Clear();
    }

    /// <summary>Advances one frame when running; no-op otherwise.</summary>
    public void Tick(double deltaSeconds, double totalSeconds)
    {
        if (!_running)
            return;

        EcsFrameContext frame = new(++_frameIndex, deltaSeconds, totalSeconds);
        for (int i = 0; i < _systems.Count; i++)
            _systems[i].Update(World, in frame);
    }
}
