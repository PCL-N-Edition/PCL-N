namespace PCL.Xsr.State;

/// <summary>
/// Classifies why one state entry changed.
/// </summary>
public enum XsrStateChangeReason
{
    ValuePublished = 1,
    CoalescedApplied = 2,
    CollectionDeltaApplied = 3,
    AvailabilityChanged = 4,
    DerivedRecomputed = 5,
}

/// <summary>
/// Describes one applied state change without duplicating the value payload.
/// </summary>
public readonly record struct XsrStateChange(
    XsrStateId Id,
    XsrSemanticId SemanticId,
    XsrStateKind Kind,
    long Revision,
    XsrStateAvailability Availability,
    XsrStateChangeReason Reason)
{
    public bool IsDerived => Kind == XsrStateKind.Cell && Reason == XsrStateChangeReason.DerivedRecomputed;
}
