using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.UI.Next;
using PCL.Xsr;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The Avalonia backend commit surface for one immutable UI.Next scene. It never reads UI tree
/// components or rebuilds shell layout: PXML/UI.Next own structure and geometry, while this
/// class owns final drawing, native input translation, and native accessibility properties.
/// Node-local presentation motion (hover, press, selection) animates between committed scene
/// facts from the currently presented value; structural geometry such as the rail expansion is
/// animated inside UI.Next itself, so the scene, the hit test, and the drawn frame always share
/// one geometry.
/// </summary>
public sealed partial class AvaloniaUiSceneSurface : Panel, IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly object _commitGate = new();
    private readonly Dictionary<XsrUiEntityId, AvaloniaUiSceneNodeControl> _controls = [];
    private readonly Dictionary<XsrUiEntityId, double> _capsuleTargets = [];
    private readonly Dictionary<XsrUiEntityId, long> _pagerRevisions = [];
    private XsrUiScene? _scene;
    private XsrUiEntityId _lastPageRoot;
    private XsrSemanticId _lastNavigation;
    private readonly Dictionary<XsrUiEntityId, string> _transitionKeys = [];
    private bool _commitQueued;
    private bool _disposed;
    private bool _initialFocusAssigned;

    public AvaloniaUiSceneSurface(XsrUiShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Focusable = true;
        FocusAdorner = null;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        AutomationProperties.SetName(this, shell.Title);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Control);
        // A transparent brush makes the surface itself hit-testable. Without it the panel never
        // receives pointers (its scene-node children are deliberately not hit-testable), and
        // every click, hover, drag, and double-click would fall through to the window.
        Background = Brushes.Transparent;
        ClipToBounds = true;
        _shell.Tree.RenderInvalidated += OnTreeRenderInvalidated;
        _shell.StateBridge?.RenderRequested += OnStateRenderRequested;
        _shell.NavigationExpandedChanged += OnNavigationExpandedChanged;
        SizeChanged += (_, _) => RequestCommit();
        AttachedToVisualTree += (_, _) => RequestCommit();
    }

    /// <summary>The last immutable scene committed to native drawing controls.</summary>
    public XsrUiScene? Scene => _scene;

    internal bool TryGetPresentedEnterProgress(XsrUiEntityId entity, out double value)
    {
        value = 1;
        return _controls.TryGetValue(entity, out AvaloniaUiSceneNodeControl? control)
            && (value = control.PresentedEnterProgress) is >= 0 and <= 1;
    }

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
        if (!_initialFocusAssigned)
        {
            XsrUiSceneNode initial = next.Nodes.FirstOrDefault(node => node.IsFocusable && node.IsFocused);
            if (!initial.Entity.IsAssigned) initial = next.Nodes.FirstOrDefault(node => node.IsFocusable && node.IsSelected);
            if (!initial.Entity.IsAssigned) initial = next.Nodes.FirstOrDefault(node => node.IsFocusable);
            if (initial.Entity.IsAssigned)
            {
                _initialFocusAssigned = true;
                if (!initial.IsFocused)
                {
                    _shell.Renderer.Focus(initial.Entity, showIndicator: false);
                    next = _shell.Render(new XsrUiSize(Bounds.Width, Bounds.Height));
                }
            }
        }
        if (_scene is null || _scene.Version != next.Version)
        {
            _scene = next;
            ApplyScene(next);
            SceneCommitted?.Invoke(this, new AvaloniaUiSceneCommittedEventArgs(next));
        }
        // Attachment can make native focus available without changing the scene version.
        SynchronizeNativeFocus(next);
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
        _shell.NavigationExpandedChanged -= OnNavigationExpandedChanged;
        if (_shell.StateBridge is not null)
        {
            _shell.StateBridge.RenderRequested -= OnStateRenderRequested;
        }

        foreach (AvaloniaUiSceneNodeControl control in _controls.Values) control.ReleasePresentation();
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
        if (_scene is null)
        {
            return finalSize;
        }

        foreach ((XsrUiEntityId entity, AvaloniaUiSceneNodeControl control) in _controls)
        {
            if (TryGetNode(_scene, entity, out XsrUiSceneNode node))
            {
                control.Arrange(new Rect(node.Rect.X, node.Rect.Y, node.Rect.Width, node.Rect.Height));
            }
        }

        return finalSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point position = e.GetPosition(this);
        XsrUiPoint point = new(position.X, position.Y);
        bool handled = _shell.Renderer.PointerPressed(point);
        CommitScene();
        BeginTextSelection(position, e.ClickCount);
        if (handled)
        {
            e.Pointer.Capture(this);
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
        handled |= _textSelecting.IsAssigned;
        _textSelecting = default;
        e.Pointer.Capture(null);
        CommitScene();
        e.Handled = handled;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (ExtendTextSelection(position)) return;
        if (_shell.Renderer.PointerMoved(new XsrUiPoint(position.X, position.Y)))
        {
            CommitScene();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (e.Pointer.Captured == this) return;
        if (_shell.Renderer.PointerMoved(new XsrUiPoint(-1, -1)))
        {
            CommitScene();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _textSelecting = default;
        if (_shell.Renderer.CancelPointerGesture()) CommitScene();
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
        if (HandleTextEditingKey(e)) { e.Handled = true; return; }
        XsrUiKey? key = e.Key switch
        {
            Key.Tab => XsrUiKey.Tab,
            Key.Enter => XsrUiKey.Enter,
            Key.Space => XsrUiKey.Space,
            Key.Up => XsrUiKey.Up,
            Key.Down => XsrUiKey.Down,
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
            control.ReleasePresentation();
            _controls.Remove(entity);
            _capsuleTargets.Remove(entity);
            _pagerRevisions.Remove(entity);
            AvaloniaUiMotion.Cancel(this, ("capsule", entity));
            AvaloniaUiMotion.Cancel(this, ("pager", entity));
        }

        for (int index = 0; index < scene.Count; index++)
        {
            XsrUiSceneNode node = scene[index];
            if (!_controls.TryGetValue(node.Entity, out AvaloniaUiSceneNodeControl? control))
            {
                control = new AvaloniaUiSceneNodeControl(
                    RouteAutomationFocus,
                    RouteAutomationInvoke,
                    () => _shell.Renderer.ReducedMotion,
                    new AvaloniaUiTextInputActions(
                        (entity, value) => { _shell.Renderer.SetTextInputValue(entity, value); CommitScene(); },
                        (entity, start, end) => { _shell.Renderer.SetTextSelection(entity, start, end); CommitScene(); },
                        (entity, value) => { _shell.Renderer.SetTextPreedit(entity, value); CommitScene(); }));
                _controls.Add(node.Entity, control);
                Children.Add(control);
            }

            control.Apply(node);
            DriveCapsuleGeometry(node);
            DrivePagerGeometry(node);
            int currentIndex = Children.IndexOf(control);
            if (currentIndex != index)
            {
                Children.RemoveAt(currentIndex);
                Children.Insert(index, control);
            }
        }

        ConfigureSelectionRelationships(scene);
        RunPageEnterAnimationsIfNavigated(scene);

        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void SynchronizeNativeFocus(XsrUiScene scene)
    {
        XsrUiSceneNode focused = scene.Nodes.FirstOrDefault(node => node.IsFocused && node.IsFocusable);
        if (focused.Entity.IsAssigned && _controls.TryGetValue(focused.Entity, out AvaloniaUiSceneNodeControl? control)
            && !control.IsFocused)
            control.Focus(focused.IsFocusVisible ? NavigationMethod.Tab : NavigationMethod.Pointer);
    }

    private void DriveCapsuleGeometry(XsrUiSceneNode node)
    {
        if (!node.VisualStyle.HoverExpand) return;
        double target = node.IsEnabled && (node.IsHovered || node.IsFocusVisible) ? 1 : 0;
        if (_capsuleTargets.TryGetValue(node.Entity, out double previous) && previous == target) return;
        _capsuleTargets[node.Entity] = target;
        AvaloniaUiMotion.AnimateSpring(this, ("capsule", node.Entity),
            () => _scene is not null && TryGetNode(_scene, node.Entity, out XsrUiSceneNode current)
                ? current.CapsuleExpansionProgress : node.CapsuleExpansionProgress,
            progress => _shell.Renderer.SetCapsulePresentationProgress(node.Entity, progress),
            target, AvaloniaMotionTokens.CapsuleSpringResponseSeconds, () => _shell.Renderer.ReducedMotion);
    }

    private void DrivePagerGeometry(XsrUiSceneNode node)
    {
        if (node.Pager is not { } pager) return;
        if (_pagerRevisions.TryGetValue(node.Entity, out long revision) && revision == pager.Revision) return;
        _pagerRevisions[node.Entity] = pager.Revision;
        if (pager.IsDragging)
        {
            AvaloniaUiMotion.Cancel(this, ("pager", node.Entity));
            return;
        }
        AvaloniaUiMotion.AnimateSpring(this, ("pager", node.Entity), () => pager.Position,
            position => _shell.Renderer.SetPagerPresentationPosition(node.Entity, position),
            pager.PageIndex, AvaloniaMotionTokens.PagerSpringResponseSeconds,
            () => _shell.Renderer.ReducedMotion,
            pager.ReleaseVelocity == 0 ? null : pager.ReleaseVelocity);
    }

    /// <summary>
    /// Page/content enter: navigation and reusable scene transition keys fade a group together.
    /// Rapid swaps continue from presented opacity; reduced motion applies the final state.
    /// </summary>
    private void RunPageEnterAnimationsIfNavigated(XsrUiScene scene)
    {
        XsrUiEntityId pageRoot = _shell.Stage.Navigation.Current;
        bool navigated = pageRoot != _lastPageRoot || _shell.SelectedNavigationId != _lastNavigation;
        _lastPageRoot = pageRoot;
        _lastNavigation = _shell.SelectedNavigationId;
        HashSet<XsrUiEntityId> animated = [], retained = [];
        for (int index = 0; index < scene.Count; index++)
        {
            XsrUiSceneNode node = scene[index];
            bool changed = navigated && node.Entity == pageRoot;
            if (node.TransitionKey is { } key)
            {
                retained.Add(node.Entity);
                changed |= _transitionKeys.TryGetValue(node.Entity, out string? before) && before != key;
                _transitionKeys[node.Entity] = key;
            }
            if (!changed) continue;
            if (node.TransitionOffsetX != 0)
            {
                XsrUiEntityId target = node.Entity;
                AvaloniaUiMotion.AnimateSpring(this, "slide:" + target,
                    () => _shell.Renderer.GetTransitionOffset(target),
                    value => _shell.Renderer.SetTransitionOffset(target, value),
                    0, AvaloniaMotionTokens.SlideSpringResponseSeconds,
                    () => _shell.Renderer.ReducedMotion);
                continue;
            }
            // A card and its descendants form one layer; chrome keeps its background in place.
            // Sibling layers stagger so related cards enter as a sequence, not one flash.
            int first = node.Role == XsrUiSemanticRole.TitleBar ? index + 1 : index;
            int layer = 0;
            for (int child = first; child < scene.Count && (child == index || scene[child].Depth > node.Depth); child++)
            {
                XsrUiEntityId entity = scene[child].Entity;
                if (animated.Add(entity) && _controls.TryGetValue(entity, out AvaloniaUiSceneNodeControl? control))
                    control.RunEnterAnimation(layer++);
            }
        }
        foreach (XsrUiEntityId entity in _transitionKeys.Keys.Where(entity => !retained.Contains(entity)).ToArray())
            _transitionKeys.Remove(entity);
    }

    /// <summary>
    /// Drives the shell's rail presentation progress on the shared motion clock. The geometry
    /// itself lives in UI.Next: every progress step re-commits the rail width there, which
    /// re-renders the scene, so the hit test and the drawn frame always share the presented
    /// geometry. Reduced motion never starts a track — the shell has already snapped the
    /// progress to its target.
    /// </summary>
    private void OnNavigationExpandedChanged(object? sender, EventArgs e)
    {
        if (_shell.Renderer.ReducedMotion)
        {
            // The shell already snapped the progress; make sure no older track can write over
            // the settled fact.
            AvaloniaUiMotion.Cancel(this, "rail-presentation");
            return;
        }

        AvaloniaUiMotion.AnimateSpring(
            this,
            "rail-presentation",
            () => _shell.RailPresentationProgress,
            value => _shell.SetRailPresentationProgress(value),
            _shell.IsNavigationExpanded ? 1 : 0,
            AvaloniaMotionTokens.RailSpringResponseSeconds,
            reducedMotion: () => _shell.Renderer.ReducedMotion);
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
/// has no knowledge of PXML controls, tree components, shell navigation, or services. Hover,
/// press, and selection presentation animate between scene facts; reduced motion applies every
/// fact immediately.
/// </summary>
internal sealed partial class AvaloniaUiSceneNodeControl : Control
{
    private const double CollapsedRailCenteringWidth = 50;
    private const double PillWidth = 5;
    private const double PillHeight = 20;
    private const double NavigationIconSize = 20;
    private const double NavigationIconTextGap = 8;
    private const double CapsuleCaptionIconGap = 6;

    /// <summary>
    /// Expanded rail rows keep the icon at the collapsed rail's centered position, so expanding
    /// never moves it sideways; the icon offset also clears the selection pill.
    /// </summary>
    private const double RailRowIconOffset = 14;

    private static readonly StyledProperty<double> HoverOpacityProperty =
        AvaloniaProperty.Register<AvaloniaUiSceneNodeControl, double>(nameof(HoverOpacity));

    private static readonly StyledProperty<double> PillScaleProperty =
        AvaloniaProperty.Register<AvaloniaUiSceneNodeControl, double>(nameof(PillScale));

    private static readonly StyledProperty<double> EnterProgressProperty =
        AvaloniaProperty.Register<AvaloniaUiSceneNodeControl, double>(
            nameof(EnterProgress),
            defaultValue: 1);

    private readonly Action<XsrUiEntityId> _focusFromAutomation;
    private readonly Action<XsrUiEntityId> _invokeFromAutomation;
    private readonly Func<bool> _reducedMotion;
    private readonly ScaleTransform _pressScale = new(1, 1);
    private readonly List<AvaloniaUiSceneNodeControl> _selectionItems = [];
    private XsrUiSceneNode _node;
    private AvaloniaUiSceneNodeControl? _selectionContainer;
    private bool _applied;

    internal AvaloniaUiSceneNodeControl(
        Action<XsrUiEntityId> focusFromAutomation,
        Action<XsrUiEntityId> invokeFromAutomation,
        Func<bool> reducedMotion,
        AvaloniaUiTextInputActions? textInputActions = null)
    {
        _focusFromAutomation = focusFromAutomation ?? throw new ArgumentNullException(nameof(focusFromAutomation));
        _invokeFromAutomation = invokeFromAutomation ?? throw new ArgumentNullException(nameof(invokeFromAutomation));
        _reducedMotion = reducedMotion ?? throw new ArgumentNullException(nameof(reducedMotion));
        IsHitTestVisible = false;
        FocusAdorner = null;
        ClipToBounds = true;
        RenderTransform = new TransformGroup { Children = { _enterScale, _pressScale } };
        RenderTransformOrigin = RelativePoint.Center;
        InitializeTextInput(textInputActions);
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

    private double EnterProgress
    {
        get => GetValue(EnterProgressProperty);
        set => SetValue(EnterProgressProperty, value);
    }

    internal double PresentedHoverOpacity => HoverOpacity;

    internal double PresentedPillScale => PillScale;

    internal double PresentedEnterProgress => EnterProgress;

    internal double PresentedPillExpand => _node.CapsuleExpansionProgress;

    internal XsrUiSceneNode Node => _node;

    internal AvaloniaUiSceneNodeControl? SelectionContainer => _selectionContainer;

    internal IReadOnlyList<AvaloniaUiSceneNodeControl> SelectionItems => _selectionItems;

    public void Apply(XsrUiSceneNode node)
    {
        XsrUiSceneNode previous = _node;
        _node = node;
        UpdateRaster(node.RasterImage);
        UpdateTextInput(previous.TextInput, node.TextInput);
        IsEnabled = node.IsEnabled;
        Focusable = node.IsFocusable;
        Clip = node.ClipRect is { } clip
            ? new RectangleGeometry(new Rect(clip.X - node.Rect.X, clip.Y - node.Rect.Y, clip.Width, clip.Height))
            : null;
        string name = node.Label ?? node.Text ?? node.Role.ToString();
        string previousName = previous.Label ?? previous.Text ?? previous.Role.ToString();
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetAutomationId(this, string.Create(
            CultureInfo.InvariantCulture,
            $"xsr-{node.Entity.Index}-{node.Entity.Generation}"));
        AutomationProperties.SetControlTypeOverride(this, ControlTypeFor(node.Role, node.IsClickable));
        AutomationProperties.SetHelpText(this, node.IsSelected ? "selected" : node.Role.ToString());
        AutomationProperties.SetIsControlElementOverride(this,
            node.IsAccessible && (node.HasRole || node.IsFocusable || node.IsClickable));
        AutomationProperties.SetAccessibilityView(this,
            node.IsAccessible && (node.HasRole || node.IsFocusable || node.IsClickable)
                ? AccessibilityView.Content : AccessibilityView.Raw);
        if (_applied && name != previousName && ControlAutomationPeer.FromElement(this) is { } namePeer)
            namePeer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, previousName, name);

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

        if (!_applied || previous.IsHovered != node.IsHovered || previous.IsFocusVisible != node.IsFocusVisible
            || previous.IsEnabled != node.IsEnabled)
        {
            if (!node.IsEnabled)
            {
                // Disabled nodes never expand or light up.
                AnimateFact(HoverOpacityProperty, 0, AvaloniaMotionTokens.HoverOutMilliseconds);
            }
            else if (node.VisualStyle.HoverExpand)
            {
                // Expansion geometry comes from the scene, never a second paint-only width.
                AnimateFact(
                    HoverOpacityProperty,
                    0,
                    AvaloniaMotionTokens.HoverOutMilliseconds);
            }
            else
            {
                AnimateFact(
                    HoverOpacityProperty,
                    node.IsHovered ? 1 : 0,
                    node.IsHovered
                        ? AvaloniaMotionTokens.HoverMilliseconds
                        : AvaloniaMotionTokens.HoverOutMilliseconds);
            }
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
    /// Fades one member of a transition group without translating/clipping its text.
    /// Reduced motion applies the final state immediately.
    /// </summary>
    internal void RunEnterAnimation(int staggerIndex)
    {
        AvaloniaUiMotion.Cancel(this, "enter");
        if (_reducedMotion())
        {
            SetValue(EnterProgressProperty, 1);
            return;
        }

        // Keep rapid retargets continuous instead of flashing back to transparent.
        if (EnterProgress >= 1) SetValue(EnterProgressProperty, 0);
        AvaloniaUiMotion.Animate(
            this,
            "enter",
            () => (double)GetValue(EnterProgressProperty)!,
            value => SetValue(EnterProgressProperty, value),
            1,
            AvaloniaMotionTokens.PageEnterMilliseconds,
            AvaloniaUiMotion.EaseOut,
            delayMilliseconds: Math.Min(staggerIndex, 8) * AvaloniaMotionTokens.PageEnterStaggerMilliseconds,
            reducedMotion: _reducedMotion);
        const double fromScale = 0.985;
        _enterScale.ScaleX = fromScale;
        _enterScale.ScaleY = fromScale;
        AvaloniaUiMotion.Animate(
            this,
            "enter-scale",
            () => _enterScale.ScaleX,
            value =>
            {
                _enterScale.ScaleX = value;
                _enterScale.ScaleY = value;
            },
            1,
            AvaloniaMotionTokens.PageEnterMilliseconds,
            AvaloniaUiMotion.EaseOut,
            delayMilliseconds: Math.Min(staggerIndex, 8) * AvaloniaMotionTokens.PageEnterStaggerMilliseconds);
    }

    /// <summary>
    /// Presents one scene-fact value, animating from its currently presented value. Reduced
    /// motion applies the fact immediately.
    /// </summary>
    private void AnimateFact(AvaloniaProperty property, double target, double durationMilliseconds, Func<double, double>? easing = null)
    {
        if (_reducedMotion())
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
            easing,
            reducedMotion: () => _reducedMotion());
    }

    private void SetPressScale(double scale)
    {
        if (_reducedMotion() || scale < 1)
        {
            AvaloniaUiMotion.Cancel(this, ScaleTransform.ScaleXProperty);
            AvaloniaUiMotion.Cancel(this, ScaleTransform.ScaleYProperty);
            _pressScale.ScaleX = scale;
            _pressScale.ScaleY = scale;
            return;
        }

        AvaloniaUiMotion.Animate(
            this, ScaleTransform.ScaleXProperty, () => _pressScale.ScaleX, value => _pressScale.ScaleX = value,
            scale, AvaloniaMotionTokens.PressMilliseconds,
            reducedMotion: () => _reducedMotion());
        AvaloniaUiMotion.Animate(
            this, ScaleTransform.ScaleYProperty, () => _pressScale.ScaleY, value => _pressScale.ScaleY = value,
            scale, AvaloniaMotionTokens.PressMilliseconds,
            reducedMotion: () => _reducedMotion());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == HoverOpacityProperty
            || e.Property == PillScaleProperty
            || e.Property == EnterProgressProperty)
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
        if (_node.TextInput is not null) return new AvaloniaUiSceneTextAutomationPeer(this);
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

        return _node.Role == XsrUiSemanticRole.Button || _node.IsClickable
            ? new AvaloniaUiSceneInvokeAutomationPeer(this)
            : new AvaloniaUiSceneNodeAutomationPeer(this);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect rect = new(Bounds.Size);
        XsrUiVisualStyleSnapshot style = _node.VisualStyle;
        double enter = EnterProgress;

        // Disabled nodes keep their layout and draw dimmed, so position and hit routing stay
        // stable while the state reads as unavailable.
        if (!_node.IsEnabled && _node.IsAccessible)
        {
            using (context.PushOpacity(0.45))
            {
                DrawWithEnter(context, rect, style, enter);
            }

            return;
        }

        if (enter < 1)
        {
            using (context.PushOpacity(Math.Clamp(enter, 0, 1)))
            {
                DrawContent(context, rect, style);
            }

            return;
        }

        DrawContent(context, rect, style);
    }

    private void DrawWithEnter(DrawingContext context, Rect rect, XsrUiVisualStyleSnapshot style, double enter)
    {
        if (enter < 1)
        {
            using (context.PushOpacity(Math.Clamp(enter, 0, 1)))
            {
                DrawContent(context, rect, style);
            }

            return;
        }

        DrawContent(context, rect, style);
    }

    private void DrawContent(DrawingContext context, Rect rect, XsrUiVisualStyleSnapshot style)
    {
        if (style.HoverExpand)
        {
            DrawHoverPill(context, rect, style);
            return;
        }

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
        if (_node.TextInput is { } textInput)
        {
            DrawTextInput(context, rect, style, textInput);
            return;
        }

        // Collapsed rail items center the vector icon and hide the label; expanded rail rows
        // keep the icon exactly where the collapsed state centered it (past the selection
        // pill) and reveal the label after it. Any other icon-bearing node without text (or a
        // collapsed rail item) centers its icon too.
        bool collapsedRailItem = style.NavigationLayout
            && Bounds.Width <= CollapsedRailCenteringWidth;
        bool textVisible = _node.Text is { Length: > 0 } && !collapsedRailItem;
        bool railRow = textVisible && style.NavigationLayout;
        if (DrawRaster(context, rect) || (_node.ImageSource is { Length: > 0 } avatarSource && AvaloniaUiAvatars.TryDraw(context, avatarSource, rect)))
        {
            if (textVisible) DrawText(context, style, 0);
        }
        else if (_node.ImageSource is { Length: > 0 } iconSource
            && AvaloniaUiIcons.TryGetGeometry(iconSource, out IReadOnlyList<Geometry> iconPaths))
        {
            double iconSize = _node.Role == XsrUiSemanticRole.Image
                ? Math.Min(Bounds.Width, Bounds.Height) : NavigationIconSize;
            double scale = iconSize / AvaloniaUiIcons.ViewBoxSize;
            double iconX = railRow
                ? RailRowIconOffset
                : textVisible
                    ? 0
                    : Math.Max(0, (Bounds.Width - iconSize) / 2);
            double iconY = Math.Max(0, (Bounds.Height - iconSize) / 2);
            IBrush iconBrush = Brush(style.Foreground) ?? new SolidColorBrush(Colors.White);
            Pen iconPen = new(iconBrush, AvaloniaUiIcons.StrokeWidth)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            using (context.PushTransform(
                Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(iconX, iconY)))
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

        if (_node.IsFocusVisible)
        {
            context.DrawRectangle(
                null,
                new Pen(Brush(new XsrUiColor(11, 91, 203))!, 2),
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
        if (style.WrapText && Bounds.Width > x)
            formatted.MaxTextWidth = Bounds.Width - x;
        double alignedX = style.TextAlignment switch
        {
            XsrUiTextAlignment.Center => Math.Max(x, (Bounds.Width - formatted.Width) / 2),
            XsrUiTextAlignment.End => Math.Max(x, Bounds.Width - formatted.Width),
            _ => x,
        };
        double y = Math.Max(0, (Bounds.Height - formatted.Height) / 2);
        context.DrawText(formatted, new Point(alignedX, y));
    }

    /// <summary>
    /// Hover-expanding capsule: at rest an icon circle pinned to the node's right edge; on
    /// hover/focus the pill grows leftward from that circle and the node's scene text fades in
    /// beside the icon. Layout and hit regions are already the presented width in the scene.
    /// </summary>
    private void DrawHoverPill(DrawingContext context, Rect rect, XsrUiVisualStyleSnapshot style)
    {
        double progress = Math.Clamp(_node.CapsuleExpansionProgress, 0, 1);
        double pillHeight = rect.Height;
        double fontSize = style.FontSize > 0 ? style.FontSize : 13;
        double pillWidth = rect.Width;
        double pillX = rect.Right - pillWidth;
        var pillRect = new Rect(pillX, rect.Y, pillWidth, rect.Height);
        IBrush? pillBackground = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(245, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(style.Background.Alpha, style.Background.Red,
                    style.Background.Green, style.Background.Blue), 1),
            ],
        };
        IPen? pillBorder = style.BorderWidth > 0 ? new Pen(Brush(style.Border), style.BorderWidth) : null;
        context.DrawRectangle(
            pillBackground,
            pillBorder,
            new RoundedRect(pillRect, new CornerRadius(pillHeight / 2)));

        // Keep the icon anchored at the trailing edge throughout expansion and reversal.
        double iconX = rect.Right - (pillHeight + NavigationIconSize) / 2;
        double iconY = Math.Max(0, (rect.Height - NavigationIconSize) / 2);
        if (_node.ImageSource is { Length: > 0 } iconSource
            && AvaloniaUiIcons.TryGetGeometry(iconSource, out IReadOnlyList<Geometry> iconPaths))
        {
            double scale = NavigationIconSize / AvaloniaUiIcons.ViewBoxSize;
            IBrush iconBrush = Brush(style.Foreground) ?? new SolidColorBrush(Colors.White);
            Pen iconPen = new(iconBrush, AvaloniaUiIcons.StrokeWidth)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            using (context.PushTransform(
                Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(iconX, iconY)))
            {
                foreach (Geometry path in iconPaths)
                {
                    context.DrawGeometry(null, iconPen, path);
                }
            }
        }
        else if (_node.Text is { Length: > 0 } glyph)
        {
            IBrush glyphBrush = Brush(style.Foreground) ?? new SolidColorBrush(Colors.White);
            FormattedText glyphText = new(
                glyph,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeightFor(style)),
                Math.Max(12, style.FontSize > 0 ? style.FontSize : 14),
                glyphBrush);
            double glyphX = rect.Right - ((pillHeight + glyphText.Width) / 2);
            double glyphY = Math.Max(0, (rect.Height - glyphText.Height) / 2);
            context.DrawText(glyphText, new Point(glyphX, glyphY));
        }

        if (progress > 0.01 && _node.Text is { Length: > 0 } label)
        {
            byte alpha = (byte)Math.Round(255 * progress);
            IBrush labelBrush = new SolidColorBrush(Color.FromArgb(
                alpha,
                style.Foreground.Red,
                style.Foreground.Green,
                style.Foreground.Blue));
            FormattedText labelText = new(
                label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeightFor(style)),
                fontSize,
                labelBrush);
            double labelX = iconX - CapsuleCaptionIconGap - labelText.Width;
            double labelY = Math.Max(0, (rect.Height - labelText.Height) / 2);
            using (context.PushClip(new Rect(pillX + 8, rect.Y,
                Math.Max(0, iconX - CapsuleCaptionIconGap - (pillX + 8)), rect.Height)))
                context.DrawText(labelText, new Point(labelX, labelY));
        }

        if (_node.IsFocusVisible)
            context.DrawRectangle(null, new Pen(Brush(new XsrUiColor(11, 91, 203))!, 2),
                new RoundedRect(pillRect.Deflate(2), new CornerRadius(pillHeight / 2 - 2)));
    }

    private static FontWeight FontWeightFor(XsrUiVisualStyleSnapshot style) =>
        style.FontWeight >= 600 ? FontWeight.SemiBold : FontWeight.Normal;

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
        XsrUiSemanticRole.TextInput => AutomationControlType.Edit,
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
