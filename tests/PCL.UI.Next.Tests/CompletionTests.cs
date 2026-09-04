using PCL.Xsr.State;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static void EasingCurvesAreDeterministic()
    {
        AssertEqual(0.5, XsrUiEasings.Linear(0.5));
        AssertEqual(0.25, XsrUiEasings.EaseInQuad(0.5));
        AssertEqual(0.75, XsrUiEasings.EaseOutQuad(0.5));
        AssertEqual(0.5, XsrUiEasings.EaseInOutQuad(0.5));
        AssertEqual(0.0, XsrUiEasings.EaseInOutQuad(0.0));
        AssertEqual(1.0, XsrUiEasings.EaseInOutQuad(1.0));
    }

    private static void AnimatorAppliesEasingAndKeyframes()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        XsrUiAnimation animation = new(TimeSpan.FromMilliseconds(100))
        {
            Easing = XsrUiEasings.EaseInQuad,
            Keyframes =
            [
                new XsrUiKeyframe(0, 0),
                new XsrUiKeyframe(0.5, 100),
                new XsrUiKeyframe(1, 200),
            ],
        };
        tree.SetComponent(root, animation);
        XsrUiAnimator animator = new(tree);
        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        animator.Start(root);

        animator.Tick(TimeSpan.FromMilliseconds(25), reducedMotion: false);
        // Raw progress 0.25 eased by ease-in quad is 0.0625; the track maps it to 12.5.
        AssertClose(0.25, animation.Progress);
        AssertEqual(12.5, animation.Value);

        animator.Tick(TimeSpan.FromMilliseconds(100), reducedMotion: false);
        AssertEqual(1.0, animation.Progress);
        AssertEqual(200, animation.Value);
        AssertEqual(0, animator.ActiveCount);

        XsrUiScene scene = renderer.Render();
        AssertEqual(1.0, scene[0].AnimationProgress!.Value);
        AssertEqual(200, scene[0].AnimationValue!.Value);
    }

    private static void KeyframesHoldBoundaryValues()
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiAnimation animation = new(TimeSpan.FromMilliseconds(100))
        {
            Keyframes = [new XsrUiKeyframe(0.25, 10), new XsrUiKeyframe(0.75, 30)],
        };
        tree.SetComponent(root, animation);
        XsrUiAnimator animator = new(tree);
        animator.Start(root);

        // Before the first keyframe the boundary value holds.
        animator.Tick(TimeSpan.FromMilliseconds(5), reducedMotion: false);
        AssertClose(0.05, animation.Progress);
        AssertEqual(10, animation.Value);

        // Past the last keyframe the boundary value holds.
        animator.Tick(TimeSpan.FromMilliseconds(200), reducedMotion: false);
        AssertEqual(1.0, animation.Progress);
        AssertEqual(30, animation.Value);
    }

    private static void ScrollOffsetsChildrenAndClamps()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiElement { Height = 100 });
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 0 });
        tree.SetComponent(root, new XsrUiScroll());
        for (int index = 0; index < 10; index++)
        {
            XsrUiEntityId row = tree.Create($"row-{index}");
            tree.SetComponent(row, new XsrUiElement { Width = 100, Height = 40 });
            tree.Attach(row, root);
        }

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene unscrolled = renderer.Render();
        AssertEqual(new XsrUiRect(0, 0, 100, 40), unscrolled[1].Rect);

        // Scrolling down moves children up; the offset clamps to the content extent.
        AssertTrue(renderer.PointerScroll(new XsrUiPoint(50, 50), deltaY: 500));
        XsrUiScene scrolled = renderer.Render();
        XsrUiScroll scroll = tree.GetComponent<XsrUiScroll>(root)!;
        AssertEqual(300, scroll.OffsetY);
        // Fully offscreen rows are absent from the scene/accessibility projection. The first
        // partially visible row retains its layout rect and carries the viewport clip.
        AssertEqual("row-7", tree.Name(scrolled[1].Entity));
        AssertEqual(new XsrUiRect(0, -20, 100, 40), scrolled[1].Rect);
        AssertEqual(new XsrUiRect(0, 0, 100, 20), scrolled[1].ClipRect!.Value);
        AssertFalse(renderer.HitTest(new XsrUiPoint(50, -10)).IsAssigned);

        // Scrolling back up returns to the origin.
        AssertTrue(renderer.PointerScroll(new XsrUiPoint(50, 50), deltaY: -1000));
        _ = renderer.Render();
        AssertEqual(0, scroll.OffsetY);
    }

    private static void ScrollHitTestFollowsOffset()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiElement { Height = 40 });
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        tree.SetComponent(root, new XsrUiScroll());
        XsrUiEntityId alpha = tree.Create("alpha");
        tree.SetComponent(alpha, new XsrUiElement { Width = 100, Height = 40 });
        XsrUiEntityId beta = tree.Create("beta");
        tree.SetComponent(beta, new XsrUiElement { Width = 100, Height = 40 });
        tree.Attach(alpha, root);
        tree.Attach(beta, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        _ = renderer.Render();

        AssertTrue(renderer.PointerScroll(new XsrUiPoint(50, 20), deltaY: 40));
        XsrUiScene scene = renderer.Render();
        // Beta moved into the viewport at y=0; alpha scrolled out above.
        AssertEqual(new XsrUiRect(0, 0, 100, 40), scene.Nodes.First(node => node.Entity.Equals(beta)).Rect);
        AssertEqual(beta, renderer.HitTest(new XsrUiPoint(50, 20)));
        AssertFalse(scene.Nodes.Any(node => node.Entity == alpha));
    }

    private static void ImageSourceCarriesToTheScene()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiElement { Width = 80, Height = 60 });
        tree.SetComponent(root, new XsrUiImage("icons/app.png"));
        tree.SetComponent(root, new XsrUiSemantic(XsrUiSemanticRole.Image, "App icon"));
        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual("icons/app.png", scene[0].ImageSource);
        AssertEqual(XsrUiSemanticRole.Image, scene[0].Role);
        AssertFalse(scene[0].AnimationValue.HasValue);
    }
}
