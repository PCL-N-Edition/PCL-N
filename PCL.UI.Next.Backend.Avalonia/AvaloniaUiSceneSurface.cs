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
/// Presentation motion (hover, press, selection, and the rail expansion) animates between
/// committed scene facts, always starting from the currently presented value.
/// </summary>
public sealed class AvaloniaUiSceneSurface : Panel, IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly object _commitGate = new();
    private readonly Dictionary<XsrUiEntityId, AvaloniaUiSceneNodeControl> _controls = [];
    private readonly Dictionary<int, XsrUiRect> _presentedRects = [];
    private readonly Dictionary<int, (XsrUiRect From, XsrUiRect To)> _railAnimation = [];
    private readonly Dictionary<int, double> _railProgress = [];
    private XsrUiScene? _scene;
    private double _lastNavigationWidth;
    private bool _commitQueued;
    private bool _disposed;

    public AvaloniaUiSceneSurface(XsrUiShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Focusable = true;
        // A transparent brush makes the surface itself hit-testable. Without it the panel never
        // receives pointers (its scene-node children are deliberately not hit-testable), and
        // every click, hover, drag, and double-click would fall through to the window.
        Background = Brushes.Transparent;
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

        AvaloniaUiMotion.CancelAll(this);
        _shell.Tree.RenderInvalidated -= OnTreeRenderInvalidated;
        if (_shell.StateBridge is not null)
        {
            _shell.StateBridge.RenderRequested -= OnStateRenderRequested;
        }

        _controls.Clear();
        _presentedRects.Clear();
        _railAnimation.Clear();
        _railProgress.Clear();
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
            XsrUiRect rect = PresentedRect(entity);
            control.Arrange(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
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
            _presentedRects.Remove(entity.Index);
            _railAnimation.Remove(entity.Index);
        }

        HashSet<int> railAnimated = CollectRailAnimatedEntities(scene);
        for (int index = 0; index < scene.Count; index++)
        {
            XsrUiSceneNode node = scene[index];
            if (!_controls.TryGetValue(node.Entity, out AvaloniaUiSceneNodeControl? control))
            {
                control = new AvaloniaUiSceneNodeControl(
                    RouteAutomationFocus,
                    RouteAutomationInvoke,
                    _shell.Renderer.ReducedMotion);
                _controls.Add(node.Entity, control);
                Children.Add(control);
            }

            control.Apply(node);
            UpdatePresentedRect(node, railAnimated.Contains(node.Entity.Index));
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

    /// <summary>
    /// Entities whose rectangles follow the navigation rail expansion (the rail subtree and the
    /// content subtree). Their rect changes are presented through a short critically damped
    /// interpolation instead of snapping. While the interpolation clock is running, later
    /// commits keep re-targeting it from the currently presented value — the press, hover, and
    /// selection facts of the very click that started the expansion arrive as extra commits and
    /// must not snap the animation away.
    /// </summary>
    private HashSet<int> CollectRailAnimatedEntities(XsrUiScene scene)
    {
        HashSet<int> animated = [];
        bool railTransition = false;
        Stack<XsrUiSceneNode> ancestors = [];
        for (int index = 0; index < scene.Count; index++)
        {
            XsrUiSceneNode node = scene[index];
            while (ancestors.Count > node.Depth)
            {
                _ = ancestors.Pop();
            }

            bool inRail = node.Role == XsrUiSemanticRole.Navigation
                || ancestors.Any(ancestor => ancestor.Role == XsrUiSemanticRole.Navigation);
            bool inContent = node.Role == XsrUiSemanticRole.Content
                || ancestors.Any(ancestor => ancestor.Role == XsrUiSemanticRole.Content);
            if (inRail || inContent)
            {
                animated.Add(node.Entity.Index);
            }

            if (node.Role == XsrUiSemanticRole.Navigation)
            {
                railTransition = _lastNavigationWidth > 0
                    && Math.Abs(node.Rect.Width - _lastNavigationWidth) > 0.5;
                _lastNavigationWidth = node.Rect.Width;
            }

            ancestors.Push(node);
        }

        // Animate only a navigation-width change and the commits that arrive while it plays;
        // unrelated rect changes (for example window resizes) keep snapping.
        if (!railTransition && _railAnimation.Count == 0)
        {
            return [];
        }

        return animated;
    }

    private void UpdatePresentedRect(XsrUiSceneNode node, bool animates)
    {
        if (!animates)
        {
            _presentedRects.Remove(node.Entity.Index);
            _railAnimation.Remove(node.Entity.Index);
            _railProgress.Remove(node.Entity.Index);
            return;
        }

        XsrUiRect target = node.Rect;
        bool running = _railAnimation.ContainsKey(node.Entity.Index);
        XsrUiRect from = _presentedRects.TryGetValue(node.Entity.Index, out XsrUiRect presented)
            ? presented
            : target;
        if (!running && from.Equals(target))
        {
            _presentedRects.Remove(node.Entity.Index);
            return;
        }

        // A running entry re-targets from the currently presented value; an unchanged target
        // keeps its entry alive so sibling commits (press, hover, selection) cannot snap the
        // interpolation away mid-gesture. The shared motion clock drives the presented value.
        int index = node.Entity.Index;
        _presentedRects[index] = from;
        _railAnimation[index] = (from, target);
        double start = _railProgress.TryGetValue(index, out double progress) ? progress : 0;
        _railProgress[index] = start;
        AvaloniaUiMotion.Animate(
            this,
            index,
            () => _railProgress.TryGetValue(index, out double value) ? value : 1,
            value =>
            {
                _railProgress[index] = value;
                if (_railAnimation.TryGetValue(index, out (XsrUiRect From, XsrUiRect To) segment))
                {
                    _presentedRects[index] = LerpRect(segment.From, segment.To, value);
                }

                InvalidateArrange();
                InvalidateVisual();
            },
            1,
            AvaloniaMotionTokens.RailExpandMilliseconds,
            progress => progress,
            completed: () =>
            {
                _railProgress.Remove(index);
                _railAnimation.Remove(index);
                _presentedRects.Remove(index);
            });
    }

    private XsrUiRect PresentedRect(XsrUiEntityId entity)
    {
        if (_scene is null || !TryGetNode(_scene, entity, out XsrUiSceneNode node))
        {
            return default;
        }

        return _presentedRects.TryGetValue(entity.Index, out XsrUiRect presented)
            ? presented
            : node.Rect;
    }

    private static XsrUiRect LerpRect(XsrUiRect from, XsrUiRect to, double progress) => new(
        from.X + ((to.X - from.X) * progress),
        from.Y + ((to.Y - from.Y) * progress),
        from.Width + ((to.Width - from.Width) * progress),
        from.Height + ((to.Height - from.Height) * progress));

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
/// has no knowledge of PXML controls, tree components, shell navigation, or services. Hover,
/// press, and selection presentation animate between scene facts; reduced motion applies every
/// fact immediately.
/// </summary>
internal sealed class AvaloniaUiSceneNodeControl : Control
{
    private const double CollapsedRailCenteringWidth = 50;
    private const double PillWidth = 5;
    private const double PillHeight = 20;
    private const double NavigationIconSize = 20;
    private const double NavigationIconTextGap = 8;

    private static readonly StyledProperty<double> HoverOpacityProperty =
        AvaloniaProperty.Register<AvaloniaUiSceneNodeControl, double>(nameof(HoverOpacity));

    private static readonly StyledProperty<double> PillScaleProperty =
        AvaloniaProperty.Register<AvaloniaUiSceneNodeControl, double>(nameof(PillScale));

    private readonly Action<XsrUiEntityId> _focusFromAutomation;
    private readonly Action<XsrUiEntityId> _invokeFromAutomation;
    private readonly bool _reducedMotion;
    private readonly ScaleTransform _pressScale = new(1, 1);
    private readonly List<AvaloniaUiSceneNodeControl> _selectionItems = [];
    private XsrUiSceneNode _node;
    private AvaloniaUiSceneNodeControl? _selectionContainer;
    private bool _applied;

    internal AvaloniaUiSceneNodeControl(
        Action<XsrUiEntityId> focusFromAutomation,
        Action<XsrUiEntityId> invokeFromAutomation,
        bool reducedMotion)
    {
        _focusFromAutomation = focusFromAutomation ?? throw new ArgumentNullException(nameof(focusFromAutomation));
        _invokeFromAutomation = invokeFromAutomation ?? throw new ArgumentNullException(nameof(invokeFromAutomation));
        _reducedMotion = reducedMotion;
        IsHitTestVisible = false;
        ClipToBounds = true;
        RenderTransform = _pressScale;
        RenderTransformOrigin = RelativePoint.Center;
    }

    private double HoverOpacity
    {
        get => GetValue(HoverOpacityProperty);
        set => SetValue(HoverOpacityProperty, value);
    }

    private double PillScale
    {
        get => GetValue(PillScaleProperty);
        set => SetValue(PillScaleProperty, value);
    }

    internal double PresentedHoverOpacity => HoverOpacity;

    internal double PresentedPillScale => PillScale;

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

        if (!_applied || previous.IsSelected != node.IsSelected)
        {
            if (node.Role == XsrUiSemanticRole.NavigationItem)
            {
                AnimateFact(
                    PillScaleProperty,
                    node.IsSelected ? 1 : 0,
                    node.IsSelected
                        ? AvaloniaMotionTokens.SelectionInMilliseconds
                        : AvaloniaMotionTokens.SelectionOutMilliseconds,
                    node.IsSelected ? null : AvaloniaUiMotion.EaseIn);
            }

            if (ControlAutomationPeer.FromElement(this) is { } peer)
            {
                peer.RaisePropertyChangedEvent(
                    SelectionItemPatternIdentifiers.IsSelectedProperty,
                    previous.IsSelected,
                    node.IsSelected);
            }
        }

        if (!_applied || previous.IsHovered != node.IsHovered)
        {
            AnimateFact(
                HoverOpacityProperty,
                node.IsHovered ? 1 : 0,
                node.IsHovered
                    ? AvaloniaMotionTokens.HoverMilliseconds
                    : AvaloniaMotionTokens.HoverOutMilliseconds);
        }

        if (!_applied || previous.IsPressed != node.IsPressed)
        {
            double scale = node.IsPressed ? AvaloniaMotionTokens.PressScale : 1;
            SetPressScale(scale);
        }

        _applied = true;
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

    /// <summary>
    /// Presents one scene-fact value, animating from its currently presented value. Reduced
    /// motion applies the fact immediately.
    /// </summary>
    private void AnimateFact(AvaloniaProperty property, double target, double durationMilliseconds, Func<double, double>? easing = null)
    {
        if (_reducedMotion)
        {
            AvaloniaUiMotion.Cancel(this, property);
            SetValue(property, target);
            return;
        }

        AvaloniaUiMotion.Animate(
            this,
            property,
            () => (double)GetValue(property)!,
            value => SetValue(property, value),
            target,
            durationMilliseconds,
            easing);
    }

    private void SetPressScale(double scale)
    {
        if (_reducedMotion)
        {
            AvaloniaUiMotion.Cancel(this, ScaleTransform.ScaleXProperty);
            AvaloniaUiMotion.Cancel(this, ScaleTransform.ScaleYProperty);
            _pressScale.ScaleX = scale;
            _pressScale.ScaleY = scale;
            return;
        }

        AvaloniaUiMotion.Animate(
            this, ScaleTransform.ScaleXProperty, () => _pressScale.ScaleX, value => _pressScale.ScaleX = value,
            scale, AvaloniaMotionTokens.PressMilliseconds);
        AvaloniaUiMotion.Animate(
            this, ScaleTransform.ScaleYProperty, () => _pressScale.ScaleY, value => _pressScale.ScaleY = value,
            scale, AvaloniaMotionTokens.PressMilliseconds);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == HoverOpacityProperty || e.Property == PillScaleProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AvaloniaUiMotion.CancelAll(this);
    }

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

        DrawHoverOverlay(context, rect, style);

        // Collapsed rail items center the vector icon and hide the label; expanded rail rows
        // clear the selection pill, lead with the icon, and draw the label after it. Any other
        // icon-bearing node without text (or a collapsed rail item) centers its icon.
        bool collapsedRailItem = _node.Role == XsrUiSemanticRole.NavigationItem
            && Bounds.Width <= CollapsedRailCenteringWidth;
        bool textVisible = _node.Text is { Length: > 0 } && !collapsedRailItem;
        bool railRow = textVisible && Bounds.Width > CollapsedRailCenteringWidth;
        if (_node.ImageSource is { Length: > 0 } iconSource
            && AvaloniaUiIcons.TryGetGeometry(iconSource, out IReadOnlyList<Geometry> iconPaths))
        {
            double scale = NavigationIconSize / AvaloniaUiIcons.ViewBoxSize;

            // The selection pill occupies the left edge of expanded item rows, so icons indent
            // past it; rows without a pill (the rail toggle) keep the same indent for alignment.
            double iconX = railRow
                ? PillWidth + 4
                : textVisible
                    ? 0
                    : Math.Max(0, (Bounds.Width - NavigationIconSize) / 2);
            double iconY = Math.Max(0, (Bounds.Height - NavigationIconSize) / 2);
            IBrush iconBrush = Brush(style.Foreground) ?? new SolidColorBrush(Colors.White);
            Pen iconPen = new(iconBrush, AvaloniaUiIcons.StrokeWidth * scale)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            using (context.PushTransform(
                Matrix.CreateTranslation(iconX, iconY) * Matrix.CreateScale(scale, scale)))
            {
                foreach (Geometry path in iconPaths)
                {
                    context.DrawGeometry(null, iconPen, path);
                }
            }

            if (textVisible)
            {
                DrawText(context, style, iconX + NavigationIconSize + NavigationIconTextGap);
            }
        }
        else if (textVisible)
        {
            DrawText(context, style, 0);
        }

        DrawSelectionPill(context, style);

        if (_node.IsFocused)
        {
            context.DrawRectangle(
                null,
                new Pen(Brush(new XsrUiColor(255, 255, 255, 210))!, 1),
                new RoundedRect(rect.Deflate(1), new CornerRadius(Math.Max(0, style.CornerRadius - 1))));
        }
    }

    private void DrawHoverOverlay(DrawingContext context, Rect rect, XsrUiVisualStyleSnapshot style)
    {
        double opacity = HoverOpacity;
        if (opacity <= 0 || style.Hover.Alpha == 0)
        {
            return;
        }

        byte alpha = (byte)Math.Round(style.Hover.Alpha * Math.Clamp(opacity, 0, 1));
        IBrush hover = new SolidColorBrush(Color.FromArgb(alpha, style.Hover.Red, style.Hover.Green, style.Hover.Blue));
        context.DrawRectangle(
            hover,
            null,
            style.CornerRadius > 0
                ? new RoundedRect(rect, new CornerRadius(style.CornerRadius))
                : new RoundedRect(rect));
    }

    private void DrawSelectionPill(DrawingContext context, XsrUiVisualStyleSnapshot style)
    {
        double scale = PillScale;
        if (_node.Role != XsrUiSemanticRole.NavigationItem || scale <= 0)
        {
            return;
        }

        XsrUiColor pill = style.Border.Alpha > 0 ? style.Border : style.Foreground;
        double height = PillHeight * Math.Clamp(scale, 0, 1);
        double y = Math.Max(0, (Bounds.Height - height) / 2);
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(pill.Alpha, pill.Red, pill.Green, pill.Blue)),
            null,
            new RoundedRect(new Rect(0, y, PillWidth, height), new CornerRadius(PillWidth / 2)));
    }

    private void DrawText(DrawingContext context, XsrUiVisualStyleSnapshot style, double x)
    {
        if (_node.Text is not { Length: > 0 } text)
        {
            return;
        }

        IBrush foreground = Brush(style.Foreground) ?? new SolidColorBrush(Colors.White);
        // Explicit visual-style typography wins; otherwise the semantic role decides, mirroring
        // the legacy sizes (17 px title, 12 px rail items, 14 px body text).
        double fontSize = style.FontSize > 0 ? style.FontSize : FontSizeFor(_node);
        FontWeight weight = style.FontWeight >= 600 ? FontWeight.SemiBold : FontWeight.Normal;
        FormattedText formatted = new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight),
            fontSize,
            foreground);
        double y = Math.Max(0, (Bounds.Height - formatted.Height) / 2);
        context.DrawText(formatted, new Point(x, y));
    }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    private static double FontSizeFor(XsrUiSceneNode node) => node.Role switch
    {
        XsrUiSemanticRole.TitleBar => 17,
        XsrUiSemanticRole.NavigationItem => 12,
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
