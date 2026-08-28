namespace PCL.Xsr;

/// <summary>
/// Identifies one XSR runtime session across diagnostics and transport.
/// </summary>
public readonly record struct XsrSessionId
{
    public XsrSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A session identifier cannot be empty.", nameof(value));
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
    /// Creates a new session identifier.
    /// </summary>
    public static XsrSessionId Create() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
