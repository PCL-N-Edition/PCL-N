// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Version of the frozen Runtime and backend contract.</summary>
public readonly record struct UiContractVersion : IComparable<UiContractVersion>
{
    public UiContractVersion(ushort major, ushort minor)
    {
        if (major == 0)
            throw new ArgumentOutOfRangeException(nameof(major));
        Major = major;
        Minor = minor;
    }

    public ushort Major { get; }

    public ushort Minor { get; }

    public bool IsValid => Major > 0;

    /// <summary>Returns whether this Runtime version supports a consumer requiring <paramref name="required"/>.</summary>
    public bool Supports(UiContractVersion required) =>
        IsValid && required.IsValid && Major == required.Major && Minor >= required.Minor;

    public int CompareTo(UiContractVersion other)
    {
        int major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>Single compatibility authority for the public PCL.UI.Next Runtime contract.</summary>
public static class UiRuntimeContract
{
    public static UiContractVersion Current { get; } = new(1, 0);

    public static bool Supports(UiContractVersion required) => Current.Supports(required);

    public static void EnsureSupported(UiContractVersion required, string? consumer = null)
    {
        if (Supports(required))
            return;
        string name = string.IsNullOrWhiteSpace(consumer) ? "Consumer" : consumer;
        throw new NotSupportedException(
            $"{name} requires PCL.UI.Next contract {required}, but Runtime provides {Current}.");
    }
}
