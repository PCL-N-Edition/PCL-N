using PCL.UI.Next;
namespace PCL.Pxml.Tests;

internal static partial class Program
{
    private static void CompileSimplePage()
    {
        PxmlUiIr ir = Compile("""
            <Page xmlns="N">
              <StackPanel Orientation="Horizontal" Spacing="4">
                <Text Content="ready" />
              </StackPanel>
            </Page>
            """);

        AssertEqual(PxmlIrNodeKind.Page, ir.Root.Kind);
        AssertEqual(PxmlIrNodeKind.StackPanel, ir.Root.Children[0].Kind);
        AssertEqual(XsrUiSemanticRole_None(), ir.Root.Children[0].Role);
        AssertEqual(XsrUiOrientation.Horizontal, ir.Root.Children[0].Orientation);
        AssertEqual(4, ir.Root.Children[0].Spacing);
        AssertEqual(PxmlIrNodeKind.Text, ir.Root.Children[0].Children[0].Kind);
        AssertEqual("ready", ir.Root.Children[0].Children[0].Content);
    }

    private static void CompileTextStateBinding()
    {
        PxmlUiIr ir = Compile("""
            <Page xmlns="N">
              <Text Content="{state account.name}" />
            </Page>
            """);

        PxmlIrNode text = ir.Root.Children[0];
        AssertNull(text.Content);
        AssertEqual(1, text.Bindings.Count);
        AssertEqual("account.name", text.Bindings[0].StatePath);
        AssertEqual(XsrUiStateProperty.Text, text.Bindings[0].Property);
        AssertTrue(text.Bindings[0].DirtyKinds.HasFlag(XsrUiDirtyKinds.Paint));
    }

    private static void CompileVisibilityBinding()
    {
        PxmlUiIr ir = Compile("""
            <Page xmlns="N">
              <StackPanel IsVisible="{state panel.open}" Scroll="true" />
            </Page>
            """);

        PxmlIrNode stack = ir.Root.Children[0];
        AssertTrue(stack.Scrollable);
        AssertEqual(1, stack.Bindings.Count);
        AssertEqual("panel.open", stack.Bindings[0].StatePath);
        AssertEqual(XsrUiStateProperty.Visibility, stack.Bindings[0].Property);
    }

    private static void CompileButtonDefaultsAndCommand()
    {
        PxmlUiIr ir = Compile("""
            <Page xmlns="N">
              <Button Label="Save" Command="app.save" />
            </Page>
            """);

        PxmlIrNode button = ir.Root.Children[0];
        AssertTrue(button.Clickable);
        AssertTrue(button.Focusable);
        AssertEqual("app.save", button.Command);
        AssertEqual(XsrUiSemanticRole_Button(), button.Role);
    }

    private static void CompileThicknessAndSize()
    {
        PxmlUiIr ir = Compile("""
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
    }

    private static XsrUiSemanticRole XsrUiSemanticRole_None() => XsrUiSemanticRole.None;

    private static XsrUiSemanticRole XsrUiSemanticRole_Button() => XsrUiSemanticRole.Button;

    private static PxmlUiIr Compile(string text)
    {
        PxmlDocument document = PxmlParser.Parse(text.Replace("xmlns=\"N\"", "xmlns=\"https://pcln.dev/pxml/2026\""));
        return PxmlCompiler.Compile(document);
    }
}
