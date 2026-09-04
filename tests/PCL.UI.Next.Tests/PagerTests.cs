using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static (XsrUiTree Tree, XsrUiRenderer Renderer, XsrUiEntityId Root, XsrUiEntityId[] Pages)
        CreatePagerFixture(int count = 3)
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("pager");
        tree.SetComponent(root, new XsrUiElement { Width = 180, Height = 100 });
        tree.SetComponent(root, new XsrUiPager());
        tree.SetComponent(root, new XsrUiInput { Focusable = true });
        List<XsrUiEntityId> pages = [];
        for (int i = 0; i < count; i++)
        {
            XsrUiEntityId page = tree.Create($"page-{i}");
            tree.SetComponent(page, new XsrUiText($"Page {i}"));
            tree.SetComponent(page, new XsrUiInput { Focusable = true, Clickable = true });
            tree.SetComponent(page, new XsrUiCommandBinding(XsrSemanticId.Parse("test.page")));
            tree.Attach(page, root);
            pages.Add(page);
        }
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build(), new XsrUiIntentBuffer());
        renderer.SetRoot(root);
        _ = renderer.Render();
        return (tree, renderer, root, [.. pages]);
    }

    private static void PagerClipsPagesAndExcludesInactiveInput()
    {
        var (tree, renderer, root, pages) = CreatePagerFixture();
        AssertTrue(renderer.Focus(pages[0]));
        AssertTrue(renderer.MovePager(root, 1));
        AssertEqual(root, renderer.Focused);
        renderer.SetPagerPresentationPosition(root, .5);
        XsrUiScene scene = renderer.Render();
        XsrUiSceneNode outgoing = scene.Nodes.Single(node => node.Entity == pages[0]);
        XsrUiSceneNode incoming = scene.Nodes.Single(node => node.Entity == pages[1]);
        AssertEqual(new XsrUiRect(0, -50, 180, 100), outgoing.Rect);
        AssertEqual(new XsrUiRect(0, 0, 180, 50), outgoing.ClipRect!.Value);
        AssertEqual(new XsrUiRect(0, 50, 180, 50), incoming.ClipRect!.Value);
        AssertTrue(!outgoing.IsAccessible && !outgoing.IsFocusable && !outgoing.IsClickable);
        AssertTrue(incoming.IsAccessible && incoming.IsFocusable);
        AssertTrue(!renderer.Activate(pages[0]) && !renderer.Focus(pages[0]));
        AssertEqual(root, renderer.HitTest(new XsrUiPoint(50, 20)));
        AssertEqual(pages[1], renderer.HitTest(new XsrUiPoint(50, 70)));
        renderer.SetPagerPresentationPosition(root, 1);
        scene = renderer.Render();
        AssertEqual(2, scene.Count);
        AssertEqual(pages[1], scene[1].Entity);
        AssertEqual(1, tree.GetComponent<XsrUiPager>(root)!.PageIndex);
    }

    private static void PagerSupportsAllInputPaths()
    {
        var (tree, renderer, root, pages) = CreatePagerFixture();
        XsrUiPager pager = tree.GetComponent<XsrUiPager>(root)!;
        renderer.ReducedMotion = true;
        AssertTrue(renderer.PointerScroll(new XsrUiPoint(50, 50), 1));
        _ = renderer.Render();
        AssertEqual(1, pager.PageIndex);
        AssertEqual(1d, pager.Position);
        AssertTrue(renderer.Focus(pages[1]));
        AssertTrue(renderer.HandleKey(XsrUiKey.Up));
        _ = renderer.Render();
        AssertEqual(0d, pager.Position);

        renderer.ReducedMotion = false;
        AssertTrue(renderer.PointerPressed(new XsrUiPoint(50, 80)));
        AssertTrue(renderer.PointerMoved(new XsrUiPoint(50, 20)));
        AssertClose(.6, pager.Position);
        renderer.SetPagerPresentationPosition(root, 0); // Clock cannot overwrite a live drag.
        AssertClose(.6, pager.Position);
        AssertTrue(renderer.PointerReleased(new XsrUiPoint(50, 20)));
        AssertEqual(1, pager.PageIndex);
        AssertTrue(!pager.IsDragging);
        renderer.ReducedMotion = true;
        renderer.SetPagerPresentationPosition(root, .7);
        _ = renderer.Render();
        AssertEqual(1d, pager.Position);

        AssertTrue(renderer.PointerPressed(new XsrUiPoint(50, 50)));
        AssertTrue(renderer.PointerMoved(new XsrUiPoint(50, 80)));
        AssertTrue(renderer.CancelPointerGesture());
        AssertEqual(1, pager.PageIndex);
        AssertEqual(1d, pager.Position);
        AssertTrue(!pager.IsDragging);
        AssertTrue(!renderer.PointerReleased(new XsrUiPoint(50, 80)));

        var empty = CreatePagerFixture(0);
        AssertTrue(!empty.Renderer.MovePager(empty.Root, 1));
        AssertTrue(empty.Renderer.PointerPressed(new XsrUiPoint(10, 80)));
        AssertTrue(empty.Renderer.PointerMoved(new XsrUiPoint(10, 20)));
        AssertTrue(empty.Renderer.PointerReleased(new XsrUiPoint(10, 20)));
    }
}
