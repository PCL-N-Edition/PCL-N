using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using PCL.UI.Next;
using PCL.UI.Next.Backend.Avalonia;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next.Backend.Avalonia.Tests;

internal static class Program
{
    private static readonly (string Name, Action Body)[] TestCases =
    [
        ("automation invoke and focus route through the renderer", AutomationInvokeAndFocusRouteThroughRenderer),
        ("navigation peers expose selection and route selection through invoke", NavigationPeersExposeSelectionAndRouteSelection),
        ("selection and hover facts present under reduced motion", SelectionAndHoverFactsPresentUnderReducedMotion),
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
                    _ = RunRailScenarioAsync(window, shell);
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
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .StartWithClassicDesktopLifetime([]);

        // Reaching this line at all proves the lifetime terminated on main-window close instead
        // of leaving the process running under OnExplicitShutdown.
        LifetimeProbeApp.MarkTerminated();
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
    }

    private static async Task RunRailScenarioAsync(AvaloniaUiShellWindow window, XsrUiShell shell)
    {
        try
        {
            // ReducedMotionCancelsRunningRailMotion: start the expansion normally, flip the
            // policy mid-flight, then collapse. The shell snaps the progress to the collapsed
            // fact and the running track must never write the expansion back over it.
            shell.SetNavigationExpanded(true);
            await Task.Delay(30).ConfigureAwait(true);
            shell.Renderer.ReducedMotion = true;
            shell.SetNavigationExpanded(false);
            await Task.Delay(300).ConfigureAwait(true);
            LifetimeProbeApp.ObservedRailProgressAfterCollapse = shell.RailPresentationProgress;
        }
        finally
        {
            window.Close();
        }
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
