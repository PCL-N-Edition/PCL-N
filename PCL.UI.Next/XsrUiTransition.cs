using PCL.Xsr;

namespace PCL.UI.Next;

/// <summary>Identifies the content of a coordinated presentation group without backend-specific timing.</summary>
public sealed class XsrUiTransition
{
    public string? Key { get; set; }
    public XsrStateId BoundKey { get; set; }
    /// <summary>Zero selects a fade; nonzero selects a signed horizontal slide in scene units.</summary>
    public double OffsetX { get; set; }
    internal double PresentedOffsetX { get; set; }
    internal string? PresentedKey { get; set; }
    internal bool HasPresentedKey { get; set; }
}
