using PCL.UI.Next;
using PCL.Xsr;

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
    VerticalPager = 6,
    TextInput = 7,
}

/// <summary>
/// One state binding in the compiled IR: the validated semantic state ID with the renderer
/// property slot it feeds and the dirty kinds the entity receives when the entry changes. This
/// is the compiled form of the PXML binding table. The ID was validated at compile time; load
/// time only resolves it through the registry.
/// </summary>
public sealed record PxmlIrBinding(XsrSemanticId State, XsrUiStateProperty Property, XsrUiDirtyKinds DirtyKinds);

/// <summary>
/// One compiled UI node: typed presentation values ready for the runtime loader. IR nodes are
/// immutable data; loading is the runtime's job (XSR-209).
/// </summary>
public sealed record PxmlIrNode
{
    /// <summary>Optional document-unique internal entity key; never an accessibility label.</summary>
    public string? Key { get; init; }

    public PxmlIrNodeKind Kind { get; init; }

    public PxmlRuntimeRecipe Recipe { get; init; }

    public IReadOnlyList<PxmlIrNode> Children { get; init; } = [];

    public double? Width { get; init; }

    public double? Height { get; init; }

    public double? MinWidth { get; init; }

    public double? MaxWidth { get; init; }

    public double? MinHeight { get; init; }

    public double? MaxHeight { get; init; }

    public double Weight { get; init; }

    public XsrUiAlignment HorizontalAlignment { get; init; }

    public XsrUiAlignment VerticalAlignment { get; init; }

    public XsrUiThickness Margin { get; init; }

    public XsrUiThickness Padding { get; init; }

    public bool IsVisible { get; init; } = true;

    public XsrUiOrientation Orientation { get; init; }

    public double Spacing { get; init; }

    public bool StretchLastChild { get; init; }

    public bool Scrollable { get; init; }

    public string? Content { get; init; }

    public string? Label { get; init; }

    public XsrUiSemanticRole Role { get; init; }

    public bool Focusable { get; init; }

    public bool Clickable { get; init; }

    /// <summary>
    /// Gets the validated semantic command ID, or null when the node carries no command.
    /// </summary>
    public XsrSemanticId? Command { get; init; }

    public string? ImageSource { get; init; }
    public string? Placeholder { get; init; }
    public bool IsPassword { get; init; }
    public bool Enabled { get; init; } = true;
    public string? TransitionKey { get; init; }
    public double TransitionOffsetX { get; init; }

    public IReadOnlyList<PxmlIrBinding> Bindings { get; init; } = [];
}

/// <summary>
/// One compiled PXML artifact: the immutable, host-internal IR root. Semantic IDs are parsed
/// and validated here, not at load time. This artifact is deliberately NOT the Plugin UI IR
/// v1 stable ABI — that surface is a separately versioned contract with format and schema
/// versions, unknown-field skipping, resource references, serialization, compatibility, and
/// security validation, delivered with the Plugin SDK. Nothing in this file is frozen for
/// plugin consumption.
/// </summary>
public sealed class PxmlHostIr(PxmlIrNode root)
{
    public PxmlIrNode Root { get; } = root ?? throw new ArgumentNullException(nameof(root));
}

/// <summary>
/// Reports one deterministic PXML compile failure.
/// </summary>
public sealed class PxmlCompileException(string message) : InvalidOperationException(message)
{
}
