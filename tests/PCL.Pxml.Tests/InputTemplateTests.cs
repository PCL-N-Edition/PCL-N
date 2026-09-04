using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Pxml.Tests;

internal static partial class Program
{
    private static void VerticalPagerCompilesLoadsAndRoutesKeyboardPages()
    {
        PxmlHostIr ir = PxmlCompiler.Compile(PxmlParser.Parse("""
            <VerticalPager Key="pager" Width="200" Height="100" Label="Information cards">
              <Text Content="About" />
              <Text Content="Trivia" />
            </VerticalPager>
            """));
        AssertEqual(PxmlRuntimeRecipe.VerticalPager, ir.Root.Recipe);
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        XsrUiEntityId host = tree.Create("host");
        XsrUiEntityId pager = PxmlUiLoader.Load(ir, tree, store, host);
        XsrUiRenderer renderer = new(tree, store) { ReducedMotion = true };
        renderer.SetRoot(host);
        AssertTrue(renderer.Render().Nodes.Any(node => node.Text == "About"));
        AssertTrue(renderer.Focus(pager));
        AssertTrue(renderer.HandleKey(XsrUiKey.Down));
        XsrUiScene scene = renderer.Render();
        AssertTrue(scene.Nodes.Any(node => node.Text == "Trivia"));
        AssertTrue(!scene.Nodes.Any(node => node.Text == "About"));
    }

    private static void TemplateButtonsRouteChildInputAndObeyEnabledState()
    {
        XsrSemanticId key = XsrSemanticId.Parse("test.enabled");
        XsrStateStoreBuilder builder = new();
        builder.Cell<bool>(key, "test");
        XsrUiTree tree = new();
        XsrUiStateBridge bridge = new(tree);
        XsrStateStore store = builder.Build(bridge);
        XsrStateId state = store.Resolve(key);
        store.Publish(state, true);
        XsrUiIntentBuffer intents = new();
        XsrUiEntityId root = tree.Create("root");
        PxmlHostIr ir = PxmlCompiler.Compile(PxmlParser.Parse("""
            <Button Key="row" Width="120" Height="52" Clickable="{state test.enabled}" Command="test.select">
              <StackPanel Margin="8">
                <Text Content="Click the child" Height="20" />
              </StackPanel>
            </Button>
            """));
        XsrUiEntityId button = PxmlUiLoader.Load(ir, tree, store, root);
        XsrUiRenderer renderer = new(tree, store, intents, bridge);
        renderer.SetRoot(root);
        _ = renderer.Render();
        XsrUiPoint point = new(15, 15);
        AssertTrue(renderer.PointerPressed(point));
        AssertTrue(renderer.PointerReleased(point));
        AssertEqual(1, intents.Count);

        // Worker publications do not touch the render-owned tree. The frame bridge applies
        // enabled state to pointer, keyboard, accessibility activation and scene semantics.
        Task.Run(() => store.Publish(state, false)).GetAwaiter().GetResult();
        XsrUiScene disabled = renderer.Render();
        XsrUiSceneNode node = disabled.Nodes.Single(item => item.Entity == button);
        AssertTrue(!node.IsEnabled && !node.IsClickable && !node.IsFocusable);
        AssertTrue(!renderer.PointerPressed(point));
        AssertTrue(!renderer.Activate(button));
        AssertTrue(!renderer.Focus(button));
        AssertEqual(1, intents.Count);
        store.Publish(state, true);
        _ = renderer.Render();
        AssertTrue(renderer.Focus(button));
        AssertTrue(renderer.HandleKey(XsrUiKey.Enter));
        AssertEqual(2, intents.Count);
    }
}
