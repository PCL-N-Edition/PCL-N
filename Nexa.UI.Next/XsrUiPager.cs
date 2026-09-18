namespace Nexa.UI.Next;

/// <summary>
/// Render-thread-owned paging state, never a product/service state cell. Direction determines
/// layout, drag axis, wheel routing, and the keyboard arrows that move between pages.
/// </summary>
public sealed class XsrUiPager(XsrUiOrientation direction = XsrUiOrientation.Vertical)
{
    public XsrUiOrientation Direction { get; } = direction;
    public int PageIndex { get; internal set; }
    public int PageCount { get; internal set; }
    public double Position { get; internal set; }
    public bool IsDragging { get; internal set; }
    public double ReleaseVelocity { get; internal set; }
    internal long Revision { get; set; }

    public XsrUiPagerSnapshot Snapshot() =>
        new(Direction, PageIndex, PageCount, Position, IsDragging, ReleaseVelocity, Revision);
}

/// <summary>Immutable paging facts; position and velocity are measured in viewport pages.</summary>
public readonly record struct XsrUiPagerSnapshot(
    XsrUiOrientation Direction,
    int PageIndex,
    int PageCount,
    double Position,
    bool IsDragging,
    double ReleaseVelocity,
    long Revision);
