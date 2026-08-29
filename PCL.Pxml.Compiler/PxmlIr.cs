using PCL.UI.Next;

namespace PCL.Pxml;

/// <summary>
/// The finite, renderer-facing component recipe carried by compiled nodes. Control names and
/// property models come from the external catalog; recipes are the stable AOT-safe bridge into
/// UI.Next components.
/// </summary>
public enum PxmlRuntimeRecipe
{
    Element = 1,
    StackLayout = 2,
    Text = 3,
    CommandInput = 4,
    Image = 5,
}

/// <summary>
/// One state binding in the compiled IR: the state path with the renderer property slot it
/// feeds and the dirty kinds the entity receives when the entry changes. This is the compiled
/// form of the PXML binding table.
/// </summary>
public sealed record PxmlIrBinding(string StatePath, XsrUiStateProperty Property, XsrUiDirtyKinds DirtyKinds);

/// <summary>
/// One compiled UI node: typed presentation values ready for the runtime loader. IR nodes are
/// immutable data; loading is the runtime's job (XSR-209).
/// </summary>
public sealed record PxmlIrNode
{
    public PxmlIrNodeKind Kind { get; init; }

    public PxmlRuntimeRecipe Recipe { get; init; }

    public IReadOnlyList<PxmlIrNode> Children { get; init; } = [];

    public double? Width { get; init; }

    public double? Height { get; init; }

    public XsrUiThickness Margin { get; init; }

    public XsrUiThickness Padding { get; init; }

    public bool IsVisible { get; init; } = true;

    public XsrUiOrientation Orientation { get; init; }

    public double Spacing { get; init; }

    public bool Scrollable { get; init; }

    public string? Content { get; init; }

    public string? Label { get; init; }

    public XsrUiSemanticRole Role { get; init; }

    public bool Focusable { get; init; }

    public bool Clickable { get; init; }

    public string? Command { get; init; }

    public string? ImageSource { get; init; }

    public IReadOnlyList<PxmlIrBinding> Bindings { get; init; } = [];
}

/// <summary>
/// One compiled PXML artifact: the immutable IR root. The IR carries state paths, not resolved
/// IDs — resolution against a concrete state store happens at load time.
/// </summary>
public sealed class PxmlUiIr(PxmlIrNode root)
{
    public PxmlIrNode Root { get; } = root ?? throw new ArgumentNullException(nameof(root));
}

/// <summary>
/// Reports one deterministic PXML compile failure.
/// </summary>
public sealed class PxmlCompileException(string message) : InvalidOperationException(message)
{
}
