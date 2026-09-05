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

        // The same point keeps hovering without another presentation change.
        AssertFalse(renderer.PointerMoved(new XsrUiPoint(50, 20)));

        AssertTrue(renderer.PointerMoved(new XsrUiPoint(50, 60)));
        AssertFalse(alphaInput.IsHovered);
        AssertTrue(tree.GetComponent<XsrUiInput>(beta)!.IsHovered);

        // Moving over a non-input entity clears hover.
        AssertTrue(renderer.PointerMoved(new XsrUiPoint(500, 500)));
        AssertFalse(tree.GetComponent<XsrUiInput>(beta)!.IsHovered);
    }

    private static void PointerLeavingInputRequestsRepaint()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId button = tree.Create("button");
        tree.SetComponent(button, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(button, new XsrUiInput { Clickable = true });
        tree.Attach(button, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        _ = renderer.Render();

        AssertTrue(renderer.PointerMoved(new XsrUiPoint(50, 20)));
        AssertTrue(renderer.Render()[1].IsHovered);

        // The root is a non-input target. Clearing the previous hover still requires a frame.
        AssertTrue(renderer.PointerMoved(new XsrUiPoint(500, 500)));
        AssertFalse(renderer.Render()[1].IsHovered);
    }

    private static void PointerExitedClearsHoverImmediately()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId button = tree.Create("button");
        tree.SetComponent(button, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(button, new XsrUiInput { Clickable = true });
        tree.Attach(button, root);

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        _ = renderer.Render();

        AssertTrue(renderer.PointerMoved(new XsrUiPoint(50, 20)));
        AssertTrue(renderer.Render()[1].IsHovered);

        // Avalonia maps PointerExited to an out-of-scene point.
        AssertTrue(renderer.PointerMoved(new XsrUiPoint(-1, -1)));
        AssertFalse(renderer.Render()[1].IsHovered);
    }

    private static void PointerCursorFollowsInteractiveTargets()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId button = tree.Create("button");
        XsrUiEntityId textInput = tree.Create("text-input");
        XsrUiEntityId disabled = tree.Create("disabled");
        foreach (XsrUiEntityId entity in new[] { button, textInput, disabled })
        {
            tree.SetComponent(entity, new XsrUiElement { Width = 120, Height = 40 });
            tree.Attach(entity, root);
        }
        tree.SetComponent(button, new XsrUiInput { Clickable = true });
        tree.SetComponent(textInput, new XsrUiInput { Focusable = true });
        tree.SetComponent(textInput, new XsrUiTextInput());
        tree.SetComponent(disabled, new XsrUiInput { Clickable = true, Enabled = false });

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        _ = renderer.Render();

        AssertEqual(XsrUiPointerCursor.Hand, renderer.PointerCursorAt(new XsrUiPoint(50, 20)));
        AssertEqual(XsrUiPointerCursor.Text, renderer.PointerCursorAt(new XsrUiPoint(50, 60)));
        AssertEqual(XsrUiPointerCursor.Default, renderer.PointerCursorAt(new XsrUiPoint(50, 100)));
        AssertEqual(XsrUiPointerCursor.Default, renderer.PointerCursorAt(new XsrUiPoint(500, 500)));
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

    private static void StageOverlaysStayOutOfStackFlow()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiStage stage = new(tree, store);
        tree.SetComponent(stage.Root, new XsrUiStackPanel(XsrUiOrientation.Vertical)
        {
            StretchLastChild = true,
        });

        XsrUiEntityId page = BuildPage(tree, "page");
        stage.Navigation.Push(page);
        XsrUiEntityId notification = tree.Create("notification");
        tree.SetComponent(notification, new XsrUiElement
        {
            Width = 100,
            Height = 30,
            HorizontalAlignment = XsrUiAlignment.Start,
            VerticalAlignment = XsrUiAlignment.End,
        });
        tree.SetComponent(notification, new XsrUiSemantic(XsrUiSemanticRole.Status, "Info: saved"));
        tree.SetComponent(notification, new XsrUiLiveRegion(XsrUiLiveSetting.Polite));
        tree.SetComponent(notification, new XsrUiOverlayMotion(XsrUiOverlayMotionKind.Notification));
        XsrUiEntityId message = tree.Create("notification-message");
        tree.SetComponent(message, new XsrUiText("saved"));
        tree.Attach(message, notification);
        stage.Show(notification);

        XsrUiScene scene = stage.Renderer.Render();
        XsrUiSceneNode host = scene.Nodes.Single(node => node.Entity == stage.ContentHost);
        XsrUiSceneNode notice = scene.Nodes.Single(node => node.Entity == notification);
        XsrUiSceneNode copy = scene.Nodes.Single(node => node.Entity == message);
        AssertClose(600, host.Rect.Height);
        AssertEqual(new XsrUiRect(0, 570, 100, 30), notice.Rect);
        AssertEqual(XsrUiLiveSetting.Polite, notice.LiveSetting);
        AssertEqual(XsrUiOverlayMotionKind.Notification, notice.OverlayMotion);
        AssertEqual(XsrUiOverlayMotionKind.Notification, copy.OverlayMotion);
        AssertEqual<XsrUiRect?>(notice.Rect, copy.OverlayAnchor);
    }

    private static void NonModalOverlayWhitespacePassesPointerInput()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiStage stage = new(tree, store, intents);
        XsrUiEntityId pageButton = tree.Create("page-button");
        tree.SetComponent(pageButton, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(pageButton, new XsrUiSemantic(XsrUiSemanticRole.Button, "Page button"));
        tree.SetComponent(pageButton, new XsrUiInput { Clickable = true });
        tree.SetComponent(pageButton, new XsrUiCommandBinding("page.activate".AsXsrId()));
        stage.Navigation.Push(pageButton);
        XsrUiEntityId emptyOverlay = tree.Create("empty-overlay");
        tree.SetComponent(emptyOverlay, new XsrUiElement { Width = 100, Height = 40 });
        stage.Show(emptyOverlay);

        stage.Renderer.Viewport = new XsrUiSize(200, 100);
        _ = stage.Renderer.Render();
        AssertEqual(emptyOverlay, stage.Renderer.HitTest(new XsrUiPoint(20, 20)));
        AssertTrue(stage.Renderer.PointerPressed(new XsrUiPoint(20, 20)));
        AssertTrue(stage.Renderer.PointerReleased(new XsrUiPoint(20, 20)));
        AssertEqual("page.activate", intents.Drain().Single().Command.ToString());
    }

    private static void ClosingNotificationLeavesStackFlowImmediately()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId root = tree.Create("notice-stack");
        tree.SetComponent(root, new XsrUiElement { Width = 100, Height = 200 });
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 10 });
        XsrUiEntityId first = CreateNotice("first");
        XsrUiEntityId second = CreateNotice("second");
        tree.Attach(first, root);
        tree.Attach(second, root);
        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        renderer.Viewport = new XsrUiSize(100, 200);

        XsrUiScene before = renderer.Render();
        AssertEqual(50, before.Nodes.Single(node => node.Entity == second).Rect.Y);
        tree.GetComponent<XsrUiOverlayMotion>(first)!.IsClosing = true;
        tree.MarkDirty(first, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);

        XsrUiScene after = renderer.Render();
        AssertEqual(0, after.Nodes.Single(node => node.Entity == second).Rect.Y);

        XsrUiEntityId CreateNotice(string name)
        {
            XsrUiEntityId notice = tree.Create(name);
            tree.SetComponent(notice, new XsrUiElement { Width = 100, Height = 40 });
            tree.SetComponent(notice, new XsrUiOverlayMotion(XsrUiOverlayMotionKind.Notification));
            return notice;
        }
    }

    private static void ModalOverlaysIsolatePageAndRouteEscape()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiStage stage = new(tree, store, intents);
        XsrUiEntityId page = tree.Create("page-action");
        tree.SetComponent(page, new XsrUiElement { Width = 120, Height = 40 });
        tree.SetComponent(page, new XsrUiSemantic(XsrUiSemanticRole.Button, "Page action"));
        tree.SetComponent(page, new XsrUiInput { Clickable = true, Focusable = true });
        tree.SetComponent(page, new XsrUiCommandBinding("page.activate".AsXsrId()));
        stage.Navigation.Push(page);
        _ = stage.Renderer.Render();
        AssertTrue(stage.Renderer.Focus(page, showIndicator: true));

        XsrUiEntityId dialog = tree.Create("dialog-layer");
        tree.SetComponent(dialog, new XsrUiSemantic(XsrUiSemanticRole.Dialog, "Confirm"));
        tree.SetComponent(dialog, new XsrUiDismissBinding("dialog.cancel".AsXsrId()));
        stage.Show(dialog, modal: true);

        XsrUiScene scene = stage.Renderer.Render();
        XsrUiSceneNode pageNode = scene.Nodes.Single(node => node.Entity == page);
        XsrUiSceneNode dialogNode = scene.Nodes.Single(node => node.Entity == dialog);
        AssertFalse(pageNode.IsAccessible);
        AssertFalse(pageNode.IsClickable);
        AssertTrue(dialogNode.IsAccessible);
        AssertFalse(stage.Renderer.Activate(page));
        AssertTrue(stage.Renderer.HandleKey(XsrUiKey.Escape));
        AssertEqual(1, intents.Count);
        var intent = intents.Drain().Single();
        AssertEqual("dialog.cancel", intent.Command.ToString());
        AssertEqual(dialog, intent.Source);
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
