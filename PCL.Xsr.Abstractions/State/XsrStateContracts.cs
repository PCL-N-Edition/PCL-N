namespace PCL.Xsr;

/// <summary>
/// Distinguishes the declared shape of one state entry.
/// </summary>
public enum XsrStateKind
{
    Cell = 1,
    Collection = 2,
}

/// <summary>
/// Describes whether current state may be trusted, separately from its last value.
/// </summary>
public enum XsrStateAvailability
{
    /// <summary>
    /// The entry exists but no current value can be trusted yet.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The last value is current and trusted.
    /// </summary>
    Available = 1,

    /// <summary>
    /// The last value is retained but a newer truth may exist, for example during a session outage.
    /// </summary>
    Stale = 2,
}

/// <summary>
/// Declares one state entry: its shape, and the owner responsible for writing it.
/// </summary>
public sealed record XsrStateDescriptor
{
    public XsrStateDescriptor(XsrStateKind kind, string owner)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        Kind = kind;
        Owner = owner;
    }

    public XsrStateKind Kind { get; }

    public string Owner { get; }
}
