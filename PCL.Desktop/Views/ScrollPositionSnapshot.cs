using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace PCL.Desktop.Views;

internal sealed class ScrollPositionSnapshot
{
    private readonly (ScrollViewer Viewer, Vector Offset)[] _positions;

    private ScrollPositionSnapshot((ScrollViewer, Vector)[] positions) => _positions = positions;

    public static ScrollPositionSnapshot Capture(Visual root) => new(
        root.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(viewer => viewer.IsEffectivelyVisible)
            .Select(viewer => (viewer, viewer.Offset)).ToArray());

    public void Restore(Visual root)
    {
        foreach ((ScrollViewer viewer, Vector offset) in _positions)
        {
            // A navigation during suspension must not restore an old page.
            if (viewer.GetVisualAncestors().Contains(root))
                viewer.Offset = offset;
        }
    }
}
