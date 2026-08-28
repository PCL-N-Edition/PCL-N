namespace PCL.Xsr;

/// <summary>
/// Identifies a command route without allowing accidental use as another route kind.
/// </summary>
public readonly record struct XsrCommandId
{
    public XsrCommandId(XsrRuntimeId value)
    {
        if (!value.IsAssigned)
        {
            throw new ArgumentException("A command ID must be assigned.", nameof(value));
        }

        Value = value;
    }

    public XsrRuntimeId Value { get; }

    public bool IsAssigned => Value.IsAssigned;

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identifies a query route without allowing accidental use as another route kind.
/// </summary>
public readonly record struct XsrQueryId
{
    public XsrQueryId(XsrRuntimeId value)
    {
        if (!value.IsAssigned)
        {
            throw new ArgumentException("A query ID must be assigned.", nameof(value));
        }

        Value = value;
    }

    public XsrRuntimeId Value { get; }

    public bool IsAssigned => Value.IsAssigned;

    public override string ToString() => Value.ToString();
}
