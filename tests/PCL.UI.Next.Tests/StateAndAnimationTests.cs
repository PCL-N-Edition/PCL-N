using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static void DerivedStateDrivesBoundText()
    {
        // Reviewer regression: an entity bound to a DERIVED entry must re-render when the
        // derived entry's source changes, and the applied read must recompute the derivation.
        XsrUiTree tree = new();
        XsrStateStoreBuilder states = new();
        states.Cell<int>("render.received".AsXsrId(), "Download");
        states.Derived<int>(
            "render.percent".AsXsrId(),
            "Derived",
            ["render.received".AsXsrId()],
            static (reader, cancellationToken) => reader.Read<int>(
                reader.Resolve("render.received".AsXsrId()),
                cancellationToken).Value * 2);
        XsrUiStateBridge bridge = new(tree);
        XsrStateStore store = states.Build(bridge);
        XsrStateId derived = store.Resolve("render.percent".AsXsrId());

        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiText(string.Empty) { BoundState = derived });
        tree.SetComponent(root, new XsrUiStateBinding(derived));
        XsrUiRenderer renderer = new(tree, store, stateBridge: bridge);
        renderer.SetRoot(root);

        _ = store.Publish(store.Resolve("render.received".AsXsrId()), 21);
        XsrUiScene first = renderer.Render();
        AssertEqual("42", first[0].Text);

        _ = store.Publish(store.Resolve("render.received".AsXsrId()), 10);
        XsrUiScene second = renderer.Render();
        AssertEqual("20", second[0].Text);
        AssertTrue(second.Version > first.Version);
    }

    private static void CoalescedStateBecomesVisibleWithoutManualFlush()
    {
        // Reviewer regression: coalesced publications have no applied revision yet, so a cached
        // scene could hide them forever. The published notification must dirty the entity and
        // the render-time applied read must flush the pending value.
        XsrUiTree tree = new();
        XsrStateStoreBuilder states = new();
        states.Cell<int>("render.progress".AsXsrId(), "Download");
        XsrUiStateBridge bridge = new(tree);
        XsrStateStore store = states.Build(bridge);
        XsrStateId progress = store.Resolve("render.progress".AsXsrId());

        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiText(string.Empty) { BoundState = progress });
        XsrUiRenderer renderer = new(tree, store, stateBridge: bridge);
        renderer.SetRoot(root);
        _ = renderer.Render();

        store.PublishCoalesced(progress, 7);
        store.PublishCoalesced(progress, 13);
        XsrUiScene scene = renderer.Render();

        AssertEqual("13", scene[0].Text);
        AssertEqual(1L, store.CoalescedCount(progress));
    }

    private static void AnimatorAdvancesAndCompletes()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiAnimation(TimeSpan.FromMilliseconds(100)));
        XsrUiAnimator animator = new(tree);
        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        animator.Start(root);

        animator.Tick(TimeSpan.FromMilliseconds(50), reducedMotion: false);
        XsrUiAnimation? animation = tree.GetComponent<XsrUiAnimation>(root);
        AssertEqual(0.5, animation!.Progress);
        AssertTrue(tree.DirtyKinds(root).HasFlag(XsrUiDirtyKinds.Paint));
        AssertEqual(1, animator.ActiveCount);

        animator.Tick(TimeSpan.FromMilliseconds(100), reducedMotion: false);
        AssertEqual(1.0, animation.Progress);
        AssertEqual(0, animator.ActiveCount);

        XsrUiScene scene = renderer.Render();
        AssertEqual(1.0, scene[0].AnimationProgress!.Value);
    }

    private static void ReducedMotionCompletesAnimationsImmediately()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiAnimation(TimeSpan.FromSeconds(10)));
        XsrUiAnimator animator = new(tree);
        var renderer = new XsrUiRenderer(tree, store);
        renderer.SetRoot(root);
        animator.Start(root);

        animator.Tick(TimeSpan.FromMilliseconds(10), reducedMotion: true);

        AssertEqual(1.0, tree.GetComponent<XsrUiAnimation>(root)!.Progress);
        AssertEqual(0, animator.ActiveCount);
    }
}
