// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Stable command identity bound from authoring to runtime.</summary>
public readonly struct UiCommand : IEquatable<UiCommand>
{
    public UiCommand(int id, string? name = null)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        Id = id;
        Name = name;
    }

    public int Id { get; }
    public string? Name { get; }

    public static UiCommand None { get; } = default;

    public bool IsNone => Id == 0;

    public bool Equals(UiCommand other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is UiCommand other && Equals(other);
    public override int GetHashCode() => Id;
    public static bool operator ==(UiCommand left, UiCommand right) => left.Equals(right);
    public static bool operator !=(UiCommand left, UiCommand right) => !left.Equals(right);
    public override string ToString() => IsNone ? "Command(none)" : (Name ?? ("Command#" + Id));
}
