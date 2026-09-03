namespace PCL.UI.Next;

/// <summary>
/// One point in renderer coordinates.
/// </summary>
public readonly record struct XsrUiPoint(double X, double Y);

/// <summary>
/// One width/height pair in renderer coordinates.
/// </summary>
public readonly record struct XsrUiSize(double Width, double Height)
{
}

/// <summary>
/// One rectangle in renderer coordinates.
/// </summary>
public readonly record struct XsrUiRect(double X, double Y, double Width, double Height)
{
    public bool Contains(XsrUiPoint point) =>
        point.X >= X && point.X < X + Width && point.Y >= Y && point.Y < Y + Height;
}

/// <summary>
/// One immutable render-scene node: the laid-out paint rectangle of one entity plus the
/// presentation facts a backend needs to draw it. Nodes never expose components or hierarchy.
/// </summary>
public readonly record struct XsrUiSceneNode(
    XsrUiEntityId Entity,
    XsrUiRect Rect,
    int Depth,
    XsrUiSemanticRole Role,
    string? Label,
    string? Text,
    string? ImageSource,
    bool IsFocused,
    double? AnimationProgress,
    double? AnimationValue,
    XsrUiVisualStyleSnapshot VisualStyle = default,
    bool IsSelected = false,
    bool IsFocusable = false,
    bool IsClickable = false,
    bool IsHovered = false,
    bool IsPressed = false)
{
    public bool HasRole => Role != XsrUiSemanticRole.None;
}

/// <summary>
/// One immutable render scene: the ordered draw list of one frame. Node order is the
/// deterministic depth-first pre-order of the entity tree; later nodes draw above earlier ones.
/// </summary>
public sealed class XsrUiScene(long version, XsrUiSceneNode[] nodes)
{
    public long Version { get; } = version;

    private readonly XsrUiSceneNode[] _nodes = nodes;

    public int Count => _nodes.Length;

    public XsrUiSceneNode this[int index] => _nodes[index];

    public IReadOnlyList<XsrUiSceneNode> Nodes => _nodes;
}
