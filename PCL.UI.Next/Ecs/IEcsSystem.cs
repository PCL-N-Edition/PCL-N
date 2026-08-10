// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.Ecs;

/// <summary>One frame phase of the experimental ECS UI pipeline.</summary>
public interface IEcsSystem
{
    /// <summary>Stable name for diagnostics (e.g. layout, dirty, draw-batch).</summary>
    string Name { get; }

    /// <summary>Runs once per frame while the ECS host is active.</summary>
    void Update(EcsWorld world, in EcsFrameContext frame);
}
