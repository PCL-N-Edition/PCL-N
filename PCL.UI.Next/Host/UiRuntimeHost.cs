// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Process-facing host around <see cref="UiWorld"/>. Safe to construct while the
/// experimental ECS backend is unimplemented; <see cref="TryStart"/> refuses activation.
/// </summary>
public sealed class UiRuntimeHost
{
    private readonly IUiClock _clock;
    private UiWorld? _world;
    private bool _running;

    public UiRuntimeHost(IUiClock? clock = null)
    {
        _clock = clock ?? new DeterministicUiClock();
    }

    public UiWorld? World => _world;

    public bool IsRunning => _running;

    /// <summary>
    /// Creates a world and marks the host running only when the experimental ECS
    /// path is allowed for this process.
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

        _world = new UiWorld(_clock);
        _running = true;
        reason = "ok";
        return true;
    }

    /// <summary>
    /// Creates a world for unit tests / playground without the experimental gate.
    /// Production hosts must use <see cref="TryStart"/>.
    /// </summary>
    public UiWorld CreateWorldForTests()
    {
        _world = new UiWorld(_clock);
        _running = true;
        return _world;
    }

    public bool Update(bool force = false)
    {
        if (!_running || _world is null)
            return false;
        return _world.Update(force);
    }

    public void Stop()
    {
        _running = false;
        _world = null;
    }
}
