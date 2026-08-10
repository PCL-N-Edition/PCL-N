// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.Ecs;

/// <summary>Per-frame timing inputs for ECS UI systems.</summary>
public readonly record struct EcsFrameContext(
    long FrameIndex,
    double DeltaSeconds,
    double TotalSeconds);
