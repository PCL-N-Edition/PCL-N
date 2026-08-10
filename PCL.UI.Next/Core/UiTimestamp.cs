// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Monotonic runtime time in seconds (double) for animation / frame logic.</summary>
public readonly struct UiTimestamp : IEquatable<UiTimestamp>, IComparable<UiTimestamp>
{
    public UiTimestamp(double seconds)
    {
        Seconds = seconds;
    }

    public double Seconds { get; }

    public static UiTimestamp Zero { get; } = new(0d);

    public UiTimestamp AddSeconds(double delta) => new(Seconds + delta);

    public double SecondsSince(UiTimestamp earlier) => Seconds - earlier.Seconds;

    public int CompareTo(UiTimestamp other) => Seconds.CompareTo(other.Seconds);

    public bool Equals(UiTimestamp other) => Seconds.Equals(other.Seconds);

    public override bool Equals(object? obj) => obj is UiTimestamp other && Equals(other);

    public override int GetHashCode() => Seconds.GetHashCode();

    public static bool operator ==(UiTimestamp left, UiTimestamp right) => left.Equals(right);

    public static bool operator !=(UiTimestamp left, UiTimestamp right) => !left.Equals(right);

    public override string ToString() => Seconds.ToString("0.###") + "s";
}
