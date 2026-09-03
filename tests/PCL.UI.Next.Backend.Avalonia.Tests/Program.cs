using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
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
            reducedMotion: true);
        control.Apply(Node(scene, button));

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(control);
        peer.SetFocus();
        AssertEqual(button, renderer.Focused);

        IInvokeProvider invoke = AssertNotNull(peer.GetProvider<IInvokeProvider>());
        invoke.Invoke();
        AssertEqual(1, intents.Count);
        AssertEqual(button, intents.Drain()[0].Source);

        AvaloniaUiSceneNodeControl text = new(_ => { }, _ => { }, reducedMotion: true);
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

        AvaloniaUiSceneNodeControl navigation = new(_ => focusCount++, _ => invokeCount++, reducedMotion: true);
        AvaloniaUiSceneNodeControl selected = new(_ => focusCount++, _ => invokeCount++, reducedMotion: true);
        AvaloniaUiSceneNodeControl other = new(_ => focusCount++, _ => invokeCount++, reducedMotion: true);
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
        AvaloniaUiSceneNodeControl item = new(_ => { }, _ => { }, reducedMotion: true);

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
