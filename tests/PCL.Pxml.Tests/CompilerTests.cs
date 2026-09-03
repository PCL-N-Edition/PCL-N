using PCL.UI.Next;
using PCL.Xsr;
namespace PCL.Pxml.Tests;

internal static partial class Program
{
    private static void GeneratedControlCatalogIsCompleteAndDeterministic()
    {
        AssertSequence(
            ["Page", "StackPanel", "Text", "Button", "Image", "Shell", "TitleBar", "Navigation", "NavigationItem", "ContentHost"],
            PxmlControlCatalog.Names.ToArray());
        AssertEqual(1, (int)PxmlIrNodeKind.Page);
        AssertEqual(2, (int)PxmlIrNodeKind.StackPanel);
        AssertEqual(3, (int)PxmlIrNodeKind.Text);
        AssertEqual(4, (int)PxmlIrNodeKind.Button);
        AssertEqual(5, (int)PxmlIrNodeKind.Image);
        AssertEqual(6, (int)PxmlIrNodeKind.Shell);
        AssertEqual(7, (int)PxmlIrNodeKind.TitleBar);
        AssertEqual(8, (int)PxmlIrNodeKind.Navigation);
        AssertEqual(9, (int)PxmlIrNodeKind.NavigationItem);
        AssertEqual(10, (int)PxmlIrNodeKind.ContentHost);

        if (PxmlControlCatalog.Names is IList<string> names)
        {
            AssertThrows<NotSupportedException>(() => names[0] = "Mutated");
        }
    }

    private static void CompileSimplePage()
    {
        PxmlHostIr ir = Compile("""
            <Page xmlns="N">
              <StackPanel Orientation="Horizontal" Spacing="4">
                <Text Content="ready" />
              </StackPanel>
            </Page>
            """);

        AssertEqual(PxmlIrNodeKind.Page, ir.Root.Kind);
        AssertEqual(PxmlRuntimeRecipe.Element, ir.Root.Recipe);
        AssertEqual(PxmlIrNodeKind.StackPanel, ir.Root.Children[0].Kind);
        AssertEqual(PxmlRuntimeRecipe.StackLayout, ir.Root.Children[0].Recipe);
        AssertEqual(XsrUiSemanticRole_None(), ir.Root.Children[0].Role);
        AssertEqual(XsrUiOrientation.Horizontal, ir.Root.Children[0].Orientation);
        AssertEqual(4, ir.Root.Children[0].Spacing);
        AssertEqual(PxmlIrNodeKind.Text, ir.Root.Children[0].Children[0].Kind);
        AssertEqual(PxmlRuntimeRecipe.Text, ir.Root.Children[0].Children[0].Recipe);
        AssertEqual("ready", ir.Root.Children[0].Children[0].Content);
    }

    private static void CompileTextStateBinding()
    {
        PxmlHostIr ir = Compile("""
            <Page xmlns="N">
              <Text Content="{state account.name}" />
            </Page>
            """);

        PxmlIrNode text = ir.Root.Children[0];
        AssertNull(text.Content);
        AssertEqual(1, text.Bindings.Count);
        AssertEqual("account.name", text.Bindings[0].State.Value);
        AssertEqual(XsrUiStateProperty.Text, text.Bindings[0].Property);
        AssertTrue(text.Bindings[0].DirtyKinds.HasFlag(XsrUiDirtyKinds.Paint));
    }

    private static void CompileVisibilityBinding()
    {
        PxmlHostIr ir = Compile("""
            <Page xmlns="N">
              <StackPanel IsVisible="{state panel.open}" Scroll="true" />
            </Page>
            """);

        PxmlIrNode stack = ir.Root.Children[0];
        AssertTrue(stack.Scrollable);
        AssertEqual(1, stack.Bindings.Count);
        AssertEqual("panel.open", stack.Bindings[0].State.Value);
        AssertEqual(XsrUiStateProperty.Visibility, stack.Bindings[0].Property);
        AssertEqual(XsrUiDirtyKinds.State, stack.Bindings[0].DirtyKinds);
    }

    private static void CompileButtonDefaultsAndCommand()
    {
        PxmlHostIr ir = Compile("""
            <Page xmlns="N">
              <Button Label="Save" Command="app.save" />
            </Page>
            """);

        PxmlIrNode button = ir.Root.Children[0];
        AssertEqual(PxmlRuntimeRecipe.CommandInput, button.Recipe);
        AssertTrue(button.Clickable);
        AssertTrue(button.Focusable);
        XsrSemanticId command = button.Command!.Value;
        AssertEqual("app.save", command.Value);
        AssertEqual(XsrUiSemanticRole_Button(), button.Role);
    }

    private static void CompileShellControlsAndStretchContract()
    {
        PxmlHostIr ir = Compile("""
            <Shell xmlns="N">
              <TitleBar />
              <StackPanel Orientation="Horizontal" StretchLastChild="true">
                <Navigation>
                  <NavigationItem Label="主页" Content="主页" Icon="lucide/play" Command="ui.navigation.home" />
                </Navigation>
                <ContentHost />
              </StackPanel>
            </Shell>
            """);

        AssertEqual(PxmlRuntimeRecipe.StackLayout, ir.Root.Recipe);
        AssertTrue(ir.Root.StretchLastChild);
        AssertEqual(XsrUiSemanticRole.TitleBar, ir.Root.Children[0].Role);
        PxmlIrNode body = ir.Root.Children[1];
        AssertTrue(body.StretchLastChild);
        AssertEqual(XsrUiSemanticRole.Navigation, body.Children[0].Role);
        AssertEqual(XsrUiSemanticRole.NavigationItem, body.Children[0].Children[0].Role);
        AssertEqual("主页", body.Children[0].Children[0].Content);
        AssertEqual("lucide/play", body.Children[0].Children[0].ImageSource);
    }

    private static void CompileThicknessAndSize()
    {
        PxmlHostIr ir = Compile("""
            <Page xmlns="N">
              <Text Content="x" Width="80" Height="24" Margin="4" Padding="1,2,3,4" />
            </Page>
            """);

        PxmlIrNode text = ir.Root.Children[0];
        AssertEqual(80, text.Width!.Value);
        AssertEqual(24, text.Height!.Value);
        AssertEqual(4, text.Margin.Left);
        AssertEqual(1, text.Padding.Left);
        AssertEqual(2, text.Padding.Top);
        AssertEqual(3, text.Padding.Right);
        AssertEqual(4, text.Padding.Bottom);
    }

    private static void CompileRejectsUnknownElementAndProperty()
    {
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Grid />
            </Page>
            """));
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Text Whatever="1" />
            </Page>
            """));
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <StackPanel Command="app.x" />
            </Page>
            """));
    }

    private static void CompileRejectsMalformedValues()
    {
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Text Content="x" Width="eight" />
            </Page>
            """));
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <StackPanel Orientation="Diagonal" />
            </Page>
            """));
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Button Command="{state app.save}" />
            </Page>
            """));
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Image />
            </Page>
            """));
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Text Content="x" Width="NaN" />
            </Page>
            """));
    }

    private static void CompileRejectsBindingsAbsentFromTheControlModel()
    {
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Text Content="x" Width="{state layout.width}" />
            </Page>
            """));
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Button Label="{state action.label}" />
            </Page>
            """));
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Image Source="{state image.source}" />
            </Page>
            """));
    }

    private static void CompileEnforcesGeneratedChildPolicy()
    {
        AssertThrows<PxmlCompileException>(() => Compile("""
            <Page xmlns="N">
              <Text Content="outer">
                <Button Label="invalid child" />
              </Text>
            </Page>
            """));
    }

    private static XsrUiSemanticRole XsrUiSemanticRole_None() => XsrUiSemanticRole.None;

    private static XsrUiSemanticRole XsrUiSemanticRole_Button() => XsrUiSemanticRole.Button;

    private static PxmlHostIr Compile(string text)
    {
        PxmlDocument document = PxmlParser.Parse(text.Replace("xmlns=\"N\"", "xmlns=\"https://pcln.dev/pxml/2026\""));
        return PxmlCompiler.Compile(document);
    }
}
