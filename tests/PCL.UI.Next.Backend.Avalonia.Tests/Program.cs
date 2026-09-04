using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCL.UI.Next;
using PCL.UI.Next.Backend.Avalonia;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static readonly (string Name, Action Body)[] TestCases =
    [
        ("automation invoke and focus route through the renderer", AutomationInvokeAndFocusRouteThroughRenderer),
        ("navigation peers expose selection and route selection through invoke", NavigationPeersExposeSelectionAndRouteSelection),
        ("selection and hover facts present under reduced motion", SelectionAndHoverFactsPresentUnderReducedMotion),
        ("capsules respond to focus press and disabled state", CapsulesRespondToFocusPressAndDisabledState),
        ("capsule spring is no bounce and preserves reversal velocity", CapsuleSpringPreservesReversalVelocity),
        ("lifetime: splash never owns the process and main window close terminates", LifetimeSplashNeverOwnsProcessAndMainWindowCloseTerminates),
    ];

    private static int Main()
    {
        foreach ((string name, Action body) in TestCases)
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"Avalonia scene backend tests passed: {TestCases.Length}.");
        return 0;
    }

    private static void AutomationInvokeAndFocusRouteThroughRenderer()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId button = tree.Create("button");
        tree.SetComponent(button, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(button, new XsrUiInput { Focusable = true, Clickable = true });
        tree.SetComponent(button, new XsrUiCommandBinding(XsrSemanticId.Parse("app.save")));
        tree.SetComponent(button, new XsrUiSemantic(XsrUiSemanticRole.Button, "Save"));
        tree.Attach(button, root);

        XsrUiRenderer renderer = new(tree, store, intents);
        renderer.SetRoot(root);
        XsrUiScene scene = renderer.Render();
        AvaloniaUiSceneNodeControl control = new(
            entity => _ = renderer.Focus(entity),
            entity =>
            {
                _ = renderer.Focus(entity);
                _ = renderer.Activate(entity);
            },
            reducedMotion: () => true);
        control.Apply(Node(scene, button));

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(control);
        peer.SetFocus();
        AssertEqual(button, renderer.Focused);

        IInvokeProvider invoke = AssertNotNull(peer.GetProvider<IInvokeProvider>());
        invoke.Invoke();
        AssertEqual(1, intents.Count);
        AssertEqual(button, intents.Drain()[0].Source);

        AvaloniaUiSceneNodeControl text = new(_ => { }, _ => { }, reducedMotion: () => true);
        text.Apply(new XsrUiSceneNode(
            tree.Create("text"),
            new XsrUiRect(0, 0, 10, 10),
            0,
            XsrUiSemanticRole.Text,
            "Status",
            "ready",
            null,
            false,
            null,
            null));
        AssertTrue(ControlAutomationPeer.CreatePeerForElement(text).GetProvider<IInvokeProvider>() is null);
    }

    private static void NavigationPeersExposeSelectionAndRouteSelection()
    {
        XsrUiTree tree = new();
        XsrUiEntityId navigationEntity = tree.Create("navigation");
        XsrUiEntityId selectedEntity = tree.Create("selected");
        XsrUiEntityId otherEntity = tree.Create("other");
        int focusCount = 0;
        int invokeCount = 0;

        AvaloniaUiSceneNodeControl navigation = new(_ => focusCount++, _ => invokeCount++, reducedMotion: () => true);
        AvaloniaUiSceneNodeControl selected = new(_ => focusCount++, _ => invokeCount++, reducedMotion: () => true);
        AvaloniaUiSceneNodeControl other = new(_ => focusCount++, _ => invokeCount++, reducedMotion: () => true);
        navigation.Apply(Node(navigationEntity, XsrUiSemanticRole.Navigation));
        selected.Apply(Node(
            selectedEntity,
            XsrUiSemanticRole.NavigationItem,
            selected: true,
            focusable: true,
            clickable: true));
        other.Apply(Node(
            otherEntity,
            XsrUiSemanticRole.NavigationItem,
            focusable: true,
            clickable: true));
        selected.SetSelectionContainer(navigation);
        other.SetSelectionContainer(navigation);
        navigation.AddSelectionItem(selected);
        navigation.AddSelectionItem(other);

        ISelectionProvider selection = AssertNotNull(
            ControlAutomationPeer.CreatePeerForElement(navigation).GetProvider<ISelectionProvider>());
        ISelectionItemProvider selectedItem = AssertNotNull(
            ControlAutomationPeer.CreatePeerForElement(selected).GetProvider<ISelectionItemProvider>());

        AssertFalse(selection.CanSelectMultiple);
        AssertTrue(selection.IsSelectionRequired);
        AssertEqual(1, selection.GetSelection().Count);
        AssertTrue(selectedItem.IsSelected);
        AssertTrue(ReferenceEquals(selection, selectedItem.SelectionContainer));

        selectedItem.Select();
        AssertEqual(1, invokeCount);
        AssertEqual(0, focusCount);
    }

    private static void CapsuleSpringPreservesReversalVelocity()
    {
        (double position, double velocity) = AvaloniaUiMotion.StepCriticalSpring(0, 0, 1, .08, .3);
        AssertTrue(position > 0 && position < 1 && velocity > 0);
        (double reversedPosition, double reversedVelocity) = AvaloniaUiMotion.StepCriticalSpring(position, velocity, 0, 0, .3);
        AssertEqual(position, reversedPosition);
        AssertEqual(velocity, reversedVelocity);
        (double settled, double speed) = AvaloniaUiMotion.StepCriticalSpring(position, velocity, 0, 1, .3);
        AssertTrue(Math.Abs(settled) < .0001 && Math.Abs(speed) < .001);
        for (int i = 0; i < 100; i++)
        {
            (double value, _) = AvaloniaUiMotion.StepCriticalSpring(0, 0, 1, i * .01, .3);
            AssertTrue(value >= 0 && value <= 1);
        }
    }

    private static void CapsulesRespondToFocusPressAndDisabledState()
    {
        XsrUiTree tree = new();
        XsrUiEntityId entity = tree.Create("capsule");
        AvaloniaUiSceneNodeControl control = new(_ => { }, _ => { }, reducedMotion: () => true);
        XsrUiSceneNode node = Node(entity, XsrUiSemanticRole.Button, focusable: true, clickable: true) with
        {
            Text = "版本列表",
            Label = "打开版本列表",
            VisualStyle = new XsrUiVisualStyle { HoverExpand = true }.Snapshot(),
        };
        control.Apply(node);
        AssertEqual(0, control.PresentedPillExpand);
        control.Apply(node with { IsFocused = true });
        AssertEqual(0, control.PresentedPillExpand);
        AssertTrue(control.FocusAdorner is null);
        control.Apply(node with { IsFocused = true, IsFocusVisible = true, CapsuleExpansionProgress = 1 });
        AssertEqual(1, control.PresentedPillExpand);
        control.Apply(node with { IsHovered = true, CapsuleExpansionProgress = 1 });
        AssertEqual(1, control.PresentedPillExpand);
        control.Apply(node with { IsHovered = true, IsEnabled = false });
        AssertEqual(0, control.PresentedPillExpand);
        AssertFalse(control.IsEnabled);
        control.Apply(node);
        AssertEqual(0, control.PresentedPillExpand);
        AssertTrue(control.IsEnabled);
    }

    private static void SelectionAndHoverFactsPresentUnderReducedMotion()
    {
        // Reduced motion applies every scene fact immediately, so the assertions below are
        // synchronous even though the animated path eases the same values.
        XsrUiTree tree = new();
        XsrUiEntityId itemEntity = tree.Create("item");
        AvaloniaUiSceneNodeControl item = new(_ => { }, _ => { }, reducedMotion: () => true);

        item.Apply(Node(itemEntity, XsrUiSemanticRole.NavigationItem, selected: true, focusable: true, clickable: true));
        AssertEqual(1, item.PresentedPillScale);
        AssertEqual(0, item.PresentedHoverOpacity);

        item.Apply(Node(itemEntity, XsrUiSemanticRole.NavigationItem, focusable: true, clickable: true));
        AssertEqual(0, item.PresentedPillScale);

        item.Apply(Node(
            itemEntity,
            XsrUiSemanticRole.NavigationItem,
            selected: false,
            focusable: true,
            clickable: true,
            hovered: true));
        AssertEqual(1, item.PresentedHoverOpacity);

        item.Apply(Node(itemEntity, XsrUiSemanticRole.NavigationItem, focusable: true, clickable: true));
        AssertEqual(0, item.PresentedHoverOpacity);
        AssertEqual(0, item.PresentedPillScale);
    }

    // A 1x1 transparent PNG so the splash has a decodable icon without an asset dependency.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static MemoryStream TinyPng() => new MemoryStream(Convert.FromBase64String(TinyPngBase64));

    private sealed class LifetimeProbeApp : Application
    {
        public static Func<Stream?>? SplashIconFactory { get; set; }

        public static ShutdownMode ObservedShutdownMode { get; private set; }

        public static Window? ObservedMainWindow { get; private set; }

        public static int ObservedWindowCount { get; private set; }

        public static bool ReachedEndOfLifetime { get; private set; }

        public static bool RunRailReducedMotionScenario { get; set; }

        public static double ObservedRailProgressAfterCollapse { get; internal set; } = double.NaN;

        public static double ObservedPageEnterProgressAfterReducedMotion { get; internal set; } = double.NaN;

        public static bool ObservedPageEnterStarted { get; internal set; }

        public static bool ObservedAllStaggeredChildrenSettled { get; internal set; } = true;
        public static Exception? Failure { get; internal set; }

        public static void MarkTerminated() => ReachedEndOfLifetime = true;

        public static void Reset(bool withSplash)
        {
            SplashIconFactory = withSplash ? TinyPng : (Func<Stream?>?)null;
            ObservedShutdownMode = default;
            ObservedMainWindow = null;
            ObservedWindowCount = 0;
            ReachedEndOfLifetime = false;
            RunRailReducedMotionScenario = false;
            ObservedRailProgressAfterCollapse = double.NaN;
            ObservedPageEnterProgressAfterReducedMotion = double.NaN;
            ObservedPageEnterStarted = false;
            ObservedAllStaggeredChildrenSettled = true;
            Failure = null;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                XsrStateStore store = new XsrStateStoreBuilder().Build();
                XsrUiShell shell = XsrUiShellComposer.Compose(store);
                // Reduced motion keeps the startup handoff synchronous for the test.
                shell.Renderer.ReducedMotion = true;
                AvaloniaUiShellWindow window = AvaloniaUiShellLifetime.Compose(
                    desktop,
                    shell,
                    SplashIconFactory?.Invoke(),
                    null);

                ObservedShutdownMode = desktop.ShutdownMode;
                ObservedMainWindow = desktop.MainWindow;
                ObservedWindowCount = desktop.Windows.Count;

                if (RunRailReducedMotionScenario)
                {
                    _ = RunMotionScenariosAsync(window, shell, window.Surface);
                    return;
                }

                // Closing the main window exercises the automatic lifetime contract: the run
                // must terminate and return to the caller.
                Dispatcher.UIThread.Post(
                    () => window.Close(),
                    DispatcherPriority.Background);
            }

            base.OnFrameworkInitializationCompleted();
        }


    }

    private static void LifetimeSplashNeverOwnsProcessAndMainWindowCloseTerminates()
    {
        LifetimeProbeApp.Reset(withSplash: true);
        LifetimeProbeApp.RunRailReducedMotionScenario = true;
        AppBuilder.Configure<LifetimeProbeApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .StartWithClassicDesktopLifetime([]);

        // Reaching this line at all proves the lifetime terminated on main-window close instead
        // of leaving the process running under OnExplicitShutdown.
        LifetimeProbeApp.MarkTerminated();
        if (LifetimeProbeApp.Failure is { } failure) throw new InvalidOperationException("Native bridge scenario failed.", failure);
        AssertTrue(LifetimeProbeApp.ReachedEndOfLifetime);
        AssertEqual(ShutdownMode.OnMainWindowClose, LifetimeProbeApp.ObservedShutdownMode);
        AssertTrue(LifetimeProbeApp.ObservedMainWindow is AvaloniaUiShellWindow);
        // Under reduced motion the handoff is synchronous: the splash closed the moment the
        // shell window took over the icon, leaving exactly the shell window alive — and its
        // close did not terminate the process early, proving the splash never owned the
        // lifetime.
        AssertEqual(1, LifetimeProbeApp.ObservedWindowCount);

        // ReducedMotionCancelsRunningRailMotion: after the mid-flight policy flip and the
        // collapse, the settled progress must still be the collapsed fact.
        AssertEqual(0, LifetimeProbeApp.ObservedRailProgressAfterCollapse);

        // ReducedMotionSettlesRunningPageEnter: the page enter animation started and the
        // mid-flight policy flip settled it to the final state.
        AssertTrue(LifetimeProbeApp.ObservedPageEnterStarted);
        AssertEqual(1, LifetimeProbeApp.ObservedPageEnterProgressAfterReducedMotion);
        AssertTrue(LifetimeProbeApp.ObservedAllStaggeredChildrenSettled);
    }

    private static async Task RunMotionScenariosAsync(
        AvaloniaUiShellWindow window,
        XsrUiShell shell,
        AvaloniaUiSceneSurface surface)
    {
        try
        {
            await Task.Delay(30).ConfigureAwait(true);
            VerifyAccessibleContentAndNativeFocus(window, shell, surface);
            VerifyNativeTextEditing(window, shell, surface);
            await VerifyTransitionGroupsAndMedia(shell, surface);
            VerifyWindowActionFeedback(window, surface);
            await VerifySpringIgnoresStaleSceneReads().ConfigureAwait(true);
            await VerifyCapsuleGeometryClock(shell, surface).ConfigureAwait(true);
            await VerifyPagerNativeDragAndClock(window, shell, surface).ConfigureAwait(true);
            // ReducedMotionCancelsRunningRailMotion: start the expansion normally, flip the
            // policy mid-flight, then collapse. The shell snaps the progress to the collapsed
            // fact and the running track must never write the expansion back over it.
            shell.SetNavigationExpanded(true);
            await Task.Delay(30).ConfigureAwait(true);
            shell.Renderer.ReducedMotion = true;
            shell.SetNavigationExpanded(false);
            await Task.Delay(300).ConfigureAwait(true);
            LifetimeProbeApp.ObservedRailProgressAfterCollapse = shell.RailPresentationProgress;

            // ReducedMotionSettlesRunningPageEnter: build a page outside the navigator, swap
            // it in, and confirm the enter animation started; flipping the policy then settles
            // the running enter track to its final state.
            shell.Renderer.ReducedMotion = false;
            XsrUiEntityId page = shell.Tree.Create("scenario-page");
            shell.Tree.SetComponent(page, new XsrUiStackPanel(XsrUiOrientation.Vertical));
            shell.Tree.SetComponent(page, new XsrUiSemantic(XsrUiSemanticRole.Page, "scenario"));
            List<XsrUiEntityId> scenarioTexts = [];
            for (int index = 0; index < 8; index++)
            {
                XsrUiEntityId text = shell.Tree.Create($"scenario-page-text-{index}");
                shell.Tree.SetComponent(text, new XsrUiText($"场景页面 {index}"));
                shell.Tree.SetComponent(text, new XsrUiSemantic(XsrUiSemanticRole.Text, "场景页面"));
                shell.Tree.Attach(text, page);
                scenarioTexts.Add(text);
            }

            shell.Stage.Navigation.Replace(page);

            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            double started = 1;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(16).ConfigureAwait(true);
                if (surface.TryGetPresentedEnterProgress(scenarioTexts[0], out started) && started < 1)
                {
                    break;
                }
            }

            LifetimeProbeApp.ObservedPageEnterStarted = started < 1;

            shell.Renderer.ReducedMotion = true;
            deadline = DateTime.UtcNow.AddSeconds(2);
            double settled = double.NaN;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(16).ConfigureAwait(true);
                if (surface.TryGetPresentedEnterProgress(scenarioTexts[0], out settled) && settled >= 1)
                {
                    break;
                }
            }

            LifetimeProbeApp.ObservedPageEnterProgressAfterReducedMotion = settled;

            // Every staggered child — including ones whose delay outlived earlier frames — must
            // settle at the final state.
            LifetimeProbeApp.ObservedAllStaggeredChildrenSettled = scenarioTexts.All(text =>
                surface.TryGetPresentedEnterProgress(text, out double value) && value >= 1);
            VerifyCloseDoesNotRestoreShadows(window, shell, surface);
        }
        catch (Exception error)
        {
            LifetimeProbeApp.Failure = error;
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task VerifySpringIgnoresStaleSceneReads()
    {
        object owner = new();
        List<double> positions = [];
        try
        {
            AvaloniaUiMotion.AnimateSpring(owner, "value", () => 0, positions.Add, 1, .3, () => false);
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while ((positions.Count == 0 || positions[^1] < .5) && DateTime.UtcNow < deadline)
                await Task.Delay(16).ConfigureAwait(true);
            AssertTrue(positions.Count > 1 && positions[^1] >= .5);
            for (int i = 1; i < positions.Count; i++) AssertTrue(positions[i] >= positions[i - 1]);
            double before = positions[^1];
            int split = positions.Count;
            AvaloniaUiMotion.AnimateSpring(owner, "value", () => 0, positions.Add, 0, .3, () => false);
            while (positions.Count == split && DateTime.UtcNow < deadline)
                await Task.Delay(16).ConfigureAwait(true);
            AssertTrue(positions[split] > before / 2); // No jump to the stale scene's zero.
            while (positions[^1] != 0 && DateTime.UtcNow < deadline)
                await Task.Delay(16).ConfigureAwait(true);
            AssertEqual(0d, positions[^1]);
        }
        finally { AvaloniaUiMotion.CancelAll(owner); }
        Console.WriteLine("PASS: spring integration and retargeting tolerate delayed scene commits");
    }

    private static void VerifyCloseDoesNotRestoreShadows(
        AvaloniaUiShellWindow window, XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        Border shadow = window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.BoxShadow.Count > 0);
        shell.Renderer.ReducedMotion = false;
        AvaloniaNativeWindowActions.WindowActionButton close = window.GetVisualDescendants()
            .OfType<AvaloniaNativeWindowActions.WindowActionButton>().Last();
        Point center = close.TranslatePoint(new Point(14, 14), window)!.Value;
        window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
        AssertTrue(!shadow.IsVisible && shadow.BoxShadow.Count == 0);
        shell.SetStyle(XsrUiShellStyle.LiquidGlass);
        surface.CommitScene();
        window.WindowState = WindowState.Maximized;
        window.WindowState = WindowState.Normal;
        AssertTrue(!shadow.IsVisible && shadow.BoxShadow.Count == 0);
        AssertTrue(!window.TransparencyLevelHint.Contains(WindowTransparencyLevel.AcrylicBlur));
        Console.WriteLine("PASS: close collapse suppresses application shadow and backdrop across scene updates");
    }

    private static void VerifyAccessibleContentAndNativeFocus(
        AvaloniaUiShellWindow window, XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        surface.CommitScene();
        AutomationPeer root = ControlAutomationPeer.CreatePeerForElement(window);
        static IEnumerable<AutomationPeer> Descendants(AutomationPeer peer)
        {
            yield return peer;
            foreach (AutomationPeer child in peer.GetChildren())
                foreach (AutomationPeer descendant in Descendants(child)) yield return descendant;
        }
        AssertTrue(Descendants(root).Any(peer => peer.IsContentElement() && peer.GetName() == "启动"));
        AssertTrue(window.FocusManager!.GetFocusedElement() is AvaloniaUiSceneNodeControl);
        AvaloniaUiSceneNodeControl navigation = surface.Children.OfType<AvaloniaUiSceneNodeControl>()
            .Single(control => control.Node.Role == XsrUiSemanticRole.NavigationItem && control.Node.Label == "设置");
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(navigation);
        peer.SetFocus();
        AssertEqual(navigation.Node.Entity, shell.Renderer.Focused);
        AssertTrue(ReferenceEquals(navigation, window.FocusManager.GetFocusedElement()));
        AssertTrue(peer.HasKeyboardFocus());
        AssertTrue(navigation.Node.IsFocusVisible);
        AssertNotNull(peer.GetProvider<IInvokeProvider>()).Invoke();
        AssertEqual(XsrSemanticId.Parse("navigation.settings"), shell.SelectedNavigationId);
        // Actual keyboard events bubble from the native focused control into the scene surface.
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        AssertTrue(window.FocusManager.GetFocusedElement() is AvaloniaUiSceneNodeControl focused
            && focused.Node.Entity == shell.Renderer.Focused && focused.Node.IsFocusVisible);
        navigation.Apply(navigation.Node with { Label = "应用设置" });
        AssertEqual("应用设置", peer.GetName());
        Console.WriteLine("PASS: native accessibility tree, keyboard focus and invoke are connected");
    }

    private static void VerifyWindowActionFeedback(AvaloniaUiShellWindow window, AvaloniaUiSceneSurface surface)
    {
        foreach (AvaloniaNativeWindowActions.WindowActionButton button in window.GetVisualDescendants()
            .OfType<AvaloniaNativeWindowActions.WindowActionButton>())
        {
            AssertTrue(button.HoverBrush is ISolidColorBrush brush && brush.Color.A > 0);
            Point center = button.TranslatePoint(new Point(14, 14), window)!.Value;
            window.MouseMove(center, RawInputModifiers.None);
            AssertEqual(1d, button.PresentedHoverOpacity);
            window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
            AssertEqual(.9, button.PresentedPressScale);
            // Release outside to exercise cancellation without minimizing/closing this test window.
            window.MouseMove(new Point(400, 300), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(400, 300), MouseButton.Left, RawInputModifiers.None);
            AssertEqual(1d, button.PresentedPressScale);
            AssertEqual(0d, button.PresentedHoverOpacity);
        }
        Console.WriteLine("PASS: native caption hover, immediate press and cancellation feedback");
    }

    private static async Task VerifyCapsuleGeometryClock(XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        XsrUiEntityId page = shell.Tree.Create("capsule-clock-page");
        XsrUiEntityId capsule = shell.Tree.Create("capsule-clock-button");
        shell.Tree.SetComponent(capsule, new XsrUiElement { Width = 112, Height = 36 });
        shell.Tree.SetComponent(capsule, new XsrUiInput { Clickable = true, Focusable = true });
        shell.Tree.SetComponent(capsule, new XsrUiVisualStyle { HoverExpand = true });
        shell.Tree.SetComponent(capsule, new XsrUiSemantic(XsrUiSemanticRole.Button, "版本设置"));
        shell.Tree.SetComponent(capsule, new XsrUiText("版本设置"));
        shell.Tree.Attach(capsule, page);
        shell.Stage.Navigation.Replace(page);
        shell.Renderer.ReducedMotion = false;
        surface.CommitScene();
        XsrUiRect collapsed = Node(surface.Scene!, capsule).Rect;
        AssertEqual(36d, collapsed.Width);
        AssertTrue(shell.Renderer.PointerMoved(new XsrUiPoint(collapsed.X + 18, collapsed.Y + 18)));
        surface.CommitScene();
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        double previousWidth = collapsed.Width;
        while (Node(surface.Scene!, capsule).CapsuleExpansionProgress < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(16).ConfigureAwait(true);
            double width = Node(surface.Scene!, capsule).Rect.Width;
            AssertTrue(width >= previousWidth);
            previousWidth = width;
        }
        AssertEqual(112d, Node(surface.Scene!, capsule).Rect.Width);
        AssertTrue(shell.Renderer.PointerMoved(new XsrUiPoint(-1, -1)));
        surface.CommitScene();
        await Task.Delay(30).ConfigureAwait(true);
        shell.Renderer.ReducedMotion = true;
        deadline = DateTime.UtcNow.AddSeconds(2);
        while (Node(surface.Scene!, capsule).CapsuleExpansionProgress > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(16).ConfigureAwait(true);
        AssertEqual(36d, Node(surface.Scene!, capsule).Rect.Width);
        Console.WriteLine("PASS: capsule clock commits actual layout width and reduced-motion reversal");
    }

    private static async Task VerifyPagerNativeDragAndClock(
        AvaloniaUiShellWindow window, XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        XsrUiEntityId root = shell.Tree.Create("native-pager-page");
        XsrUiEntityId entity = shell.Tree.Create("native-pager");
        XsrUiPager pager = new();
        shell.Tree.SetComponent(entity, new XsrUiElement { Width = 220, Height = 140 });
        shell.Tree.SetComponent(entity, new XsrUiInput { Focusable = true });
        shell.Tree.SetComponent(entity, pager);
        shell.Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.Content, "Information cards"));
        shell.Tree.Attach(entity, root);
        for (int i = 0; i < 2; i++)
        {
            XsrUiEntityId child = shell.Tree.Create($"native-pager-card-{i}");
            shell.Tree.SetComponent(child, new XsrUiSemantic(XsrUiSemanticRole.Text, $"Card {i}"));
            shell.Tree.SetComponent(child, new XsrUiText($"Card {i}"));
            shell.Tree.Attach(child, entity);
        }
        shell.Renderer.ReducedMotion = true;
        shell.Stage.Navigation.Replace(root);
        surface.CommitScene();
        window.UpdateLayout();
        XsrUiRect rect = Node(surface.Scene!, entity).Rect;
        Point start = surface.TranslatePoint(new Point(rect.X + 50, rect.Y + 110), window)!.Value;
        Point end = start.WithY(start.Y - 90);
        shell.Renderer.ReducedMotion = false;
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        AssertTrue(pager.IsDragging && pager.Position > .5 && pager.Position < 1);
        double held = pager.Position;
        await Task.Delay(40).ConfigureAwait(true);
        AssertEqual(held, pager.Position); // A captured drag, not an autonomous animation.
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        AssertEqual(1, pager.PageIndex);
        AssertTrue(!pager.IsDragging);
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (pager.Position != 1 && DateTime.UtcNow < deadline) await Task.Delay(16).ConfigureAwait(true);
        AssertEqual(1d, pager.Position);

        AssertTrue(shell.Renderer.MovePager(entity, -1));
        surface.CommitScene();
        await Task.Delay(32).ConfigureAwait(true);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        held = pager.Position;
        await Task.Delay(40).ConfigureAwait(true);
        AssertEqual(held, pager.Position); // Re-grab a moving card without a jump.
        window.MouseUp(start, MouseButton.Left, RawInputModifiers.None);
        shell.Renderer.ReducedMotion = true;
        deadline = DateTime.UtcNow.AddSeconds(2);
        while (pager.Position != 0 && DateTime.UtcNow < deadline) await Task.Delay(16).ConfigureAwait(true);
        AssertEqual(0d, pager.Position);
        AssertTrue(shell.Renderer.Focus(entity));
        surface.CommitScene();
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        AssertEqual(1, pager.PageIndex);
        AssertEqual(1d, pager.Position);
        Console.WriteLine("PASS: native captured card drag, interruption, spring settling and keyboard paging");
    }

    private static XsrUiSceneNode Node(XsrUiScene scene, XsrUiEntityId entity) =>
        scene.Nodes.Single(node => node.Entity.Equals(entity));

    private static XsrUiSceneNode Node(
        XsrUiEntityId entity,
        XsrUiSemanticRole role,
        bool selected = false,
        bool focusable = false,
        bool clickable = false,
        bool hovered = false) =>
        new(
            entity,
            new XsrUiRect(0, 0, 100, 40),
            0,
            role,
            role.ToString(),
            null,
            null,
            false,
            null,
            null,
            default,
            selected,
            focusable,
            clickable,
            hovered);

    private static T AssertNotNull<T>(T? value)
        where T : class =>
        value ?? throw new InvalidOperationException("Expected a non-null value.");

    private static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true but received false.");
        }
    }

    private static void AssertFalse(bool value) => AssertTrue(!value);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
