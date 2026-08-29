using PCL.Xsr;
using PCL.UI.Next;
using PCL.Xsr.State;

namespace PCL.Pxml.Tests;

internal static partial class Program
{
    private static void LoaderProducesSameSceneAsHandBuiltTree()
    {
        // Parity: the same page loaded from PXML and built by hand must render identically.
        XsrUiTree xmlTree = new();
        XsrStateStore store = BuildStore();
        XsrUiEntityId xmlRoot = PxmlUiLoader.Load(CompilePage(), xmlTree, store, xmlTree.Create("xml-root"));
        XsrUiRenderer xmlRenderer = new(xmlTree, store);
        xmlRenderer.SetRoot(xmlRoot);
        XsrUiScene xmlScene = xmlRenderer.Render();

        XsrUiTree handTree = new();
        XsrUiEntityId handRoot = handTree.Create("hand-root");
        handTree.SetComponent(handRoot, new XsrUiSemantic(XsrUiSemanticRole.Page, null));
        XsrUiEntityId stack = handTree.Create("stack");
        handTree.SetComponent(stack, new XsrUiElement { Margin = XsrUiThickness.Uniform(8) });
        handTree.SetComponent(stack, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 4 });
        handTree.Attach(stack, handRoot);
        XsrUiEntityId title = handTree.Create("title");
        handTree.SetComponent(title, new XsrUiText("Download manager"));
        handTree.SetComponent(title, new XsrUiSemantic(XsrUiSemanticRole.Text, null));
        handTree.Attach(title, stack);
        XsrUiEntityId save = handTree.Create("save");
        handTree.SetComponent(save, new XsrUiSemantic(XsrUiSemanticRole.Button, "Save"));
        handTree.SetComponent(save, new XsrUiInput { Focusable = true, Clickable = true });
        handTree.SetComponent(save, new XsrUiCommandBinding(XsrSemanticId.Parse("app.save")));
        handTree.Attach(save, stack);
        XsrUiEntityId version = handTree.Create("version");
        handTree.SetComponent(version, new XsrUiText(string.Empty));
        handTree.SetComponent(version, new XsrUiSemantic(XsrUiSemanticRole.Text, null));
        handTree.Attach(version, stack);

        XsrUiRenderer handRenderer = new(handTree, store);
        handRenderer.SetRoot(handRoot);
        XsrUiScene handScene = handRenderer.Render();

        AssertEqual(handScene.Count, xmlScene.Count);
        for (int index = 0; index < handScene.Count; index++)
        {
            AssertEqual(handScene[index].Depth, xmlScene[index].Depth);
            AssertEqual(handScene[index].Rect, xmlScene[index].Rect);
            AssertEqual(handScene[index].Text, xmlScene[index].Text);
            AssertEqual(handScene[index].Label, xmlScene[index].Label);
            AssertEqual(handScene[index].Role, xmlScene[index].Role);
        }
    }

    private static void LoadedBindingsDriveRendering()
    {
        XsrUiTree tree = new();
        XsrStateStore store = BuildStore();
        XsrUiStateBridge bridge = new(tree);
        store = BuildStore(bridge);
        XsrUiEntityId root = PxmlUiLoader.Load(
            CompilePage(),
            tree,
            store,
            tree.Create("load-root"));
        XsrUiRenderer renderer = new(tree, store, stateBridge: bridge);
        renderer.SetRoot(root);

        XsrStateId version = store.Resolve("ui.version".AsPxmlStateId());
        _ = store.Publish(version, "v2.0.0.alpha.1");
        XsrUiScene scene = renderer.Render();

        XsrUiSceneNode textNode = scene.Nodes.First(node => node.Text is not null && node.Text.Contains("v2.0.0"));
        AssertEqual("v2.0.0.alpha.1", textNode.Text);
    }

    private static void LoaderRejectsUnknownStatePaths()
    {
        XsrUiTree tree = new();
        XsrStateStore store = BuildStore();

        AssertThrows<PxmlLoadException>(() => PxmlUiLoader.Load(
            Compile("""
                <Page xmlns="N">
                  <Text Content="{state missing.state}" />
                </Page>
                """),
            tree,
            store,
            tree.Create("root")));
    }

    private static PxmlUiIr CompilePage()
    {
        return Compile("""
            <Page xmlns="N">
              <StackPanel Margin="8" Spacing="4">
                <Text Content="Download manager" />
                <Button Label="Save" Command="app.save" />
                <Text Content="{state ui.version}" />
              </StackPanel>
            </Page>
            """);
    }

    private static XsrStateStore BuildStore(XsrUiStateBridge? bridge = null)
    {
        XsrStateStoreBuilder states = new();
        states.Cell<string>("ui.version".AsPxmlStateId(), "Update");
        return states.Build(bridge);
    }


}

file static class PxmlTestStateExtensions
{
    public static XsrSemanticId AsPxmlStateId(this string value) => XsrSemanticId.Parse(value);
}
