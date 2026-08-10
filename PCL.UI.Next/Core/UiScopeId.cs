// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Lifecycle scope handle (application / window / page / popup / async work).
/// Stale async results must check generation before applying.
/// </summary>
public readonly struct UiScopeId : IEquatable<UiScopeId>
{
    public UiScopeId(int index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public int Index { get; }
    public uint Generation { get; }

    public static UiScopeId None { get; } = new(0, 0);

    public bool IsNone => Index == 0 && Generation == 0;

    public bool Equals(UiScopeId other) => Index == other.Index && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is UiScopeId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    public static bool operator ==(UiScopeId left, UiScopeId right) => left.Equals(right);

    public static bool operator !=(UiScopeId left, UiScopeId right) => !left.Equals(right);

    public override string ToString() =>
        IsNone ? "UiScope(none)" : $"UiScope({Index}@{Generation})";
}
