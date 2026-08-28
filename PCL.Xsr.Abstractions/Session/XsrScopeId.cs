namespace PCL.Xsr;

/// <summary>
/// Identifies one runtime lifetime scope across diagnostics and cleanup.
/// </summary>
public readonly record struct XsrScopeId
{
    public XsrScopeId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A scope identifier cannot be empty.", nameof(value));
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
    /// Creates a new scope identifier.
    /// </summary>
    public static XsrScopeId Create() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
