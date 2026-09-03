using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The Avalonia backend commit surface for one immutable UI.Next scene. It never reads UI tree
/// components or rebuilds shell layout: PXML/UI.Next own structure and geometry, while this
/// class owns final drawing, native input translation, and native accessibility properties.
/// </summary>
public sealed class AvaloniaUiSceneSurface : Panel, IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly object _commitGate = new();
    private readonly Dictionary<XsrUiEntityId, AvaloniaUiSceneNodeControl> _controls = [];
    private XsrUiScene? _scene;
    private bool _commitQueued;
    private bool _disposed;

    public AvaloniaUiSceneSurface(XsrUiShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Focusable = true;
        ClipToBounds = true;
        _shell.Tree.RenderInvalidated += OnTreeRenderInvalidated;
        _shell.StateBridge?.RenderRequested += OnStateRenderRequested;
        SizeChanged += (_, _) => RequestCommit();
        AttachedToVisualTree += (_, _) => RequestCommit();
    }

    /// <summary>The last immutable scene committed to native drawing controls.</summary>
    public XsrUiScene? Scene => _scene;

    /// <summary>
    /// Raised when a pointer press lands on the PXML title-bar surface rather than a clickable
    /// entity. The Window translates it into its native drag operation.
    /// </summary>
    public event EventHandler<PointerPressedEventArgs>? TitleBarDragRequested;

    /// <summary>
    /// Raised after an immutable UI.Next scene has been committed to this backend surface. Native
    /// window chrome consumes this event instead of reading shell palette or navigation state.
    /// </summary>
    public event EventHandler<AvaloniaUiSceneCommittedEventArgs>? SceneCommitted;

    /// <summary>
    /// Schedules one UI-thread scene commit. Repeated invalidations coalesce until that frame is
    /// rendered, including state publications from background service threads.
    /// </summary>
    public void RequestCommit()
    {
        lock (_commitGate)
        {
            if (_disposed || _commitQueued)
            {
                return;
            }

            _commitQueued = true;
        }

        Dispatcher.UIThread.Post(CommitScene);
    }

    /// <summary>
    /// Renders the UI.Next tree at the native surface size and applies that one scene to the
    /// native visual/accessibility bridge.
    /// </summary>
    public void CommitScene()
    {
        lock (_commitGate)
        {
            if (_disposed)
            {
                return;
            }

            // Clear this before rendering. A state publication that occurs during the render
            // will then schedule a following frame instead of being silently lost.
            _commitQueued = false;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        XsrUiScene next = _shell.Render(new XsrUiSize(Bounds.Width, Bounds.Height));
        if (_scene is null || _scene.Version != next.Version)
        {
            _scene = next;
            ApplyScene(next);
            SceneCommitted?.Invoke(this, new AvaloniaUiSceneCommittedEventArgs(next));
        }
    }

    public void Dispose()
    {
        lock (_commitGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shell.Tree.RenderInvalidated -= OnTreeRenderInvalidated;
        if (_shell.StateBridge is not null)
        {
            _shell.StateBridge.RenderRequested -= OnStateRenderRequested;
        }

        _controls.Clear();
        Children.Clear();
        GC.SuppressFinalize(this);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (Control child in Children)
        {
            child.Measure(availableSize);
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach ((XsrUiEntityId entity, AvaloniaUiSceneNodeControl control) in _controls)
        {
            if (_scene is null || !TryGetNode(_scene, entity, out XsrUiSceneNode node))
            {
                continue;
            }

            control.Arrange(new Rect(node.Rect.X, node.Rect.Y, node.Rect.Width, node.Rect.Height));
        }

        return finalSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _ = Focus();
        Point position = e.GetPosition(this);
        XsrUiPoint point = new(position.X, position.Y);
        XsrUiEntityId target = _shell.Renderer.HitTest(point);
        if (target.IsAssigned)
        {
            _ = _shell.Renderer.Focus(target);
        }

        bool handled = _shell.Renderer.PointerPressed(point);
        CommitScene();
        if (handled)
        {
            e.Handled = true;
            return;
        }

        if (IsTitleBarPoint(point))
        {
            TitleBarDragRequested?.Invoke(this, e);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Point position = e.GetPosition(this);
        bool handled = _shell.Renderer.PointerReleased(new XsrUiPoint(position.X, position.Y));
        CommitScene();
        e.Handled = handled;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (_shell.Renderer.PointerMoved(new XsrUiPoint(position.X, position.Y)))
        {
            CommitScene();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_shell.Renderer.PointerMoved(new XsrUiPoint(-1, -1)))
        {
            CommitScene();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point position = e.GetPosition(this);
        if (_shell.Renderer.PointerScroll(
                new XsrUiPoint(position.X, position.Y),
                -e.Delta.Y,
                -e.Delta.X))
        {
            CommitScene();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        XsrUiKey? key = e.Key switch
        {
            Key.Tab => XsrUiKey.Tab,
            Key.Enter => XsrUiKey.Enter,
            Key.Space => XsrUiKey.Space,
            _ => null,
        };
        if (key is { } routed && _shell.Renderer.HandleKey(routed))
        {
            CommitScene();
            e.Handled = true;
        }
    }

    private void ApplyScene(XsrUiScene scene)
    {
        HashSet<XsrUiEntityId> current = scene.Nodes.Select(node => node.Entity).ToHashSet();
        foreach ((XsrUiEntityId entity, AvaloniaUiSceneNodeControl control) in _controls.ToArray())
        {
            if (current.Contains(entity))
            {
                continue;
            }

            Children.Remove(control);
            _controls.Remove(entity);
        }

        for (int index = 0; index < scene.Count; index++)
        {
            XsrUiSceneNode node = scene[index];
            if (!_controls.TryGetValue(node.Entity, out AvaloniaUiSceneNodeControl? control))
            {
                control = new AvaloniaUiSceneNodeControl(RouteAutomationFocus, RouteAutomationInvoke);
                _controls.Add(node.Entity, control);
                Children.Add(control);
            }

            control.Apply(node);
            int currentIndex = Children.IndexOf(control);
            if (currentIndex != index)
            {
                Children.RemoveAt(currentIndex);
                Children.Insert(index, control);
            }
        }

        ConfigureSelectionRelationships(scene);

        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void ConfigureSelectionRelationships(XsrUiScene scene)
    {
        foreach (AvaloniaUiSceneNodeControl control in _controls.Values)
        {
            control.ResetSelectionRelationships();
        }

        Stack<XsrUiSceneNode> ancestors = [];
        for (int index = 0; index < scene.Count; index++)
        {
            XsrUiSceneNode node = scene[index];
            while (ancestors.Count > node.Depth)
            {
                _ = ancestors.Pop();
            }

            if (node.Role == XsrUiSemanticRole.NavigationItem
                && _controls.TryGetValue(node.Entity, out AvaloniaUiSceneNodeControl? item))
            {
                foreach (XsrUiSceneNode ancestor in ancestors)
                {
                    if (ancestor.Role != XsrUiSemanticRole.Navigation
                        || !_controls.TryGetValue(ancestor.Entity, out AvaloniaUiSceneNodeControl? navigation))
                    {
                        continue;
                    }

                    item.SetSelectionContainer(navigation);
                    navigation.AddSelectionItem(item);
                    break;
                }
            }

            ancestors.Push(node);
        }
    }

    private void RouteAutomationFocus(XsrUiEntityId entity) => RouteAutomationAction(entity, activate: false);

    private void RouteAutomationInvoke(XsrUiEntityId entity) => RouteAutomationAction(entity, activate: true);

    private void RouteAutomationAction(XsrUiEntityId entity, bool activate)
    {
        lock (_commitGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RouteAutomationAction(entity, activate));
            return;
        }

        _ = _shell.Renderer.Focus(entity);
        if (activate)
        {
            _ = _shell.Renderer.Activate(entity);
        }

        CommitScene();
    }

    private bool IsTitleBarPoint(XsrUiPoint point) => _scene is not null
        && _scene.Nodes.Any(node => node.Role == XsrUiSemanticRole.TitleBar && node.Rect.Contains(point));

    private static bool TryGetNode(XsrUiScene scene, XsrUiEntityId entity, out XsrUiSceneNode result)
    {
        for (int index = 0; index < scene.Count; index++)
        {
            if (scene[index].Entity.Equals(entity))
            {
                result = scene[index];
                return true;
            }
        }

        result = default;
        return false;
    }

    private void OnTreeRenderInvalidated(object? sender, EventArgs e) => RequestCommit();

    private void OnStateRenderRequested(object? sender, EventArgs e) => RequestCommit();
}

/// <summary>Immutable scene data delivered to backend-native chrome after a commit.</summary>
public sealed class AvaloniaUiSceneCommittedEventArgs(XsrUiScene scene) : EventArgs
{
    public XsrUiScene Scene { get; } = scene ?? throw new ArgumentNullException(nameof(scene));
}

/// <summary>
/// One final-drawing and accessibility projection of an immutable scene node. It intentionally
/// has no knowledge of PXML controls, tree components, shell navigation, or services.
/// </summary>
internal sealed class AvaloniaUiSceneNodeControl : Control
{
    private readonly Action<XsrUiEntityId> _focusFromAutomation;
    private readonly Action<XsrUiEntityId> _invokeFromAutomation;
    private readonly List<AvaloniaUiSceneNodeControl> _selectionItems = [];
    private XsrUiSceneNode _node;
    private AvaloniaUiSceneNodeControl? _selectionContainer;

    internal AvaloniaUiSceneNodeControl(
        Action<XsrUiEntityId> focusFromAutomation,
        Action<XsrUiEntityId> invokeFromAutomation)
    {
        _focusFromAutomation = focusFromAutomation ?? throw new ArgumentNullException(nameof(focusFromAutomation));
        _invokeFromAutomation = invokeFromAutomation ?? throw new ArgumentNullException(nameof(invokeFromAutomation));
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    internal XsrUiSceneNode Node => _node;

    internal AvaloniaUiSceneNodeControl? SelectionContainer => _selectionContainer;

    internal IReadOnlyList<AvaloniaUiSceneNodeControl> SelectionItems => _selectionItems;

    public void Apply(XsrUiSceneNode node)
    {
        XsrUiSceneNode previous = _node;
        _node = node;
        string name = node.Label ?? node.Text ?? node.Role.ToString();
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetAutomationId(this, string.Create(
            CultureInfo.InvariantCulture,
            $"xsr-{node.Entity.Index}-{node.Entity.Generation}"));
        AutomationProperties.SetControlTypeOverride(this, ControlTypeFor(node.Role, node.IsClickable));
        AutomationProperties.SetHelpText(this, node.IsSelected ? "selected" : node.Role.ToString());
        AutomationProperties.SetIsControlElementOverride(this, node.HasRole || node.IsFocusable || node.IsClickable);
        if (previous.IsSelected != node.IsSelected
            && ControlAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(
                SelectionItemPatternIdentifiers.IsSelectedProperty,
                previous.IsSelected,
                node.IsSelected);
        }

        InvalidateVisual();
    }

    internal void ResetSelectionRelationships()
    {
        _selectionContainer = null;
        _selectionItems.Clear();
    }

    internal void SetSelectionContainer(AvaloniaUiSceneNodeControl selectionContainer)
    {
        _selectionContainer = selectionContainer ?? throw new ArgumentNullException(nameof(selectionContainer));
    }

    internal void AddSelectionItem(AvaloniaUiSceneNodeControl item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _selectionItems.Add(item);
    }

    internal void FocusFromAutomation() => _focusFromAutomation(Node.Entity);

    internal void InvokeFromAutomation() => _invokeFromAutomation(Node.Entity);

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        if (_node.Role == XsrUiSemanticRole.Navigation)
        {
            return new AvaloniaUiSceneNavigationAutomationPeer(this);
        }

        if (_node.Role == XsrUiSemanticRole.NavigationItem
            && _node.IsClickable
            && _selectionContainer is not null)
        {
            return new AvaloniaUiSceneNavigationItemAutomationPeer(this);
        }

        return _node.IsClickable
            ? new AvaloniaUiSceneInvokeAutomationPeer(this)
            : new AvaloniaUiSceneNodeAutomationPeer(this);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect rect = new(Bounds.Size);
        XsrUiVisualStyleSnapshot style = _node.VisualStyle;
        IBrush? background = Brush(style.Background);
        IPen? border = style.BorderWidth > 0 ? new Pen(Brush(style.Border), style.BorderWidth) : null;
        if (style.CornerRadius > 0)
        {
            context.DrawRectangle(
                background,
                border,
                new RoundedRect(rect, new CornerRadius(style.CornerRadius)));
        }
        else
        {
            context.DrawRectangle(background, border, rect);
        }

        if (_node.IsHovered && !_node.IsSelected)
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
                null,
                style.CornerRadius > 0
                    ? new RoundedRect(rect, new CornerRadius(style.CornerRadius))
                    : new RoundedRect(rect));
        }

        if (_node.Text is { Length: > 0 } text)
        {
            IBrush foreground = Brush(style.Foreground) ?? new SolidColorBrush(Colors.White);
            FormattedText formatted = new(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                FontSizeFor(_node),
                foreground);
            double y = Math.Max(0, (Bounds.Height - formatted.Height) / 2);
            context.DrawText(formatted, new Point(0, y));
        }

        if (_node.IsFocused)
        {
            context.DrawRectangle(
                null,
                new Pen(Brush(new XsrUiColor(255, 255, 255, 210))!, 1),
                new RoundedRect(rect.Deflate(1), new CornerRadius(Math.Max(0, style.CornerRadius - 1))));
        }
    }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    private static double FontSizeFor(XsrUiSceneNode node) => node.Role switch
    {
        XsrUiSemanticRole.TitleBar => 17,
        XsrUiSemanticRole.NavigationItem => 14,
        XsrUiSemanticRole.Text => 14,
        _ => 14,
    };

    private static AutomationControlType ControlTypeFor(XsrUiSemanticRole role, bool clickable) => role switch
    {
        XsrUiSemanticRole.Button => AutomationControlType.Button,
        XsrUiSemanticRole.NavigationItem => AutomationControlType.ListItem,
        XsrUiSemanticRole.Navigation => AutomationControlType.List,
        XsrUiSemanticRole.TitleBar => AutomationControlType.TitleBar,
        XsrUiSemanticRole.Text => AutomationControlType.Text,
        XsrUiSemanticRole.Image => AutomationControlType.Image,
        XsrUiSemanticRole.ProgressBar => AutomationControlType.ProgressBar,
        XsrUiSemanticRole.Dialog => AutomationControlType.Window,
        XsrUiSemanticRole.Content => AutomationControlType.Pane,
        _ when clickable => AutomationControlType.Button,
        _ => AutomationControlType.Custom,
    };

    private static SolidColorBrush? Brush(XsrUiColor color) => color.Alpha == 0
        ? null
        : new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
}
