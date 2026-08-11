// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Style class identity used by authoring. The runtime stores a stable integer id
/// (string name is optional diagnostics only).
/// </summary>
public readonly struct UiClass : IEquatable<UiClass>
{
    public UiClass(int id, string? name = null)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        Id = id;
        Name = name;
    }

    public int Id { get; }
    public string? Name { get; }

    public static UiClass Button { get; } = new(1, "Button");
    public static UiClass PageTitle { get; } = new(2, "PageTitle");
    public static UiClass Body { get; } = new(3, "Body");
    public static UiClass Card { get; } = new(4, "Card");

    public bool Equals(UiClass other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is UiClass other && Equals(other);
    public override int GetHashCode() => Id;
    public static bool operator ==(UiClass left, UiClass right) => left.Equals(right);
    public static bool operator !=(UiClass left, UiClass right) => !left.Equals(right);
    public override string ToString() => Name is { Length: > 0 } n ? $"{n}#{Id}" : $"Class#{Id}";
}
