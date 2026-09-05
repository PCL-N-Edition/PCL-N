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
    bool IsPressed = false,
    bool IsEnabled = true,
    XsrUiRect? ClipRect = null,
    bool IsFocusVisible = false,
    double CapsuleExpansionProgress = 0,
    XsrUiPagerSnapshot? Pager = null,
    bool IsAccessible = true,
    XsrUiTextInputSnapshot? TextInput = null,
    XsrUiRasterImage? RasterImage = null,
    string? TransitionKey = null,
    double TransitionOffsetX = 0,
    double TransitionPresentedOffsetX = 0,
    double TransitionOffsetY = 0,
    double PresentationOpacity = 1,
    double TransitionPresentedOffsetY = 0,
    int TransitionEntryOrder = -1,
    double? Progress = null,
    XsrUiLiveSetting LiveSetting = XsrUiLiveSetting.Off,
    XsrUiOverlayMotionKind OverlayMotion = XsrUiOverlayMotionKind.None,
    bool IsOverlayClosing = false,
    XsrUiRect? OverlayAnchor = null,
    int TextMaxLines = 0,
    bool TextTrimsOverflow = false,
    XsrUiScrollSnapshot? Scroll = null)
{
    public bool HasRole => Role != XsrUiSemanticRole.None;
}

/// <summary>
/// One outgoing presentation group. It is not part of the live tree, input, or accessibility.
/// </summary>
public sealed record XsrUiOutgoingLayer(XsrUiEntityId Group, IReadOnlyList<XsrUiSceneNode> Nodes, bool BehindSelf = false);

/// <summary>Immutable, depth-first live draw list plus bounded non-interactive outgoing layers.</summary>
public sealed class XsrUiScene(long version, XsrUiSceneNode[] nodes, IReadOnlyList<XsrUiOutgoingLayer>? outgoing = null)
{
    /// <summary>Bounded presentation snapshots. Never part of input or accessibility traversal.</summary>
    public IReadOnlyList<XsrUiOutgoingLayer> Outgoing { get; } = outgoing ?? [];
    public long Version { get; } = version;

    private readonly XsrUiSceneNode[] _nodes = nodes;

    public int Count => _nodes.Length;

    public XsrUiSceneNode this[int index] => _nodes[index];

    public IReadOnlyList<XsrUiSceneNode> Nodes => _nodes;
}
