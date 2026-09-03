using PCL.UI.Next;
using PCL.Xsr.State;

namespace PCL.Pxml.Tests;

internal static partial class Program
{
    private static void PxmlShellTemplateLoadsIntoUiNextShell()
    {
        PxmlHostIr ir = Compile("""
            <Shell xmlns="N">
              <TitleBar>
                <Text Content="PCL Nexa" />
                <Text Content="2.0.0.alpha.1" />
              </TitleBar>
              <StackPanel Orientation="Horizontal" StretchLastChild="true">
                <Navigation>
                  <NavigationItem Label="主页" Content="⌂  主页" Command="ui.navigation.home" />
                  <NavigationItem Label="设置" Content="⚙  设置" Command="ui.navigation.settings" />
                </Navigation>
                <ContentHost />
              </StackPanel>
            </Shell>
            """);
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiTree tree = new();
        XsrUiEntityId host = tree.Create("host");
        XsrUiEntityId root = PxmlUiLoader.Load(ir, tree, store, host);
        tree.Detach(root);
        tree.Destroy(host);
        XsrUiEntityId titleBar = tree.Children(root).Single(child =>
            tree.GetComponent<XsrUiSemantic>(child)?.Role == XsrUiSemanticRole.TitleBar);
        XsrUiEntityId body = tree.Children(root).Single(child =>
            tree.GetComponent<XsrUiStackPanel>(child) is not null
            && !child.Equals(titleBar));
        XsrUiEntityId navigation = tree.Children(body).Single(child =>
            tree.GetComponent<XsrUiSemantic>(child)?.Role == XsrUiSemanticRole.Navigation);
        XsrUiEntityId content = tree.Children(body).Single(child =>
            tree.GetComponent<XsrUiSemantic>(child)?.Role == XsrUiSemanticRole.Content);
        XsrUiShellNavigationItem[] items =
        [
            new("navigation.home", "主页", "⌂"),
            new("navigation.settings", "设置", "⚙"),
        ];
        Dictionary<PCL.Xsr.XsrSemanticId, XsrUiEntityId> entities = [];
        foreach ((XsrUiShellNavigationItem item, XsrUiEntityId entity) in items.Zip(
                     tree.Children(navigation)))
        {
            entities.Add(item.Id, entity);
        }

        XsrUiShell shell = XsrUiShellComposer.Compose(
            store,
            new XsrUiShellTemplate(tree, root, titleBar, body, navigation, content, items, entities),
            new XsrUiShellOptions { Title = "PCL Nexa", Version = "2.0.0.alpha.1" });
        XsrUiScene scene = shell.Render(new XsrUiSize(800, 600));
        AssertEqual(XsrUiSemanticRole.TitleBar, scene.Nodes.First(node => node.Entity.Equals(titleBar)).Role);
        AssertEqual("⌂  主页", scene.Nodes.First(node => node.Entity.Equals(entities[items[0].Id])).Text);
        AssertTrue(scene.Nodes.First(node => node.Entity.Equals(entities[items[0].Id])).IsSelected);
        AssertEqual(XsrUiSurfaceKind.Solid, scene.Nodes.First(node => node.Entity.Equals(root)).VisualStyle.Surface);

        // The PXML ContentHost is the actual UI.Next stage host. A future product page attaches
        // here and therefore becomes part of the same render scene a backend commits.
        XsrUiEntityId page = tree.Create("home-page");
        tree.SetComponent(page, new XsrUiText("Home PXML content"));
        shell.Stage.Navigation.Push(page);
        XsrUiScene pageScene = shell.Render(new XsrUiSize(800, 600));
        AssertTrue(pageScene.Nodes.Any(node => node.Entity.Equals(page) && node.Text == "Home PXML content"));
    }
}
