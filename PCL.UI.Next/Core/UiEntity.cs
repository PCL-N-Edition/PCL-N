// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Stable entity handle: slot index + generation. Destroyed slots bump generation so stale handles fail validation.
/// Layout target: 8 bytes (int + uint).
/// </summary>
public readonly struct UiEntity : IEquatable<UiEntity>
{
    public UiEntity(int index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public int Index { get; }
    public uint Generation { get; }

    public static UiEntity None { get; } = new(0, 0);

    public bool IsNone => Index == 0 && Generation == 0;

    public bool Equals(UiEntity other) => Index == other.Index && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is UiEntity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    public static bool operator ==(UiEntity left, UiEntity right) => left.Equals(right);

    public static bool operator !=(UiEntity left, UiEntity right) => !left.Equals(right);

    public override string ToString() =>
        IsNone ? "UiEntity(none)" : $"UiEntity({Index}@{Generation})";
}
