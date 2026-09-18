using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next;

/// <summary>
/// Which edge the fill grows from. <see cref="Leading"/> is the standard bar (grows right in
/// horizontal slots); <see cref="Bottom"/> rises from the bottom edge of the slot so a round
/// container reads as filling up, mirroring the legacy task bubble.
/// </summary>
public enum XsrUiProgressFillAnchor
{
    Leading = 0,
    Bottom = 1,
}

/// <summary>
/// Render-thread-owned progress presentation for one entity: <see cref="Target"/> is the
/// state-bound fill fraction (0..1), <see cref="Presented"/> is the renderer-owned value the
/// backend animates toward the target so fast stage jumps catch up smoothly. Never a
/// product/service state cell.
/// </summary>
public sealed class XsrUiProgress
{
    public XsrStateId BoundState { get; set; }

    public XsrUiProgressFillAnchor Anchor { get; set; }

    public double Target { get; internal set; }

    public double Presented { get; internal set; }

    internal long Revision { get; set; }

    /// <summary>
    /// Sets the fill target for runtime-built entities without a state binding. Same
    /// presentation-only status as <see cref="Presented"/>: the backend still animates toward
    /// the target, product state is untouched.
    /// </summary>
    public void SetTarget(double value) => Target = Math.Clamp(value, 0d, 1d);
}
