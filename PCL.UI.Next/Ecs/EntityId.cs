// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.Ecs;

/// <summary>Opaque entity handle in the UI ECS world.</summary>
public readonly record struct EntityId(int Value)
{
    public static EntityId None { get; } = new(0);

    public bool IsNone => Value == 0;

    public override string ToString() => IsNone ? "Entity(none)" : "Entity(" + Value + ")";
}
