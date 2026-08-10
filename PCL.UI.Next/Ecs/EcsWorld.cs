// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.Ecs;

/// <summary>
/// Minimal ECS world scaffold for the experimental UI render architecture.
/// Full component stores, archetypes, and queries will land with the real pipeline.
/// </summary>
public sealed class EcsWorld
{
    private int _nextId = 1;

    public int EntityCount { get; private set; }

    public EntityId CreateEntity()
    {
        int id = _nextId++;
        EntityCount++;
        return new EntityId(id);
    }

    public void Clear()
    {
        _nextId = 1;
        EntityCount = 0;
    }
}
