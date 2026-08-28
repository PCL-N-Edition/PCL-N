namespace PCL.Xsr.State;

/// <summary>
/// One coherent typed cell read: value, revision, and availability move together.
/// </summary>
public readonly record struct XsrStateValue<TValue>(
    XsrStateId Id,
    long Revision,
    XsrStateAvailability Availability,
    bool HasValue,
    TValue Value)
{
    public bool IsAvailable => Availability == XsrStateAvailability.Available;
}
