namespace PCL.UI.Next;

/// <summary>Render-thread-owned vertical paging state, never a product/service state cell.</summary>
public sealed class XsrUiPager
{
    public int PageIndex { get; internal set; }
    public int PageCount { get; internal set; }
    public double Position { get; internal set; }
    public bool IsDragging { get; internal set; }
    public double ReleaseVelocity { get; internal set; }
    internal long Revision { get; set; }

    public XsrUiPagerSnapshot Snapshot() =>
        new(PageIndex, PageCount, Position, IsDragging, ReleaseVelocity, Revision);
}

/// <summary>Immutable paging facts; position and velocity are measured in viewport pages.</summary>
public readonly record struct XsrUiPagerSnapshot(
    int PageIndex, int PageCount, double Position, bool IsDragging, double ReleaseVelocity, long Revision);
