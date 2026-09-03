using PCL.Xsr.State;

namespace PCL.UI.Next;

/// <summary>
/// The composition glue for one UI surface: a stage hosts the navigation content and any number
/// of overlay layers above it. Scene draw order follows attach order, so overlays always draw
/// above the page. The stage owns its renderer; disposing the stage scope composition is the
/// caller's job through a lifetime scope.
/// </summary>
public sealed class XsrUiStage
{
    private readonly XsrUiTree _tree;
    private readonly List<XsrUiEntityId> _overlays = [];

    public XsrUiStage(
        XsrUiTree tree,
        XsrStateStore state,
        IXsrUiIntentSink? sink = null,
        XsrUiStateBridge? stateBridge = null)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        ValidateStateBridge(tree, stateBridge);
        Tree = tree;
        Root = tree.Create("stage");
        ContentHost = tree.Create("content-host");
        tree.Attach(ContentHost, Root);
        Renderer = new XsrUiRenderer(tree, state, sink, stateBridge);
        Renderer.SetRoot(Root);
        Navigation = new XsrUiNavigator(tree, ContentHost);
    }

    /// <summary>
    /// Creates a stage over a tree that was already populated by a compiled PXML template. The
    /// stage does not create or reparent entities in this overload; the template owns its root and
    /// content host, while the stage supplies renderer, overlay, and page-navigation services.
    /// </summary>
    public XsrUiStage(
        XsrUiTree tree,
        XsrStateStore state,
        XsrUiEntityId root,
        XsrUiEntityId contentHost,
        IXsrUiIntentSink? sink = null,
        XsrUiStateBridge? stateBridge = null)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        ArgumentNullException.ThrowIfNull(state);
        ValidateStateBridge(tree, stateBridge);
        if (!tree.IsAlive(root))
        {
            throw new InvalidOperationException($"The stage root '{root}' is not alive.");
        }

        if (!tree.IsAlive(contentHost))
        {
            throw new InvalidOperationException($"The stage content host '{contentHost}' is not alive.");
        }

        Tree = tree;
        Root = root;
        ContentHost = contentHost;
        Renderer = new XsrUiRenderer(tree, state, sink, stateBridge);
        Renderer.SetRoot(Root);
        Navigation = new XsrUiNavigator(tree, ContentHost);
    }

    public XsrUiTree Tree { get; }

    public XsrUiEntityId Root { get; }

    public XsrUiEntityId ContentHost { get; }

    public XsrUiRenderer Renderer { get; }

    public XsrUiNavigator Navigation { get; }

    /// <summary>
    /// Shows one overlay above every current overlay and the page.
    /// </summary>
    public void Show(XsrUiEntityId overlay)
    {
        if (!_tree.IsAlive(overlay))
        {
            throw new InvalidOperationException($"The overlay '{overlay}' is not alive.");
        }

        _tree.Detach(overlay);
        _tree.Attach(overlay, Root);
        _overlays.Add(overlay);
    }

    /// <summary>
    /// Dismisses one shown overlay. Returns false when it is not shown.
    /// </summary>
    public bool Dismiss(XsrUiEntityId overlay)
    {
        if (_overlays.Remove(overlay))
        {
            _tree.Detach(overlay);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Dismisses the top-most overlay. Returns false when no overlay is shown.
    /// </summary>
    public bool DismissTop()
    {
        if (_overlays.Count == 0)
        {
            return false;
        }

        XsrUiEntityId top = _overlays[^1];
        _overlays.RemoveAt(_overlays.Count - 1);
        _tree.Detach(top);
        return true;
    }

    public IReadOnlyList<XsrUiEntityId> Overlays => _overlays;

    private static void ValidateStateBridge(XsrUiTree tree, XsrUiStateBridge? stateBridge)
    {
        if (stateBridge is not null && !ReferenceEquals(tree, stateBridge.Tree))
        {
            throw new ArgumentException(
                "The state bridge must observe the stage's render tree.",
                nameof(stateBridge));
        }
    }
}
