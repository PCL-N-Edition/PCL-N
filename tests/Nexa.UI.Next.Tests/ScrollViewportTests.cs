using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static (XsrUiTree Tree, XsrUiRenderer Renderer, XsrUiEntityId Root, XsrUiEntityId Child, XsrUiScroll Scroll)
        ScrollViewport(XsrUiOrientation direction, bool wraps = false)
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("scroll"), child = tree.Create("content");
        tree.SetComponent(root, new XsrUiElement { Width = 300, Height = 40 });
        tree.SetComponent(root, new XsrUiStackPanel(direction));
        XsrUiScroll scroll = new() { ShowsVerticalIndicator = true };
        tree.SetComponent(root, scroll);
        tree.SetComponent(child, new XsrUiElement
        {
            Weight = direction == XsrUiOrientation.Horizontal ? 1 : 0,
            Height = !wraps && direction == XsrUiOrientation.Vertical ? 20 : null
        });
        if (wraps)
        {
            tree.SetComponent(child, new XsrUiText(new string('x', 84)) { MaxLines = 10 });
            tree.SetComponent(child, new XsrUiVisualStyle { FontSize = 14, WrapText = true });
        }
        tree.Attach(child, root);
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build());
        renderer.SetRoot(root);
        return (tree, renderer, root, child, scroll);
    }

    private static void VerticalStackReservesVerticalIndicatorGutter()
    {
        var f = ScrollViewport(XsrUiOrientation.Vertical);
        XsrUiScene scene = f.Renderer.Render();
        AssertEqual(300d, scene.Nodes.Single(n => n.Entity == f.Root).Rect.Width);
        AssertEqual(288d, scene.Nodes.Single(n => n.Entity == f.Child).Rect.Width);
    }

    private static void HorizontalStackReservesRightSideVerticalIndicatorGutter()
    {
        var f = ScrollViewport(XsrUiOrientation.Horizontal);
        XsrUiScene scene = f.Renderer.Render();
        AssertEqual(new XsrUiRect(0, 0, 288, 40), scene.Nodes.Single(n => n.Entity == f.Child).Rect);
    }

    private static void WrappedContentMeasuresInsideIndicatorViewport()
    {
        var f = ScrollViewport(XsrUiOrientation.Vertical, wraps: true);
        XsrUiScene scene = f.Renderer.Render();
        AssertEqual(new XsrUiRect(0, 0, 288, 60), scene.Nodes.Single(n => n.Entity == f.Child).Rect);
    }

    private static void IndicatorGutterAffectsScrollExtentCoherently()
    {
        var f = ScrollViewport(XsrUiOrientation.Vertical, wraps: true);
        f.Scroll.OffsetY = 100;
        XsrUiScene scene = f.Renderer.Render();
        AssertEqual(20d, f.Scroll.OffsetY);
        var snapshot = scene.Nodes.Single(n => n.Entity == f.Root).Scroll!.Value;
        AssertEqual(288d, snapshot.ViewportWidth);
        AssertEqual(60d, snapshot.ContentHeight);
        AssertEqual(20d, snapshot.MaximumOffsetY);
        XsrUiRect child = scene.Nodes.Single(n => n.Entity == f.Child).Rect;
        AssertEqual(40d, child.Y + child.Height);
        f.Scroll.ShowsVerticalIndicator = false;
        f.Tree.MarkDirty(f.Root, XsrUiDirtyKinds.Layout);
        scene = f.Renderer.Render();
        AssertEqual(0d, f.Scroll.OffsetY);
        AssertEqual(new XsrUiRect(0, 0, 300, 40), scene.Nodes.Single(n => n.Entity == f.Child).Rect);
    }
}
