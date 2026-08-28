namespace PCL.Xsr;

/// <summary>
/// Identifies one event route without allowing accidental use as another route or state kind.
/// </summary>
public readonly record struct XsrEventId
{
    public XsrEventId(XsrRuntimeId value)
    {
        if (!value.IsAssigned)
        {
            throw new ArgumentException("An event ID must be assigned.", nameof(value));
        }

        Value = value;
    }

    public XsrRuntimeId Value { get; }

    public bool IsAssigned => Value.IsAssigned;

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Declares the documented ordering domain of one event.
/// </summary>
public enum XsrEventOrdering
{
    /// <summary>
    /// All publications of the event share one ordering domain.
    /// </summary>
    Global = 1,

    /// <summary>
    /// Publications are ordered inside one domain per caller-provided scope key.
    /// </summary>
    PerKey = 2,
}
