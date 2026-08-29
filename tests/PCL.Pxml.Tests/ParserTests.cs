namespace PCL.Pxml.Tests;

internal static partial class Program
{
    private const string Header = $"<Page xmlns=\"{PxmlWellKnown.Namespace}\">";

    private static readonly string[] ExpectedChildOrder = ["Text", "Button", "Image"];

    private static void SimpleDocumentParsesStructurally()
    {
        PxmlDocument document = PxmlParser.Parse($$"""
            {{Header}}
              <StackPanel Orientation="Vertical" Spacing="8">
                <Text Content="hello" />
              </StackPanel>
            </Page>
            """);

        AssertEqual("Page", document.Root.Name);
        AssertEqual(1, document.Root.Children.Count);
        PxmlElement stack = document.Root.Children[0];
        AssertEqual("StackPanel", stack.Name);
        AssertEqual("Vertical", stack.FindProperty("Orientation")!.Value.Text);
        AssertEqual(PxmlValueKind.Literal, stack.FindProperty("Spacing")!.Value.Kind);
        AssertEqual("hello", stack.Children[0].FindProperty("Content")!.Value.Text);
    }

    private static void StateBindingsAreRecognized()
    {
        PxmlDocument document = PxmlParser.Parse($$"""
            {{Header}}
              <Text Content="{state account.name}" />
            </Page>
            """);

        PxmlValue value = document.Root.Children[0].FindProperty("Content")!.Value;
        AssertEqual(PxmlValueKind.StateBinding, value.Kind);
        AssertEqual("account.name", value.Text);
    }

    private static void NestedChildrenKeepDocumentOrder()
    {
        PxmlDocument document = PxmlParser.Parse($$"""
            {{Header}}
              <StackPanel>
                <Text Content="a" />
                <Button Label="b" />
                <Image Source="c" />
              </StackPanel>
            </Page>
            """);

        AssertSequence(
            ExpectedChildOrder,
            document.Root.Children[0].Children.Select(child => child.Name).ToArray());
    }

    private static void CommentsAndWhitespaceAreIgnored()
    {
        PxmlDocument document = PxmlParser.Parse($$"""
            {{Header}}
              <!-- a comment -->
              <Text Content="kept" />
            </Page>
            """);

        AssertEqual(1, document.Root.Children.Count);
        AssertEqual("kept", document.Root.Children[0].FindProperty("Content")!.Value.Text);
    }

    private static void DuplicatePropertiesAreRejected()
    {
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse($$"""
            {{Header}}
              <Text Content="a" Content="b" />
            </Page>
            """));
    }

    private static void MalformedBindingsAreRejected()
    {
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse($$"""
            {{Header}}
              <Text Content="{state }" />
            </Page>
            """));
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse($$"""
            {{Header}}
              <Text Content="{state a b}" />
            </Page>
            """));
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse($$"""
            {{Header}}
              <Text Content="{state account.name" />
            </Page>
            """));
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse($$"""
            {{Header}}
              <Text Content="{cmd account.name}" />
            </Page>
            """));
    }

    private static void TextContentIsRejected()
    {
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse($$"""
            {{Header}}
              <Text>hello</Text>
            </Page>
            """));
    }

    private static void DocumentsWithoutOneRootAreRejected()
    {
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse(string.Empty));
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse($$"""
            {{Header}}
            </Page>
            <Page xmlns="{{PxmlWellKnown.Namespace}}">
            </Page>
            """));
    }

    private static void ForeignNamespacesAreRejected()
    {
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse($$"""
            <Page xmlns="https://example.com/other">
            </Page>
            """));
    }

    private static void DtdAndQualifiedPropertiesAreRejected()
    {
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse("""
            <!DOCTYPE Page [<!ENTITY value "expanded">]>
            <Page xmlns="https://pcln.dev/pxml/2026">
              <Text Content="&value;" />
            </Page>
            """));
        AssertThrows<PxmlParseException>(() => PxmlParser.Parse("""
            <Page xmlns="https://pcln.dev/pxml/2026" xmlns:x="https://example.com/property">
              <Text x:Content="not-a-pxml-property" />
            </Page>
            """));
    }

    private static void DocumentLevelCommentsAreAccepted()
    {
        PxmlDocument document = PxmlParser.Parse("""
            <!-- before -->
            <Page xmlns="https://pcln.dev/pxml/2026">
              <Text Content="kept" />
            </Page>
            <!-- after -->
            """);

        AssertEqual("Page", document.Root.Name);
        AssertEqual(1, document.Root.Children.Count);
    }
}
