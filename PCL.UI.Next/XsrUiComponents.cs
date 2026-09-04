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

    public XsrStateId BoundVisibility { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    public double? MinWidth { get; set; }

    public double? MaxWidth { get; set; }

    public double? MinHeight { get; set; }

    public double? MaxHeight { get; set; }

    /// <summary>
    /// Shares the parent's remaining stack-axis space with weighted siblings. Zero keeps the
    /// element at its desired size; positive values behave like star-sized rows or columns.
    /// </summary>
    public double Weight { get; set; }

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

    /// <summary>
    /// Gives the final visible child the remaining main-axis space. This is intentionally
    /// opt-in: a normal stack keeps its intrinsic size, while shell rows can reserve the rest
    /// of the viewport for the content host.
    /// </summary>
    public bool StretchLastChild { get; set; }
}

/// <summary>
/// Semantic role and label of one entity for accessibility and tests. Backends map roles to
/// their native accessibility bridges; UI.Next never references a concrete backend.
/// </summary>
public sealed class XsrUiSemantic(XsrUiSemanticRole role, string? label = null)
{
    public XsrUiSemanticRole Role { get; set; } = role;

    public string? Label { get; set; } = label;

    /// <summary>Optional host state whose applied string value is the current accessible label.</summary>
    public XsrStateId BoundLabel { get; set; }
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
    TitleBar = 9,
    Navigation = 10,
    NavigationItem = 11,
    Content = 12,
    TextInput = 13,
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
/// Scroll offsets of one stacking container. Offsets are renderer-local ephemeral state,
/// clamped to the measured content extent during arrange; wheel routing adjusts them.
/// </summary>
public sealed class XsrUiScroll
{
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }
}

/// <summary>
/// One media slot. The kernel carries only the source reference; decoding and drawing belong to
/// backends.
/// </summary>
public sealed class XsrUiImage
{
    public XsrUiImage(string source)
    {
        Source = source;
    }

    public string Source { get; set; }
}

/// <summary>
/// Marks one entity as an input target: focusable for keyboard navigation, clickable for
/// pointer activation. Hover, pressed, and focus flags are renderer-local ephemeral state.
/// </summary>
public sealed class XsrUiInput
{
    /// <summary>Renderer-owned capsule geometry; never a product state value.</summary>
    public double CapsuleExpansionProgress { get; internal set; }

    public bool Focusable { get; set; }

    public bool Clickable { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The optional state that drives clickability: when assigned and the applied value is
    /// false, the node presents and routes as disabled even though its base Clickable stays
    /// true. Mirrors <see cref="XsrUiElement.BoundVisibility"/>.
    /// </summary>
    public XsrStateId BoundEnabled { get; set; }

    public bool IsHovered { get; set; }

    public bool IsPressed { get; set; }

    public bool IsFocused { get; set; }
    public bool IsFocusVisible { get; set; }
}

/// <summary>
/// Selection state for mutually exclusive navigation or list items. Selection is a semantic
/// renderer fact, separate from focus and pointer-pressed state.
/// </summary>
public sealed class XsrUiSelection
{
    public bool IsSelected { get; set; }
}

/// <summary>
/// Backend-neutral RGBA color. UI.Next carries color values as data; a platform backend decides
/// how the value is turned into a native brush or draw command.
/// </summary>
public readonly record struct XsrUiColor(byte Red, byte Green, byte Blue, byte Alpha = 255)
{
    public static XsrUiColor FromRgb(byte red, byte green, byte blue) => new(red, green, blue);

    public static XsrUiColor Transparent => new(0, 0, 0, 0);
}

/// <summary>
/// Describes the material treatment of a surface without naming a platform compositor.
/// </summary>
public enum XsrUiSurfaceKind
{
    None = 0,
    Solid = 1,
    Translucent = 2,
    Glass = 3,
}

/// <summary>Horizontal placement of text inside its scene rectangle.</summary>
public enum XsrUiTextAlignment
{
    Start = 0,
    Center = 1,
    End = 2,
}

/// <summary>
/// Immutable visual facts copied from an entity into the render scene. The default value means
/// that the backend should use its own neutral fallback.
/// </summary>
public readonly record struct XsrUiVisualStyleSnapshot(
    XsrUiColor Background,
    XsrUiColor Foreground,
    XsrUiColor Border,
    XsrUiColor Hover,
    bool HoverExpand,
    XsrUiSurfaceKind Surface,
    double Opacity,
    double CornerRadius,
    double BorderWidth,
    double BlurRadius,
    double FontSize,
    double FontWeight,
    XsrUiTextAlignment TextAlignment,
    bool NavigationLayout = false,
    bool WrapText = false)
{
    public bool IsDefined => Surface != XsrUiSurfaceKind.None || Opacity != 0;
}

/// <summary>
/// Mutable visual component owned by the render-thread tree. Mutating a component must be
/// followed by <see cref="XsrUiTree.MarkDirty(XsrUiEntityId, XsrUiDirtyKinds)"/> by its owner.
/// </summary>
public sealed class XsrUiVisualStyle
{
    public XsrUiColor Background { get; set; } = XsrUiColor.Transparent;

    public XsrUiColor Foreground { get; set; } = XsrUiColor.FromRgb(255, 255, 255);

    public XsrUiColor Border { get; set; } = XsrUiColor.Transparent;

    /// <summary>
    /// Gets or sets the transient pointer-hover tint. Zero alpha means the backend picks its own
    /// neutral hover treatment.
    /// </summary>
    public XsrUiColor Hover { get; set; } = XsrUiColor.Transparent;

    /// <summary>
    /// Gets or sets whether this node presents as a hover-expanding capsule: an icon circle at
    /// rest that grows leftward on hover or focus to reveal its scene text. The node's hit
    /// region follows renderer-owned presentation progress rather than reserving expanded space.
    /// </summary>
    public bool HoverExpand { get; set; }
    public bool NavigationLayout { get; set; }
    public bool WrapText { get; set; }

    public XsrUiSurfaceKind Surface { get; set; } = XsrUiSurfaceKind.None;

    public double Opacity { get; set; } = 1;

    public double CornerRadius { get; set; }

    public double BorderWidth { get; set; }

    public double BlurRadius { get; set; }

    /// <summary>
    /// Gets or sets the text size in logical pixels. Zero means the backend picks the size
    /// implied by the entity's semantic role.
    /// </summary>
    public double FontSize { get; set; }

    /// <summary>
    /// Gets or sets the text weight on the common 100..900 scale. 400 means normal; the backend
    /// maps values of roughly 600 and above to its semibold face.
    /// </summary>
    public double FontWeight { get; set; } = 400;

    public XsrUiTextAlignment TextAlignment { get; set; }

    public XsrUiVisualStyleSnapshot Snapshot() => new(
        Background,
        Foreground,
        Border,
        Hover,
        HoverExpand,
        Surface,
        Opacity,
        CornerRadius,
        BorderWidth,
        BlurRadius,
        FontSize,
        FontWeight,
        TextAlignment,
        NavigationLayout,
        WrapText);
}

/// <summary>
/// The keyboard keys the renderer kernel routes itself.
/// </summary>
public enum XsrUiKey
{
    Tab = 1,
    Enter = 2,
    Space = 3,
    Back = 4,
    Up = 5,
    Down = 6,
}
