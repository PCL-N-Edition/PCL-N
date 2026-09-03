using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static void DualShellStylesShareSemanticChrome()
    {
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiShell experimental = XsrUiShellComposer.Compose(store);
        XsrUiShell liquidGlass = XsrUiShellComposer.Compose(
            store,
            new XsrUiShellOptions { Style = XsrUiShellStyle.LiquidGlass });

        XsrUiScene experimentalScene = experimental.Render(new XsrUiSize(1200, 800));
        XsrUiScene liquidScene = liquidGlass.Render(new XsrUiSize(1200, 800));
        AssertEqual(experimental.NavigationItems.Count, liquidGlass.NavigationItems.Count);
        AssertEqual(experimental.SelectedNavigationId, liquidGlass.SelectedNavigationId);
        AssertEqual(XsrUiSemanticRole.TitleBar, Node(experimentalScene, experimental.TitleBar).Role);
        AssertEqual(XsrUiSemanticRole.Navigation, Node(experimentalScene, experimental.Navigation).Role);
        AssertEqual(XsrUiSemanticRole.Content, Node(experimentalScene, experimental.Content).Role);
        AssertEqual(new XsrUiRect(0, 0, 1200, 58), Node(experimentalScene, experimental.TitleBar).Rect);
        AssertEqual(new XsrUiRect(0, 58, 236, 742), Node(experimentalScene, experimental.Navigation).Rect);
        AssertEqual(new XsrUiRect(236, 58, 964, 742), Node(experimentalScene, experimental.Content).Rect);

        XsrUiSceneNode experimentalRoot = Node(experimentalScene, experimental.Root);
        XsrUiSceneNode liquidRoot = Node(liquidScene, liquidGlass.Root);
        AssertEqual(XsrUiSurfaceKind.Solid, experimentalRoot.VisualStyle.Surface);
        AssertEqual(XsrUiSurfaceKind.Solid, Node(experimentalScene, experimental.TitleBar).VisualStyle.Surface);
        AssertEqual(XsrUiSurfaceKind.Glass, Node(liquidScene, liquidGlass.TitleBar).VisualStyle.Surface);
        AssertTrue(liquidRoot.VisualStyle.Background != experimentalRoot.VisualStyle.Background);
        AssertTrue(liquidGlass.Palette.BlurRadius > experimental.Palette.BlurRadius);
        AssertTrue(Node(experimentalScene, experimental.NavigationEntities[experimental.SelectedNavigationId]).IsSelected);
    }

    private static void ShellNavigationSelectionUpdatesSceneAndIntent()
    {
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiShell shell = XsrUiShellComposer.Compose(store, intentSink: intents);
        XsrUiScene initial = shell.Render(new XsrUiSize(1024, 700));
        XsrUiShellNavigationItem destination = shell.NavigationItems[2];
        XsrUiEntityId destinationEntity = shell.NavigationEntities[destination.Id];
        XsrUiRect destinationRect = Node(initial, destinationEntity).Rect;

        AssertTrue(shell.Renderer.PointerPressed(new XsrUiPoint(destinationRect.X + 10, destinationRect.Y + 10)));
        AssertTrue(shell.Renderer.PointerReleased(new XsrUiPoint(destinationRect.X + 10, destinationRect.Y + 10)));
        AssertEqual(destination.Id, shell.SelectedNavigationId);
        AssertEqual(1, intents.Count);
        AssertEqual(destination.Command, intents.Drain()[0].Command);
        XsrUiScene selected = shell.Render(new XsrUiSize(1024, 700));
        AssertTrue(Node(selected, destinationEntity).IsSelected);
        AssertFalse(Node(selected, shell.NavigationEntities[shell.NavigationItems[0].Id]).IsSelected);
    }

    private static void ShellRejectsUnknownNavigationSelection()
    {
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiShell shell = XsrUiShellComposer.Compose(store);
        XsrSemanticId unknown = XsrSemanticId.Parse("navigation.unknown");
        AssertFalse(shell.Select(unknown));
        AssertEqual(shell.NavigationItems[0].Id, shell.SelectedNavigationId);
        AssertFalse(shell.Select(default(XsrUiEntityId)));
    }

    private static void ShellRuntimeContextDrainsHostPublications()
    {
        // Production creates the UI context before its host store. This verifies the same bridge
        // observes the store and is injected into the shell renderer rather than only into a
        // hand-wired test renderer.
        XsrUiRuntimeContext context = new();
        XsrStateStoreBuilder states = new();
        states.Cell<string>("shell.status".AsXsrId(), "Shell");
        XsrStateStore store = states.Build(context.StateBridge);
        XsrUiShell shell = XsrUiShellComposer.Compose(store, stateBridge: context.StateBridge);
        AssertTrue(ReferenceEquals(context.Tree, shell.Tree));
        AssertTrue(ReferenceEquals(context.StateBridge, shell.StateBridge));

        XsrUiEntityId status = shell.Tree.Create("status");
        shell.Tree.SetComponent(status, new XsrUiText(string.Empty)
        {
            BoundState = store.Resolve("shell.status".AsXsrId()),
        });
        shell.Tree.Attach(status, shell.Content);
        _ = shell.Render(new XsrUiSize(1024, 700));

        int renderRequests = 0;
        context.StateBridge.RenderRequested += (_, _) => renderRequests++;
        _ = store.Publish(store.Resolve("shell.status".AsXsrId()), "connected");

        AssertEqual(1, renderRequests);
        XsrUiScene refreshed = shell.Render(new XsrUiSize(1024, 700));
        AssertEqual("connected", Node(refreshed, status).Text);
    }

    private static void ShellStyleToggleUsesRendererIntentAndSceneInputFacts()
    {
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiIntentBuffer intents = new();
        XsrUiShell shell = XsrUiShellComposer.Compose(store, intentSink: intents);
        XsrUiEntityId toggle = shell.Tree.Create("style-toggle");
        shell.Tree.SetComponent(toggle, new XsrUiText("玻璃"));
        shell.Tree.SetComponent(toggle, new XsrUiInput { Focusable = true, Clickable = true });
        shell.Tree.SetComponent(toggle, new XsrUiCommandBinding(XsrUiShellIds.StyleToggle));
        shell.Tree.Attach(toggle, shell.TitleBar);

        XsrUiScene initial = shell.Render(new XsrUiSize(1024, 700));
        XsrUiSceneNode toggleNode = Node(initial, toggle);
        AssertTrue(toggleNode.IsFocusable);
        AssertTrue(toggleNode.IsClickable);
        XsrUiPoint point = new(toggleNode.Rect.X + 1, toggleNode.Rect.Y + 1);

        AssertTrue(shell.Renderer.PointerMoved(point));
        AssertTrue(Node(shell.Render(new XsrUiSize(1024, 700)), toggle).IsHovered);
        AssertTrue(shell.Renderer.PointerPressed(point));
        AssertTrue(Node(shell.Render(new XsrUiSize(1024, 700)), toggle).IsPressed);
        AssertTrue(shell.Renderer.PointerReleased(point));

        AssertTrue(shell.Style == XsrUiShellStyle.LiquidGlass);
        AssertEqual(XsrUiShellIds.StyleToggle, intents.Drain().Single().Command);
    }

    private static XsrUiSceneNode Node(XsrUiScene scene, XsrUiEntityId entity) =>
        scene.Nodes.First(node => node.Entity.Equals(entity));
}
