namespace PCL.Xsr;

/// <summary>
/// Correlates one XSR operation across routing and diagnostics.
/// </summary>
public readonly struct XsrCorrelationId : IEquatable<XsrCorrelationId>
{
    /// <summary>
    /// Creates an assigned correlation identifier.
    /// </summary>
    public XsrCorrelationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A correlation identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the underlying identifier, or <see cref="Guid.Empty"/> when unassigned.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Gets a value indicating whether this identifier is assigned.
    /// </summary>
    public bool IsAssigned => Value != Guid.Empty;

    /// <summary>
    /// Creates a new correlation identifier.
    /// </summary>
    public static XsrCorrelationId Create() => new(Guid.NewGuid());

    /// <inheritdoc />
    public bool Equals(XsrCorrelationId other) => Value.Equals(other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is XsrCorrelationId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");

    public static bool operator ==(XsrCorrelationId left, XsrCorrelationId right) => left.Equals(right);

    public static bool operator !=(XsrCorrelationId left, XsrCorrelationId right) => !left.Equals(right);
}
