using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static void FixedLeafProducesExactRect()
    {
        XsrUiRenderer renderer = BuildSingleLeafRenderer(out XsrUiScene scene);
        _ = renderer;

        AssertEqual(new XsrUiRect(0, 0, 200, 100), scene[0].Rect);
        AssertEqual(1, scene.Count);
    }

    private static void VerticalStackFlowsTopDown()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 10 });
        XsrUiEntityId alpha = tree.Create("alpha");
        tree.SetComponent(alpha, new XsrUiElement { Width = 200, Height = 50 });
        XsrUiEntityId beta = tree.Create("beta");
        tree.SetComponent(beta, new XsrUiElement { Width = 180, Height = 30 });
        tree.Attach(alpha, root);
        tree.Attach(beta, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(0, 0, 800, 600), scene[0].Rect);
        AssertEqual(new XsrUiRect(0, 0, 200, 50), scene[1].Rect);
        AssertEqual(new XsrUiRect(0, 60, 180, 30), scene[2].Rect);
    }

    private static void HorizontalStackFlowsLeftRight()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Horizontal) { Spacing = 5 });
        XsrUiEntityId alpha = tree.Create("alpha");
        tree.SetComponent(alpha, new XsrUiElement { Width = 100, Height = 50 });
        XsrUiEntityId beta = tree.Create("beta");
        tree.SetComponent(beta, new XsrUiElement { Width = 200, Height = 40 });
        tree.Attach(alpha, root);
        tree.Attach(beta, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(0, 0, 100, 50), scene[1].Rect);
        AssertEqual(new XsrUiRect(105, 0, 200, 40), scene[2].Rect);
    }

    private static void PaddingInsetsAndMarginOffsets()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiElement { Padding = XsrUiThickness.Uniform(20) });
        XsrUiEntityId child = tree.Create("child");
        tree.SetComponent(child, new XsrUiElement
        {
            Width = 100,
            Height = 100,
            Margin = XsrUiThickness.Uniform(10),
        });
        tree.Attach(child, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(30, 30, 100, 100), scene[1].Rect);
    }

    private static void CrossAxisAlignmentPositionsChildren()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId centered = tree.Create("centered");
        tree.SetComponent(centered, new XsrUiElement
        {
            Width = 100,
            Height = 40,
            HorizontalAlignment = XsrUiAlignment.Center,
        });
        XsrUiEntityId ending = tree.Create("ending");
        tree.SetComponent(ending, new XsrUiElement
        {
            Width = 100,
            Height = 40,
            HorizontalAlignment = XsrUiAlignment.End,
        });
        tree.Attach(centered, root);
        tree.Attach(ending, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(350, 0, 100, 40), scene[1].Rect);
        AssertEqual(new XsrUiRect(700, 40, 100, 40), scene[2].Rect);
    }

    private static void WeightedStackDistributesStarSlotsAndHonorsLimits()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Horizontal) { Spacing = 20 });
        XsrUiEntityId left = tree.Create("left");
        tree.SetComponent(left, new XsrUiElement
        {
            Weight = 0.92,
            MinWidth = 240,
            MaxWidth = 360,
        });
        XsrUiEntityId right = tree.Create("right");
        tree.SetComponent(right, new XsrUiElement
        {
            Weight = 1.35,
            MinWidth = 280,
        });
        tree.Attach(left, root);
        tree.Attach(right, root);

        XsrUiRenderer renderer = new(tree, store) { Viewport = new XsrUiSize(1000, 200) };
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(0, 0, 360, 200), scene[1].Rect);
        AssertEqual(new XsrUiRect(380, 0, 620, 200), scene[2].Rect);
    }

    private static void WeightedStackFillsRemainingVerticalSpace()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 12 });
        XsrUiEntityId fixedCard = tree.Create("fixed");
        tree.SetComponent(fixedCard, new XsrUiElement { Height = 100 });
        XsrUiEntityId remainingCard = tree.Create("remaining");
        tree.SetComponent(remainingCard, new XsrUiElement { Weight = 1 });
        tree.Attach(fixedCard, root);
        tree.Attach(remainingCard, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(0, 0, 800, 100), scene[1].Rect);
        AssertEqual(new XsrUiRect(0, 112, 800, 488), scene[2].Rect);
    }

    private static void MaximumSizeAndEndAlignmentConstrainPaintRect()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId child = tree.Create("child");
        tree.SetComponent(child, new XsrUiElement
        {
            Width = 500,
            Height = 300,
            MaxWidth = 360,
            MaxHeight = 200,
            HorizontalAlignment = XsrUiAlignment.End,
            VerticalAlignment = XsrUiAlignment.End,
        });
        tree.Attach(child, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(440, 400, 360, 200), scene[1].Rect);
    }

    private static void StackMeasurementIncludesChildMargins()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId nested = tree.Create("nested");
        tree.SetComponent(nested, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId inset = tree.Create("inset");
        tree.SetComponent(inset, new XsrUiElement
        {
            Height = 20,
            Margin = new XsrUiThickness(0, 5, 0, 7),
        });
        XsrUiEntityId following = tree.Create("following");
        tree.SetComponent(following, new XsrUiElement { Height = 10 });
        tree.Attach(nested, root);
        tree.Attach(following, root);
        tree.Attach(inset, nested);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(0, 5, 800, 20), scene[2].Rect);
        AssertEqual(new XsrUiRect(0, 32, 800, 10), scene[3].Rect);
    }

    private static void ExplicitWidthConstrainsWrappedIntrinsicHeight()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId card = tree.Create("card");
        tree.SetComponent(card, new XsrUiElement { Width = 100 });
        tree.SetComponent(card, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId copy = tree.Create("copy");
        tree.SetComponent(copy, new XsrUiText(new string('x', 24)) { MaxLines = 2 });
        tree.SetComponent(copy, new XsrUiVisualStyle { FontSize = 14, WrapText = true });
        tree.Attach(copy, card);
        tree.Attach(card, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(0, 0, 100, 40), scene.Nodes.Single(node => node.Entity == card).Rect);
        AssertEqual(new XsrUiRect(0, 0, 100, 40), scene.Nodes.Single(node => node.Entity == copy).Rect);
    }

    private static void InvisibleEntitiesLeaveSceneAndLayout()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 10 });
        XsrUiEntityId hidden = tree.Create("hidden");
        tree.SetComponent(hidden, new XsrUiElement { Width = 100, Height = 40, IsVisible = false });
        XsrUiEntityId visible = tree.Create("visible");
        tree.SetComponent(visible, new XsrUiElement { Width = 100, Height = 40 });
        tree.Attach(hidden, root);
        tree.Attach(visible, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertEqual(2, scene.Count);
        AssertEqual(visible, scene[1].Entity);
        AssertEqual(new XsrUiRect(0, 0, 100, 40), scene[1].Rect);
    }

    private static void HiddenListMutationsInvalidateLayout()
    {
        XsrUiTree tree = new();
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build());
        XsrUiEntityId root = tree.Create("root"), panel = tree.Create("panel"), rows = tree.Create("rows");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        tree.SetComponent(panel, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        tree.SetComponent(rows, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiElement visibility = new() { Weight = 1 };
        tree.SetComponent(panel, visibility);
        tree.SetComponent(rows, new XsrUiElement { Weight = 1 });
        tree.Attach(panel, root); tree.Attach(rows, panel);
        renderer.SetRoot(root);
        _ = renderer.Render(); // caches an empty list
        visibility.IsVisible = false;
        tree.MarkDirty(panel, XsrUiDirtyKinds.Layout);
        _ = renderer.Render();
        XsrUiEntityId row = tree.Create("new row");
        tree.SetComponent(row, new XsrUiElement { Height = 56 });
        tree.Attach(row, rows);
        XsrUiScene hidden = renderer.Render();
        AssertFalse(hidden.Nodes.Any(node => node.Entity == row));
        AssertTrue(ReferenceEquals(hidden, renderer.Render())); // no hidden-dirt frame loop
        visibility.IsVisible = true;
        tree.MarkDirty(panel, XsrUiDirtyKinds.Layout);
        AssertEqual(56d, renderer.Render().Nodes.Single(node => node.Entity == row).Rect.Height);
        visibility.IsVisible = false;
        tree.MarkDirty(panel, XsrUiDirtyKinds.Layout);
        _ = renderer.Render();
        tree.Destroy(row);
        XsrUiEntityId replacement = tree.Create("replacement");
        tree.SetComponent(replacement, new XsrUiElement { Height = 72 });
        tree.Attach(replacement, rows);
        _ = renderer.Render();
        visibility.IsVisible = true;
        tree.MarkDirty(panel, XsrUiDirtyKinds.Layout);
        AssertEqual(72d, renderer.Render().Nodes.Single(node => node.Entity == replacement).Rect.Height);
    }

    private static void CleanTreeReturnsSameScene()
    {
        XsrUiRenderer renderer = BuildSingleLeafRenderer(out XsrUiScene first);

        XsrUiScene second = renderer.Render();

        AssertTrue(ReferenceEquals(first, second));
        AssertEqual(first.Version, second.Version);
    }

    private static void DirtyLeafRelayoutsOnlyItsSubtree()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Horizontal));
        XsrUiEntityId leftBranch = tree.Create("left");
        tree.SetComponent(leftBranch, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId rightBranch = tree.Create("right");
        tree.SetComponent(rightBranch, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId leftLeaf = tree.Create("left-leaf");
        tree.SetComponent(leftLeaf, new XsrUiElement { Width = 50, Height = 20 });
        XsrUiEntityId rightLeaf = tree.Create("right-leaf");
        tree.SetComponent(rightLeaf, new XsrUiElement { Width = 50, Height = 20 });
        tree.Attach(leftBranch, root);
        tree.Attach(rightBranch, root);
        tree.Attach(leftLeaf, leftBranch);
        tree.Attach(rightLeaf, rightBranch);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        _ = renderer.Render();
        AssertEqual(5, renderer.LastLayoutVisits);

        // Mutating one leaf relayouts the root chain and that leaf only.
        tree.SetComponent(leftLeaf, new XsrUiElement { Width = 90, Height = 20 });
        _ = renderer.Render();
        AssertEqual(3, renderer.LastLayoutVisits);
    }

    private static void SiblingRectsFollowSlotChanges()
    {
        // Reviewer regression: a clean sibling must re-arrange into its new slot even though it
        // is not dirty — measure caching must never cache arrange results.
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId alpha = tree.Create("alpha");
        XsrUiEntityId beta = tree.Create("beta");
        tree.SetComponent(alpha, new XsrUiElement { Width = 100, Height = 20 });
        tree.SetComponent(beta, new XsrUiElement { Width = 100, Height = 20 });
        tree.Attach(alpha, root);
        tree.Attach(beta, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene first = renderer.Render();
        AssertEqual(new XsrUiRect(0, 0, 100, 20), first[1].Rect);
        AssertEqual(new XsrUiRect(0, 20, 100, 20), first[2].Rect);

        tree.SetComponent(alpha, new XsrUiElement { Width = 100, Height = 40 });
        XsrUiScene second = renderer.Render();

        AssertEqual(new XsrUiRect(0, 0, 100, 40), second[1].Rect);
        AssertEqual(new XsrUiRect(0, 40, 100, 20), second[2].Rect);
        AssertTrue(second.Version > first.Version);

        // Only the changed chain re-measures: the clean sibling keeps its cached measurement.
        AssertEqual(2, renderer.LastLayoutVisits);
    }

    private static void ShrinkKeepsSiblingsCorrectToo()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 5 });
        XsrUiEntityId alpha = tree.Create("alpha");
        XsrUiEntityId beta = tree.Create("beta");
        tree.SetComponent(alpha, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(beta, new XsrUiElement { Width = 100, Height = 20 });
        tree.Attach(alpha, root);
        tree.Attach(beta, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        _ = renderer.Render();

        tree.SetComponent(alpha, new XsrUiElement { Width = 100, Height = 10 });
        XsrUiScene scene = renderer.Render();

        AssertEqual(new XsrUiRect(0, 0, 100, 10), scene[1].Rect);
        AssertEqual(new XsrUiRect(0, 15, 100, 20), scene[2].Rect);
    }

    private static void StateBoundTextRendersAppliedValue()
    {
        XsrUiTree tree = new();
        XsrStateStoreBuilder states = new();
        states.Cell<string>("ui.label".AsXsrId(), "Owner");
        XsrUiStateBridge bridge = new(tree);
        XsrStateStore store = states.Build(bridge);
        XsrStateId label = store.Resolve("ui.label".AsXsrId());

        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiText(string.Empty) { BoundState = label });
        XsrUiRenderer renderer = new(tree, store, stateBridge: bridge);
        renderer.SetRoot(root);

        _ = store.Publish(label, "hello");
        XsrUiScene first = renderer.Render();
        AssertEqual("hello", first[0].Text);

        _ = store.Publish(label, "world");
        XsrUiScene second = renderer.Render();
        AssertEqual("world", second[0].Text);
        AssertTrue(second.Version > first.Version);
    }

    private static void SceneOrderIsDepthFirstPreOrder()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId alpha = tree.Create("alpha");
        XsrUiEntityId beta = tree.Create("beta");
        XsrUiEntityId alphaChild = tree.Create("alpha-child");
        tree.SetComponent(alpha, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        tree.Attach(alpha, root);
        tree.Attach(beta, root);
        tree.Attach(alphaChild, alpha);
        tree.SetComponent(alphaChild, new XsrUiSemantic(XsrUiSemanticRole.Button, "OK"));

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();

        AssertSequence(
            new[] { root, alpha, alphaChild, beta },
            scene.Nodes.Select(node => node.Entity).ToArray());
        AssertEqual(0, scene[0].Depth);
        AssertEqual(1, scene[1].Depth);
        AssertEqual(2, scene[2].Depth);
        AssertEqual(1, scene[3].Depth);
        AssertEqual(XsrUiSemanticRole.Button, scene[2].Role);
        AssertEqual("OK", scene[2].Label);
    }

    private static void RenderWithoutRootThrows()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiRenderer renderer = new(tree, store);

        AssertThrows<InvalidOperationException>(() => renderer.Render());
    }

    private static void ViewportChangeRelayouts()
    {
        XsrUiRenderer renderer = BuildSingleLeafRenderer(out XsrUiScene first);
        AssertEqual(200, first[0].Rect.Width);

        renderer.Viewport = new XsrUiSize(400, 300);
        XsrUiScene second = renderer.Render();

        AssertEqual(new XsrUiRect(0, 0, 200, 100), second[0].Rect);
        AssertTrue(second.Version > first.Version);
    }

    private static XsrUiRenderer BuildSingleLeafRenderer(out XsrUiScene scene)
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("leaf");
        tree.SetComponent(root, new XsrUiElement { Width = 200, Height = 100 });
        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        scene = renderer.Render();
        return renderer;
    }
}
