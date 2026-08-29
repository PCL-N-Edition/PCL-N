using System.Globalization;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next;

/// <summary>
/// The UI.Next runtime: runs the deterministic measure/arrange layout pass over dirty subtrees,
/// produces the immutable render scene, and hands it to the backend commit boundary. It reads
/// state through <see cref="XsrStateStore"/> and emits intent through
/// <see cref="IXsrUiIntentSink"/>; it never resolves services or touches a backend type.
/// </summary>
public sealed class XsrUiRenderer
{
    private readonly XsrUiTree _tree;
    private readonly XsrStateStore _state;
    private readonly IXsrUiIntentSink? _sink;
    private readonly XsrUiStateBridge? _stateBridge;
    private XsrUiEntityId _hovered;
    private XsrUiEntityId _pressed;
    private XsrUiEntityId _focused;
    private readonly Dictionary<int, XsrUiSize> _desiredSizes = [];
    private readonly Dictionary<int, XsrUiSize> _stackContentSizes = [];
    private readonly Dictionary<int, XsrUiRect> _paintRects = [];
    private readonly Dictionary<int, XsrUiRect> _arrangedSlots = [];
    private readonly HashSet<int> _measuredThisPass = [];
    private XsrUiScene? _scene;
    private long _sceneVersion;
    private XsrUiEntityId _root;
    private XsrUiSize _viewport = new(800, 600);
    private int _layoutVisits;

    public XsrUiRenderer(
        XsrUiTree tree,
        XsrStateStore state,
        IXsrUiIntentSink? sink = null,
        XsrUiStateBridge? stateBridge = null)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _sink = sink;
        _stateBridge = stateBridge;
    }

    /// <summary>
    /// Gets or sets whether animation-heavy presentation should reduce motion. The flag is a
    /// presentation contract; backends and animation drivers must honor it.
    /// </summary>
    public bool ReducedMotion { get; set; }

    /// <summary>
    /// Gets the currently focused entity, or an unassigned handle.
    /// </summary>
    public XsrUiEntityId Focused => _focused;

    /// <summary>
    /// Gets or sets the size the root entity is arranged into.
    /// </summary>
    public XsrUiSize Viewport
    {
        get => _viewport;
        set
        {
            if (_root.IsAssigned && value != _viewport)
            {
                _tree.MarkDirty(_root, XsrUiDirtyKinds.Layout);
            }

            _viewport = value;
        }
    }

    /// <summary>
    /// Gets the version of the last produced scene; unchanged renders reuse the version.
    /// </summary>
    public long SceneVersion => _scene?.Version ?? 0;

    /// <summary>
    /// Gets how many entities the last render measured. Layout touches only dirty subtrees.
    /// </summary>
    public int LastLayoutVisits { get; private set; }

    /// <summary>
    /// Sets the root entity of the rendered scene.
    /// </summary>
    public void SetRoot(XsrUiEntityId root)
    {
        if (!_tree.IsAlive(root))
        {
            throw new InvalidOperationException($"The renderer root '{root}' is not alive.");
        }

        _root = root;
        _tree.MarkDirty(root, XsrUiDirtyKinds.Structure);
    }

    /// <summary>
    /// Produces the current render scene. A clean tree returns the cached scene unchanged;
    /// any dirty subtree relayouts exactly that subtree before the scene rebuilds.
    /// </summary>
    public XsrUiScene Render()
    {
        if (!_root.IsAssigned)
        {
            throw new InvalidOperationException("The renderer has no root entity.");
        }

        // The state bridge queues publisher-thread changes; the render thread drains them here,
        // so coalesced and derived state land in this frame's dirty set before the clean check.
        _stateBridge?.DrainAndMark(_state);

        if (_scene is not null && !_tree.HasDirtySubtree(_root))
        {
            return _scene;
        }

        _layoutVisits = 0;
        _measuredThisPass.Clear();
        if (SubtreeHasKind(_root, XsrUiDirtyKinds.Structure))
        {
            PruneDeadCacheEntries();
        }


        Layout(_root, new XsrUiRect(0, 0, _viewport.Width, _viewport.Height));

        XsrUiSceneNode[] nodes = CollectNodes(_root, depth: 0);
        _sceneVersion++;
        _scene = new XsrUiScene(_sceneVersion, nodes);
        LastLayoutVisits = _layoutVisits;

        _tree.Walk(_root, entity =>
        {
            _tree.ClearDirty(entity);
            return true;
        });

        return _scene;
    }

    private bool NeedsLayout(XsrUiEntityId entity)
    {
        XsrUiDirtyKinds relevant = XsrUiDirtyKinds.Structure
            | XsrUiDirtyKinds.Layout
            | XsrUiDirtyKinds.State;
        return (_tree.DirtyKinds(entity) & relevant) != 0;
    }

    private bool SubtreeHasKind(XsrUiEntityId root, XsrUiDirtyKinds kind)
    {
        bool found = false;
        _tree.Walk(
            root,
            entity =>
            {
                if (_tree.DirtyKinds(entity).HasFlag(kind))
                {
                    found = true;
                }

                return !found;
            },
            entity => _tree.HasDirtySubtree(entity));
        return found;
    }

    private void PruneDeadCacheEntries()
    {
        foreach (int id in _desiredSizes.Keys.Where(id => !_tree.IsIndexAlive(id)).ToArray())
        {
            _ = _desiredSizes.Remove(id);
        }

        foreach (int id in _paintRects.Keys.Where(id => !_tree.IsIndexAlive(id)).ToArray())
        {
            _ = _paintRects.Remove(id);
        }

        foreach (int id in _arrangedSlots.Keys.Where(id => !_tree.IsIndexAlive(id)).ToArray())
        {
            _ = _arrangedSlots.Remove(id);
        }

        foreach (int id in _stackContentSizes.Keys.Where(id => !_tree.IsIndexAlive(id)).ToArray())
        {
            _ = _stackContentSizes.Remove(id);
        }
    }

    private void Layout(XsrUiEntityId entity, XsrUiRect slot)
    {
        // Measure caching and arrange caching are separate concerns: a clean subtree keeps its
        // measured sizes, but any entity whose input slot moved must re-arrange, even when the
        // entity itself is clean — otherwise siblings keep stale coordinates.
        bool subtreeClean = !_tree.HasDirtyLayoutSubtree(entity);
        bool slotUnchanged = _arrangedSlots.TryGetValue(entity.Index, out XsrUiRect previous) && previous == slot;
        bool arranged = _paintRects.ContainsKey(entity.Index);
        if (subtreeClean && slotUnchanged && arranged)
        {
            return;
        }

        XsrUiSize desired = Measure(entity, new XsrUiSize(slot.Width, slot.Height));
        Arrange(entity, slot, desired);
    }

    private XsrUiSize Measure(XsrUiEntityId entity, XsrUiSize available)
    {
        // Subtrees without layout-relevant dirt keep the previous pass's desired size; anything
        // with a dirty descendant re-aggregates so containers pick up new child measurements.
        if (!_tree.HasDirtyLayoutSubtree(entity) && _desiredSizes.TryGetValue(entity.Index, out XsrUiSize cached))
        {
            return cached;
        }

        if (_measuredThisPass.Add(entity.Index))
        {
            _layoutVisits++;
        }
        XsrUiElement? element = _tree.GetComponent<XsrUiElement>(entity);
        XsrUiThickness padding = element?.Padding ?? default;
        XsrUiThickness margin = element?.Margin ?? default;

        double contentWidth = Math.Max(0, available.Width - margin.Horizontal);
        double contentHeight = Math.Max(0, available.Height - margin.Vertical);
        double width = 0;
        double height = 0;

        XsrUiStackPanel? stack = _tree.GetComponent<XsrUiStackPanel>(entity);
        if (stack is not null)
        {
            double main = 0;
            double cross = 0;
            int children = 0;
            foreach (XsrUiEntityId child in _tree.Children(entity))
            {
                if (!IsVisible(child))
                {
                    continue;
                }

                XsrUiSize childAvailable = new(
                    Math.Max(0, contentWidth - padding.Horizontal),
                    Math.Max(0, contentHeight - padding.Vertical));
                XsrUiSize childDesired = Measure(child, childAvailable);
                bool isMainWidth = stack.Direction == XsrUiOrientation.Horizontal;
                double childMain = isMainWidth ? childDesired.Width : childDesired.Height;
                double childCross = isMainWidth ? childDesired.Height : childDesired.Width;
                main += childMain;
                cross = Math.Max(cross, childCross);
                children++;
            }

            main += Math.Max(0, children - 1) * stack.Spacing;
            width = stack.Direction == XsrUiOrientation.Horizontal ? main : cross;
            height = stack.Direction == XsrUiOrientation.Horizontal ? cross : main;
            width += padding.Horizontal;
            height += padding.Vertical;
            _stackContentSizes[entity.Index] = new XsrUiSize(width, height);
        }

        // Explicit sizes constrain the content box; padding adds on top of them.
        if (element?.Width is { } explicitWidth)
        {
            width = explicitWidth + padding.Horizontal;
        }

        if (element?.Height is { } explicitHeight)
        {
            height = explicitHeight + padding.Vertical;
        }

        XsrUiSize desired = new(width, height);
        _desiredSizes[entity.Index] = desired;
        return desired;
    }

    private void Arrange(XsrUiEntityId entity, XsrUiRect slot, XsrUiSize desired)
    {
        XsrUiElement? element = _tree.GetComponent<XsrUiElement>(entity);
        XsrUiThickness margin = element?.Margin ?? default;
        XsrUiThickness padding = element?.Padding ?? default;

        double slotX = slot.X + margin.Left;
        double slotY = slot.Y + margin.Top;
        double slotW = Math.Max(0, slot.Width - margin.Horizontal);
        double slotH = Math.Max(0, slot.Height - margin.Vertical);

        double contentDesiredW = element?.Width ?? Math.Max(0, desired.Width - padding.Horizontal);
        double contentDesiredH = element?.Height ?? Math.Max(0, desired.Height - padding.Vertical);
        double borderDesiredW = contentDesiredW + padding.Horizontal;
        double borderDesiredH = contentDesiredH + padding.Vertical;

        // Stretch fills the slot only when no explicit size constrains the axis.
        bool stretchW = (element?.HorizontalAlignment ?? XsrUiAlignment.Stretch) == XsrUiAlignment.Stretch
            && element?.Width is null;
        bool stretchH = (element?.VerticalAlignment ?? XsrUiAlignment.Stretch) == XsrUiAlignment.Stretch
            && element?.Height is null;

        double borderW = stretchW ? slotW : Math.Min(slotW, borderDesiredW);
        double borderH = stretchH ? slotH : Math.Min(slotH, borderDesiredH);
        XsrUiAlignment horizontal = element?.HorizontalAlignment ?? XsrUiAlignment.Stretch;
        XsrUiAlignment vertical = element?.VerticalAlignment ?? XsrUiAlignment.Stretch;
        double borderX = slotX + (horizontal switch
        {
            XsrUiAlignment.Center => (slotW - borderW) / 2,
            XsrUiAlignment.End => slotW - borderW,
            _ => 0,
        });
        double borderY = slotY + (vertical switch
        {
            XsrUiAlignment.Center => (slotH - borderH) / 2,
            XsrUiAlignment.End => slotH - borderH,
            _ => 0,
        });

        double contentX = borderX + padding.Left;
        double contentY = borderY + padding.Top;
        double contentWidth = Math.Max(0, borderW - padding.Horizontal);
        double contentHeight = Math.Max(0, borderH - padding.Vertical);
        _arrangedSlots[entity.Index] = slot;
        _paintRects[entity.Index] = new XsrUiRect(contentX, contentY, contentWidth, contentHeight);

        XsrUiStackPanel? stack = _tree.GetComponent<XsrUiStackPanel>(entity);
        if (stack is null)
        {
            // Without a stack component, children overlap the full content rect in attach order.
            foreach (XsrUiEntityId child in _tree.Children(entity))
            {
                if (IsVisible(child))
                {
                    Layout(child, new XsrUiRect(contentX, contentY, contentWidth, contentHeight));
                }
            }

            return;
        }

        XsrUiScroll? scroll = _tree.GetComponent<XsrUiScroll>(entity);
        if (scroll is not null)
        {
            // Clamp scroll offsets to the measured content extent; children are placed into
            // slots shifted by the offset, so hit testing follows scrolled content for free.
            XsrUiSize content = _stackContentSizes.TryGetValue(entity.Index, out XsrUiSize stackSize)
                ? stackSize
                : desired;
            double maxOffsetX = Math.Max(0, content.Width - contentWidth);
            double maxOffsetY = Math.Max(0, content.Height - contentHeight);
            scroll.OffsetX = Math.Clamp(scroll.OffsetX, 0, maxOffsetX);
            scroll.OffsetY = Math.Clamp(scroll.OffsetY, 0, maxOffsetY);
        }

        double scrollX = scroll?.OffsetX ?? 0;
        double scrollY = scroll?.OffsetY ?? 0;
        double cursor = (stack.Direction == XsrUiOrientation.Vertical ? contentY : contentX) - (stack.Direction == XsrUiOrientation.Vertical ? scrollY : scrollX);
        double crossAvailable = stack.Direction == XsrUiOrientation.Vertical ? contentWidth : contentHeight;
        double crossOrigin = stack.Direction == XsrUiOrientation.Vertical ? contentX : contentY;
        double crossScroll = stack.Direction == XsrUiOrientation.Vertical ? scrollX : scrollY;
        foreach (XsrUiEntityId child in _tree.Children(entity))
        {
            if (!IsVisible(child))
            {
                continue;
            }

            XsrUiSize childDesired = _desiredSizes.TryGetValue(child.Index, out XsrUiSize size)
                ? size
                : Measure(child, new XsrUiSize(contentWidth, contentHeight));
            XsrUiElement? childElement = _tree.GetComponent<XsrUiElement>(child);
            XsrUiThickness childMargin = childElement?.Margin ?? default;
            double childMain = stack.Direction == XsrUiOrientation.Vertical
                ? childDesired.Height + childMargin.Vertical
                : childDesired.Width + childMargin.Horizontal;
            XsrUiAlignment crossAlignment = stack.Direction == XsrUiOrientation.Vertical
                ? childElement?.HorizontalAlignment ?? XsrUiAlignment.Stretch
                : childElement?.VerticalAlignment ?? XsrUiAlignment.Stretch;

            double crossSize = crossAlignment == XsrUiAlignment.Stretch
                ? crossAvailable
                : (stack.Direction == XsrUiOrientation.Vertical
                    ? childDesired.Width + childMargin.Horizontal
                    : childDesired.Height + childMargin.Vertical);
            double crossOffset = crossAlignment switch
            {
                XsrUiAlignment.Center => (crossAvailable - crossSize) / 2,
                XsrUiAlignment.End => crossAvailable - crossSize,
                _ => 0,
            };

            XsrUiRect childSlot = stack.Direction == XsrUiOrientation.Vertical
                ? new XsrUiRect(crossOrigin + crossOffset - crossScroll, cursor, crossSize, childMain)
                : new XsrUiRect(cursor, crossOrigin + crossOffset - crossScroll, childMain, crossSize);

            Layout(child, childSlot);
            cursor += childMain + stack.Spacing;
        }
    }

    private bool IsVisible(XsrUiEntityId entity)
    {
        XsrUiElement? element = _tree.GetComponent<XsrUiElement>(entity);
        if (element?.BoundVisibility is { } bound && bound.IsAssigned)
        {
            return _state.ReadAppliedValue(bound) is bool visible && visible;
        }

        return element?.IsVisible ?? true;
    }

    /// <summary>
    /// Resolves the top-most scene entity at one point. Hit testing reads the last produced
    /// scene, so it never triggers layout or state reads.
    /// </summary>
    public XsrUiEntityId HitTest(XsrUiPoint point)
    {
        if (_scene is null)
        {
            return default;
        }

        for (int index = _scene.Count - 1; index >= 0; index--)
        {
            XsrUiSceneNode node = _scene[index];
            if (node.Rect.Contains(point))
            {
                return node.Entity;
            }
        }

        return default;
    }

    /// <summary>
    /// Routes a pointer press. Returns true when a clickable entity absorbed it.
    /// </summary>
    public bool PointerPressed(XsrUiPoint point)
    {
        XsrUiEntityId entity = HitTest(point);
        XsrUiInput? input = _tree.GetComponent<XsrUiInput>(entity);
        if (entity.IsAssigned && input is { Clickable: true })
        {
            input.IsPressed = true;
            _pressed = entity;
            _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Routes a pointer release. Releasing over the pressed entity activates its command binding
    /// and emits one intent with a renderer-produced correlation ID.
    /// </summary>
    public bool PointerReleased(XsrUiPoint point)
    {
        if (!_pressed.IsAssigned || !_tree.IsAlive(_pressed))
        {
            _pressed = default;
            return false;
        }

        XsrUiEntityId pressed = _pressed;
        XsrUiInput? input = _tree.GetComponent<XsrUiInput>(pressed);
        if (input is not null)
        {
            input.IsPressed = false;
        }
        _pressed = default;
        _tree.MarkDirty(pressed, XsrUiDirtyKinds.Paint);
        return HitTest(point).Equals(pressed) && Activate(pressed);
    }

    /// <summary>
    /// Routes a pointer move, updating hover state on input entities.
    /// </summary>
    public bool PointerMoved(XsrUiPoint point)
    {
        XsrUiEntityId entity = HitTest(point);
        XsrUiInput? input = entity.IsAssigned ? _tree.GetComponent<XsrUiInput>(entity) : null;
        bool overInput = input is not null;

        if (_hovered.IsAssigned && !_tree.IsAlive(_hovered))
        {
            _hovered = default;
        }

        if (_hovered.IsAssigned && !_hovered.Equals(entity))
        {
            XsrUiInput? previous = _tree.GetComponent<XsrUiInput>(_hovered);
            if (previous is not null)
            {
                previous.IsHovered = false;
                _tree.MarkDirty(_hovered, XsrUiDirtyKinds.Paint);
            }
        }

        _hovered = overInput ? entity : default;
        if (input is not null && !input.IsHovered)
        {
            input.IsHovered = true;
            _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            return true;
        }

        return overInput;
    }

    /// <summary>
    /// Moves keyboard focus to the next focusable entity in scene (tab) order, wrapping around.
    /// Returns false when no focusable entity exists.
    /// </summary>
    public bool FocusNext()
    {
        if (_scene is null)
        {
            return false;
        }

        List<XsrUiEntityId> focusable = [];
        for (int index = 0; index < _scene.Count; index++)
        {
            if (_tree.GetComponent<XsrUiInput>(_scene[index].Entity)?.Focusable == true)
            {
                focusable.Add(_scene[index].Entity);
            }
        }

        if (focusable.Count == 0)
        {
            return false;
        }

        if (_focused.IsAssigned && !_tree.IsAlive(_focused))
        {
            _focused = default;
        }

        int currentIndex = focusable.FindIndex(entity => entity.Equals(_focused));
        int next = (currentIndex + 1) % focusable.Count;
        return Focus(focusable[next]);
    }

    /// <summary>
    /// Sets keyboard focus to one focusable entity.
    /// </summary>
    public bool Focus(XsrUiEntityId entity)
    {
        if (!_tree.IsAlive(entity))
        {
            _focused = default;
            return false;
        }

        XsrUiInput? input = _tree.GetComponent<XsrUiInput>(entity);
        if (input is not { Focusable: true })
        {
            return false;
        }

        if (_focused.IsAssigned && !_focused.Equals(entity))
        {
            XsrUiInput? previous = _tree.GetComponent<XsrUiInput>(_focused);
            if (previous is not null)
            {
                previous.IsFocused = false;
                _tree.MarkDirty(_focused, XsrUiDirtyKinds.Paint);
            }
        }

        _focused = entity;
        input.IsFocused = true;
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        return true;
    }

    /// <summary>
    /// Routes one wheel scroll to the nearest scroll container under the point, walking up the
    /// hierarchy. Offsets clamp during the next arrange.
    /// </summary>
    public bool PointerScroll(XsrUiPoint point, double deltaY, double deltaX = 0)
    {
        XsrUiEntityId entity = HitTest(point);
        while (entity.IsAssigned && _tree.IsAlive(entity))
        {
            if (_tree.GetComponent<XsrUiScroll>(entity) is { } scroll)
            {
                scroll.OffsetX = Math.Max(0, scroll.OffsetX + deltaX);
                scroll.OffsetY = Math.Max(0, scroll.OffsetY + deltaY);
                _tree.MarkDirty(entity, XsrUiDirtyKinds.Layout);
                return true;
            }

            entity = _tree.Parent(entity);
        }

        return false;
    }

    /// <summary>
    /// Routes one keyboard key: Tab moves focus, Enter and Space activate the focused entity,
    /// Back pops navigation through the intent sink as a plain false (navigation is owned by
    /// composition). Returns true when the key was handled.
    /// </summary>
    public bool HandleKey(XsrUiKey key)
    {
        return key switch
        {
            XsrUiKey.Tab => FocusNext(),
            XsrUiKey.Enter or XsrUiKey.Space => _focused.IsAssigned && Activate(_focused),
            _ => false,
        };
    }

    private bool Activate(XsrUiEntityId entity)
    {
        if (!_tree.IsAlive(entity))
        {
            return false;
        }

        XsrUiCommandBinding? binding = _tree.GetComponent<XsrUiCommandBinding>(entity);
        if (binding is null || _sink is null || !binding.Command.IsAssigned)
        {
            return false;
        }

        _sink.Emit(binding.Command, entity, XsrCorrelationId.Create());
        return true;
    }

    private XsrUiSceneNode[] CollectNodes(XsrUiEntityId root, int depth)
    {
        List<XsrUiSceneNode> nodes = [];
        CollectNode(root, depth, nodes);
        return [.. nodes];
    }

    private void CollectNode(XsrUiEntityId entity, int depth, List<XsrUiSceneNode> nodes)
    {
        if (!IsVisible(entity))
        {
            return;
        }

        XsrUiSemantic? semantic = _tree.GetComponent<XsrUiSemantic>(entity);
        XsrUiText? text = _tree.GetComponent<XsrUiText>(entity);
        string? content = text?.Content;
        if (text?.BoundState is { } bound && bound.IsAssigned)
        {
            object? value = _state.ReadAppliedValue(bound);
            content = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        XsrUiRect rect = _paintRects.TryGetValue(entity.Index, out XsrUiRect paintRect)
            ? paintRect
            : default;
        XsrUiAnimation? animation = _tree.GetComponent<XsrUiAnimation>(entity);
        XsrUiImage? image = _tree.GetComponent<XsrUiImage>(entity);
        nodes.Add(new XsrUiSceneNode(
            entity,
            rect,
            depth,
            semantic?.Role ?? XsrUiSemanticRole.None,
            semantic?.Label,
            content,
            image?.Source,
            _tree.GetComponent<XsrUiInput>(entity)?.IsFocused ?? false,
            animation?.Progress,
            animation is { Keyframes.Count: > 0 } ? animation.Value : null));

        foreach (XsrUiEntityId child in _tree.Children(entity))
        {
            CollectNode(child, depth + 1, nodes);
        }
    }
}
