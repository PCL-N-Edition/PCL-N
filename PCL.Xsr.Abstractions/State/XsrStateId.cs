namespace PCL.Xsr;

/// <summary>
/// Identifies one state entry without allowing accidental use as another state kind or route.
/// </summary>
public readonly record struct XsrStateId
{
    public XsrStateId(XsrRuntimeId value)
    {
        if (!value.IsAssigned)
        {
            throw new ArgumentException("A state ID must be assigned.", nameof(value));
        }

        Value = value;
    }

    public XsrRuntimeId Value { get; }

    public bool IsAssigned => Value.IsAssigned;

    public override string ToString() => Value.ToString();
}
