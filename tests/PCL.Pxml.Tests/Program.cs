using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Pxml.Tests;

internal static partial class Program
{
    private static readonly (string Name, Action Body)[] TestCases =
    [
        // XSR-207: PXML grammar and parser.
        ("simple document parses structurally", SimpleDocumentParsesStructurally),
        ("state bindings are recognized", StateBindingsAreRecognized),
        ("nested children keep document order", NestedChildrenKeepDocumentOrder),
        ("comments and whitespace are ignored", CommentsAndWhitespaceAreIgnored),
        ("duplicate properties are rejected", DuplicatePropertiesAreRejected),
        ("malformed bindings are rejected", MalformedBindingsAreRejected),
        ("text content is rejected", TextContentIsRejected),
        ("documents without one root are rejected", DocumentsWithoutOneRootAreRejected),
        ("foreign namespaces are rejected", ForeignNamespacesAreRejected),
        ("DTD and qualified properties are rejected", DtdAndQualifiedPropertiesAreRejected),
        ("document-level comments are accepted", DocumentLevelCommentsAreAccepted),
        // XSR-208: PXML to UI.Next IR compilation.
        ("generated control catalog is complete and deterministic", GeneratedControlCatalogIsCompleteAndDeterministic),
        ("vertical pager compiles loads and routes keyboard pages", VerticalPagerCompilesLoadsAndRoutesKeyboardPages),
        ("compile simple page", CompileSimplePage),
        ("compile text state binding", CompileTextStateBinding),
        ("compile visibility binding", CompileVisibilityBinding),
        ("compile button defaults and command", CompileButtonDefaultsAndCommand),
        ("compile shell controls and stretch contract", CompileShellControlsAndStretchContract),
        ("compile thickness and size", CompileThicknessAndSize),
        ("compile weighted constrained alignment", CompileWeightedConstrainedAlignment),
        ("compile separates entity keys from semantic labels", CompileSeparatesEntityKeysFromSemanticLabels),
        ("compile rejects duplicate entity keys", CompileRejectsDuplicateEntityKeys),
        ("compile rejects unknown element and property", CompileRejectsUnknownElementAndProperty),
        ("compile rejects malformed values", CompileRejectsMalformedValues),
        ("compile rejects bindings absent from the control model", CompileRejectsBindingsAbsentFromTheControlModel),
        ("compile enforces generated child policy", CompileEnforcesGeneratedChildPolicy),
        // XSR-209: runtime loader.
        ("loader produces same scene as hand built tree", LoaderProducesSameSceneAsHandBuiltTree),
        ("loaded bindings drive rendering", LoadedBindingsDriveRendering),
        ("loaded visibility bindings drive rendering", LoadedVisibilityBindingsDriveRendering),
        ("loaded weighted layout facts drive rendering", LoadedWeightedLayoutFactsDriveRendering),
        ("loaded dynamic button text and semantic label stay synchronized", LoadedDynamicButtonTextAndSemanticLabelStaySynchronized),
        ("template buttons route child input and obey enabled state", TemplateButtonsRouteChildInputAndObeyEnabledState),
        ("loader rejects unknown state paths", LoaderRejectsUnknownStatePaths),
        ("loader failures leave the tree unchanged", LoaderFailuresLeaveTheTreeUnchanged),
        ("PXML shell template loads into UI.Next shell", PxmlShellTemplateLoadsIntoUiNextShell),
    ];

    private static int Main()
    {
        foreach ((string name, Action body) in TestCases)
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"PXML compiler tests passed: {TestCases.Length}.");
        return 0;
    }

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
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }

    private static void AssertNull<T>(T? value)
        where T : class
    {
        if (value is not null)
        {
            throw new InvalidOperationException("Expected null but received a value.");
        }
    }

    private static void AssertSequence<T>(T[] expected, T[] actual)
        where T : IEquatable<T>
    {
        if (expected.Length != actual.Length
            || !expected.Zip(actual, (left, right) => left.Equals(right)).All(equal => equal))
        {
            throw new InvalidOperationException(
                $"Expected sequence [{string.Join(", ", expected)}] but received [{string.Join(", ", actual)}].");
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}
