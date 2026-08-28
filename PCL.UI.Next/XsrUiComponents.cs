using PCL.Xsr;

namespace PCL.UI.Next;

/// <summary>
/// Text content of one entity. Content is plain text; the backend chooses presentation. Text
/// can bind to one state entry, in which case the renderer reads the applied value per render.
/// </summary>
public sealed class XsrUiText(string content)
{
    public string Content { get; set; } = content ?? string.Empty;

    public XsrStateId BoundState { get; set; }
}

/// <summary>
/// Box model spacing around and inside one element.
/// </summary>
public readonly record struct XsrUiThickness(double Left, double Top, double Right, double Bottom)
{
    public static XsrUiThickness Uniform(double value) => new(value, value, value, value);

    public double Horizontal => Left + Right;

    public double Vertical => Top + Bottom;
}

/// <summary>
/// Alignment of one element inside its arranged slot.
/// </summary>
public enum XsrUiAlignment
{
    Stretch = 0,
    Start = 1,
    Center = 2,
    End = 3,
}

/// <summary>
/// Stack flow direction.
/// </summary>
public enum XsrUiOrientation
{
    Vertical = 0,
    Horizontal = 1,
}

/// <summary>
/// Size, spacing, alignment, and visibility constraints of one element.
/// </summary>
public sealed class XsrUiElement
{
    public bool IsVisible { get; set; } = true;

    public double? Width { get; set; }

    public double? Height { get; set; }

    public XsrUiThickness Margin { get; set; }

    public XsrUiThickness Padding { get; set; }

    public XsrUiAlignment HorizontalAlignment { get; set; }

    public XsrUiAlignment VerticalAlignment { get; set; }
}

/// <summary>
/// Lays out children in one flow direction with fixed spacing.
/// </summary>
public sealed class XsrUiStackPanel(XsrUiOrientation direction)
{
    public XsrUiOrientation Direction { get; set; } = direction;

    public double Spacing { get; set; }
}

/// <summary>
/// Semantic role and label of one entity for accessibility and tests. Backends map roles to
/// their native accessibility bridges; UI.Next never references a concrete backend.
/// </summary>
public sealed class XsrUiSemantic(XsrUiSemanticRole role, string? label = null)
{
    public XsrUiSemanticRole Role { get; set; } = role;

    public string? Label { get; set; } = label;
}

/// <summary>
/// The accessibility roles the renderer kernel knows about.
/// </summary>
public enum XsrUiSemanticRole
{
    None = 0,
    Text = 1,
    Button = 2,
    Page = 3,
    List = 4,
    ListItem = 5,
    Image = 6,
    ProgressBar = 7,
    Dialog = 8,
}

/// <summary>
/// Binds one entity to a command: activation emits the command as intent through the intent
/// sink. The command itself stays a semantic ID; binding resolution is the composition root's job.
/// </summary>
public sealed class XsrUiCommandBinding(XsrSemanticId command)
{
    public XsrSemanticId Command { get; set; } = command;
}

/// <summary>
/// Binds one entity to one state entry: when the entry changes, the entity is marked dirty and
/// the next render pass reads the current value through the state source.
/// </summary>
public sealed class XsrUiStateBinding(XsrStateId state)
{
    public XsrStateId State { get; set; } = state;
}

/// <summary>
/// Marks one entity as an input target: focusable for keyboard navigation, clickable for
/// pointer activation.
/// </summary>
public sealed class XsrUiInput
{
    public bool Focusable { get; set; }

    public bool Clickable { get; set; }

    public bool IsHovered { get; set; }

    public bool IsPressed { get; set; }
}
