using System.Globalization;

namespace PCL.Xsr;

/// <summary>
/// Identifies an XSR registration by its compact process-local numeric value.
/// </summary>
public readonly struct XsrRuntimeId : IEquatable<XsrRuntimeId>
{
    /// <summary>
    /// Creates an assigned runtime identifier. Zero is reserved for an uninitialized value.
    /// </summary>
    public XsrRuntimeId(uint value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        Value = value;
    }

    /// <summary>
    /// Gets the numeric identifier value.
    /// </summary>
    public uint Value { get; }

    /// <summary>
    /// Gets a value indicating whether the identifier is nonzero.
    /// </summary>
    public bool IsAssigned => Value != 0;

    /// <inheritdoc />
    public bool Equals(XsrRuntimeId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is XsrRuntimeId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public static bool operator ==(XsrRuntimeId left, XsrRuntimeId right) => left.Equals(right);

    public static bool operator !=(XsrRuntimeId left, XsrRuntimeId right) => !left.Equals(right);
}
