using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next;

/// <summary>
/// Render-thread-owned progress presentation for one entity: <see cref="Target"/> is the
/// state-bound fill fraction (0..1), <see cref="Presented"/> is the renderer-owned value the
/// backend animates toward the target so fast stage jumps catch up smoothly. Never a
/// product/service state cell.
/// </summary>
public sealed class XsrUiProgress
{
    public XsrStateId BoundState { get; set; }
    public double Target { get; internal set; }

    public double Presented { get; internal set; }

    internal long Revision { get; set; }
}
