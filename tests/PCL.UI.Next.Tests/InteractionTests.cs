using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static void HitTestReturnsTopMostEntity()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId bottom = tree.Create("bottom");
        XsrUiEntityId top = tree.Create("top");
        tree.SetComponent(bottom, new XsrUiElement { Width = 200, Height = 100 });
        tree.SetComponent(top, new XsrUiElement { Width = 100, Height = 100 });
        tree.Attach(bottom, root);
        tree.Attach(top, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        renderer.Render();

        // Without a stack component, children overlap; the later entity draws above.
        AssertEqual(top, renderer.HitTest(new XsrUiPoint(50, 50)));
        AssertEqual(bottom, renderer.HitTest(new XsrUiPoint(150, 50)));
        // The background root covers the whole viewport, so a miss resolves to it.
        AssertEqual(root, renderer.HitTest(new XsrUiPoint(500, 500)));
    }

    private static void PointerActivationEmitsCommandIntent()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId button = tree.Create("button");
        tree.SetComponent(button, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(button, new XsrUiInput { Clickable = true });
        tree.SetComponent(button, new XsrUiCommandBinding("app.save".AsXsrId()));
        tree.SetComponent(button, new XsrUiSemantic(XsrUiSemanticRole.Button, "Save"));
        tree.Attach(button, root);

        XsrUiRenderer renderer = new(tree, store, intents);
        renderer.SetRoot(root);
        renderer.Render();

        AssertTrue(renderer.PointerPressed(new XsrUiPoint(50, 20)));
        AssertTrue(renderer.PointerReleased(new XsrUiPoint(50, 20)));

        AssertEqual(1, intents.Count);
        (XsrSemanticId Command, XsrUiEntityId Source, XsrCorrelationId CorrelationId) intent = intents.Drain()[0];
        AssertEqual("app.save", intent.Command.ToString());
        AssertEqual(button, intent.Source);
        AssertTrue(intent.CorrelationId.IsAssigned);
    }

    private static void PointerPressOnNonClickableIsNotHandled()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId label = tree.Create("label");
        tree.SetComponent(label, new XsrUiElement { Width = 100, Height = 40 });
        tree.Attach(label, root);

        XsrUiRenderer renderer = new(tree, store, intents);
        renderer.SetRoot(root);
        renderer.Render();

        AssertFalse(renderer.PointerPressed(new XsrUiPoint(50, 20)));
        AssertFalse(renderer.PointerReleased(new XsrUiPoint(50, 20)));
        AssertEqual(0, intents.Count);
    }

    private static void PointerReleaseOutsideDoesNotActivate()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId button = tree.Create("button");
        tree.SetComponent(button, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(button, new XsrUiInput { Clickable = true });
        tree.SetComponent(button, new XsrUiCommandBinding("app.save".AsXsrId()));
        tree.Attach(button, root);

        XsrUiRenderer renderer = new(tree, store, intents);
        renderer.SetRoot(root);
        renderer.Render();

        AssertTrue(renderer.PointerPressed(new XsrUiPoint(50, 20)));
        AssertFalse(renderer.PointerReleased(new XsrUiPoint(500, 500)));
        AssertEqual(0, intents.Count);
    }

    private static void PointerMoveTracksHover()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId alpha = tree.Create("alpha");
        XsrUiEntityId beta = tree.Create("beta");
        tree.SetComponent(alpha, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(alpha, new XsrUiInput { Clickable = true });
        tree.SetComponent(beta, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(beta, new XsrUiInput { Clickable = true });
        tree.Attach(alpha, root);
        tree.Attach(beta, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        renderer.Render();

        AssertTrue(renderer.PointerMoved(new XsrUiPoint(50, 20)));
        XsrUiInput? alphaInput = tree.GetComponent<XsrUiInput>(alpha);
        AssertTrue(alphaInput!.IsHovered);

        // The same point keeps hovering without further changes.
        AssertTrue(renderer.PointerMoved(new XsrUiPoint(50, 20)));

        AssertTrue(renderer.PointerMoved(new XsrUiPoint(50, 60)));
        AssertFalse(alphaInput.IsHovered);
        AssertTrue(tree.GetComponent<XsrUiInput>(beta)!.IsHovered);

        // Moving over a non-input entity clears hover.
        AssertFalse(renderer.PointerMoved(new XsrUiPoint(500, 500)));
        AssertFalse(tree.GetComponent<XsrUiInput>(beta)!.IsHovered);
    }

    private static void FocusCyclesThroughFocusableEntities()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId first = tree.Create("first");
        XsrUiEntityId middle = tree.Create("middle");
        XsrUiEntityId last = tree.Create("last");
        foreach (XsrUiEntityId entity in new[] { first, middle, last })
        {
            tree.SetComponent(entity, new XsrUiElement { Width = 100, Height = 40 });
            tree.Attach(entity, root);
        }

        tree.SetComponent(first, new XsrUiInput { Focusable = true });
        tree.SetComponent(middle, new XsrUiInput { Clickable = true });
        tree.SetComponent(last, new XsrUiInput { Focusable = true });

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        renderer.Render();

        AssertTrue(renderer.FocusNext());
        AssertEqual(first, renderer.Focused);
        AssertTrue(renderer.FocusNext());
        AssertEqual(last, renderer.Focused);
        AssertTrue(renderer.FocusNext());
        AssertEqual(first, renderer.Focused);

        // Focusing a non-focusable entity is rejected.
        AssertFalse(renderer.Focus(middle));
        AssertEqual(first, renderer.Focused);
    }

    private static void KeyboardActivationEmitsIntent()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId button = tree.Create("button");
        tree.SetComponent(button, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(button, new XsrUiInput { Focusable = true });
        tree.SetComponent(button, new XsrUiCommandBinding("app.submit".AsXsrId()));
        tree.Attach(button, root);

        XsrUiRenderer renderer = new(tree, store, intents);
        renderer.SetRoot(root);
        renderer.Render();

        AssertTrue(renderer.FocusNext());
        AssertTrue(renderer.HandleKey(XsrUiKey.Enter));
        AssertTrue(renderer.HandleKey(XsrUiKey.Space));
        AssertFalse(renderer.HandleKey(XsrUiKey.Back));
        AssertEqual(2, intents.Count);
        _ = intents.Drain();
    }

    private static void FocusedEntityIsVisibleInTheScene()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId button = tree.Create("button");
        tree.SetComponent(button, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(button, new XsrUiInput { Focusable = true });
        tree.Attach(button, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        XsrUiScene unfocused = renderer.Render();
        AssertFalse(unfocused[1].IsFocused);

        AssertTrue(renderer.FocusNext());
        XsrUiScene focused = renderer.Render();
        AssertTrue(focused[1].IsFocused);
        AssertTrue(focused.Version > unfocused.Version);
    }

    private static void NavigatorPushPopAndReplaceSwapPages()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        RecordingNavigatorObserver observer = new();
        XsrUiEntityId host = tree.Create("host");
        XsrUiNavigator navigator = new(tree, host, observer);
        List<XsrUiNavigationRecord> events = observer.Events;

        XsrUiEntityId first = BuildPage(tree, "first");
        XsrUiEntityId second = BuildPage(tree, "second");
        XsrUiEntityId third = BuildPage(tree, "third");

        navigator.Push(first);
        AssertEqual(first, navigator.Current);
        AssertEqual(1, navigator.Depth);

        navigator.Push(second);
        AssertEqual(second, navigator.Current);
        AssertEqual(2, navigator.Depth);
        AssertFalse(tree.Children(host).Contains(first));

        AssertTrue(navigator.Pop());
        AssertEqual(first, navigator.Current);
        AssertTrue(tree.Children(host).Contains(first));
        AssertFalse(navigator.Pop());

        navigator.Push(second);
        navigator.Replace(third);
        AssertEqual(third, navigator.Current);
        AssertEqual(2, navigator.Depth);
        AssertTrue(navigator.Pop());
        AssertEqual(first, navigator.Current);

        AssertEqual(6, events.Count);
        AssertEqual(XsrUiNavigationKind.Push, events[0].Kind);
        AssertEqual(XsrUiNavigationKind.Pop, events[2].Kind);
        AssertEqual(XsrUiNavigationKind.Replace, events[4].Kind);
        AssertEqual(XsrUiNavigationKind.Pop, events[5].Kind);
    }

    private static void NavigatorRejectsUnknownPages()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId host = tree.Create("host");
        XsrUiNavigator navigator = new(tree, host);

        AssertThrows<InvalidOperationException>(() => navigator.Push(new XsrUiEntityId(999, 1)));
        AssertFalse(navigator.Pop());

        XsrUiEntityId page = BuildPage(tree, "page");
        navigator.Push(page);
        AssertThrows<InvalidOperationException>(() => navigator.Push(page));
    }

    private static void StageOverlaysDrawAbovePage()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiStage stage = new(tree, store);

        XsrUiEntityId page = BuildPage(tree, "page-content");
        stage.Navigation.Push(page);
        XsrUiEntityId dialog = tree.Create("dialog");
        tree.SetComponent(dialog, new XsrUiText("dialog-content"));
        tree.SetComponent(dialog, new XsrUiSemantic(XsrUiSemanticRole.Dialog, "Confirm"));
        stage.Show(dialog);

        XsrUiScene scene = stage.Renderer.Render();

        AssertEqual(4, scene.Count);
        AssertEqual(stage.Root, scene[0].Entity);
        AssertEqual(ContentHostEntity(tree, stage), scene[1].Entity);
        AssertEqual("page-content", scene[2].Text);
        AssertEqual("dialog-content", scene[3].Text);
        AssertEqual(XsrUiSemanticRole.Dialog, scene[3].Role);
        AssertSequence(new[] { dialog }, stage.Overlays.ToArray());
    }

    private static void StageDismissRemovesTopOverlay()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiStage stage = new(tree, store);
        XsrUiEntityId page = BuildPage(tree, "page");
        stage.Navigation.Push(page);
        XsrUiEntityId first = tree.Create("first-overlay");
        XsrUiEntityId second = tree.Create("second-overlay");
        stage.Show(first);
        stage.Show(second);

        AssertTrue(stage.DismissTop());
        AssertSequence(new[] { first }, stage.Overlays.ToArray());
        AssertFalse(tree.Children(stage.Root).Contains(second));

        AssertTrue(stage.Dismiss(first));
        AssertFalse(stage.Dismiss(first));
        AssertEqual(0, stage.Overlays.Count);
    }

    private static void StageNavigationSwapsPageContent()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiStage stage = new(tree, store);

        XsrUiEntityId first = BuildPage(tree, "first-page");
        XsrUiEntityId second = BuildPage(tree, "second-page");
        stage.Navigation.Push(first);
        AssertEqual("first-page", stage.Renderer.Render()[2].Text);

        stage.Navigation.Push(second);
        XsrUiScene scene = stage.Renderer.Render();
        AssertEqual("second-page", scene[2].Text);
    }

    private static void ReducedMotionIsAPresentationContractFlag()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiRenderer renderer = new(tree, store);
        AssertFalse(renderer.ReducedMotion);
        renderer.ReducedMotion = true;
        AssertTrue(renderer.ReducedMotion);
    }

    private sealed class RecordingNavigatorObserver : IXsrUiNavigatorObserver
    {
        public List<XsrUiNavigationRecord> Events { get; } = [];

        public void OnNavigated(XsrUiNavigationRecord args) => Events.Add(args);
    }

    private static XsrUiEntityId BuildPage(XsrUiTree tree, string content)
    {
        XsrUiEntityId page = tree.Create($"page:{content}");
        tree.SetComponent(page, new XsrUiText(content));
        return page;
    }

    private static XsrUiEntityId ContentHostEntity(XsrUiTree tree, XsrUiStage stage)
    {
        foreach (XsrUiEntityId child in tree.Children(stage.Root))
        {
            if (child.Equals(stage.ContentHost))
            {
                return child;
            }
        }

        throw new InvalidOperationException("The content host is missing from the stage root.");
    }
}
