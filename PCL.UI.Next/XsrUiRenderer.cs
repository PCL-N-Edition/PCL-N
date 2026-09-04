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
public sealed partial class XsrUiRenderer
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
    /// Render-thread composition hook for materializing state-backed PXML templates. Runs before
    /// state dirt is drained and before layout; publishers must never call this hook themselves.
    /// </summary>
    public event EventHandler? FramePreparing;

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
        FramePreparing?.Invoke(this, EventArgs.Empty);
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

        // Hidden subtrees were not measured this frame. Their dirt is still acknowledged below,
        // so discard stale layout caches first: a later visibility change must measure new rows
        // rather than reuse the empty/old list's desired size and recycled entity rectangles.
        _tree.Walk(_root, entity =>
        {
            if (_tree.HasDirtyLayoutSubtree(entity) && !_measuredThisPass.Contains(entity.Index))
            {
                _desiredSizes.Remove(entity.Index);
                _stackContentSizes.Remove(entity.Index);
                _arrangedSlots.Remove(entity.Index);
                _paintRects.Remove(entity.Index);
            }
            return true;
        }, entity => _tree.HasDirtyLayoutSubtree(entity));

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

        if (_tree.GetComponent<XsrUiText>(entity) is { } text)
        {
            XsrUiSize textSize = MeasureText(ResolveText(text), _tree.GetComponent<XsrUiVisualStyle>(entity)?.FontSize ?? 0);
            width = textSize.Width;
            height = textSize.Height;
        }

        XsrUiStackPanel? stack = _tree.GetComponent<XsrUiStackPanel>(entity);
        if (_tree.GetComponent<XsrUiPager>(entity) is not null)
        {
            foreach (XsrUiEntityId child in _tree.Children(entity).Where(IsVisible))
            {
                XsrUiSize size = Measure(child, new XsrUiSize(contentWidth, contentHeight));
                width = Math.Max(width, size.Width);
                height = Math.Max(height, size.Height);
            }
        }
        else if (stack is not null)
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
                XsrUiThickness childMargin = _tree.GetComponent<XsrUiElement>(child)?.Margin ?? default;
                bool isMainWidth = stack.Direction == XsrUiOrientation.Horizontal;
                double childMain = isMainWidth
                    ? childDesired.Width + childMargin.Horizontal
                    : childDesired.Height + childMargin.Vertical;
                double childCross = isMainWidth
                    ? childDesired.Height + childMargin.Vertical
                    : childDesired.Width + childMargin.Horizontal;
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
        if (PresentedWidth(entity, element) is { } explicitWidth)
        {
            width = explicitWidth + padding.Horizontal;
        }

        if (element?.Height is { } explicitHeight)
        {
            height = explicitHeight + padding.Vertical;
        }

        width = ConstrainDimension(width, element?.MinWidth, element?.MaxWidth, padding.Horizontal);
        height = ConstrainDimension(height, element?.MinHeight, element?.MaxHeight, padding.Vertical);

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

        double contentDesiredW = PresentedWidth(entity, element) ?? Math.Max(0, desired.Width - padding.Horizontal);
        double contentDesiredH = element?.Height ?? Math.Max(0, desired.Height - padding.Vertical);
        double borderDesiredW = ConstrainDimension(
            contentDesiredW + padding.Horizontal,
            element?.MinWidth,
            element?.MaxWidth,
            padding.Horizontal);
        double borderDesiredH = ConstrainDimension(
            contentDesiredH + padding.Vertical,
            element?.MinHeight,
            element?.MaxHeight,
            padding.Vertical);

        // Stretch fills the slot only when no explicit size constrains the axis.
        bool stretchW = (element?.HorizontalAlignment ?? XsrUiAlignment.Stretch) == XsrUiAlignment.Stretch
            && element?.Width is null;
        bool stretchH = (element?.VerticalAlignment ?? XsrUiAlignment.Stretch) == XsrUiAlignment.Stretch
            && element?.Height is null;

        double borderW = Math.Min(
            slotW,
            ConstrainDimension(
                stretchW ? slotW : borderDesiredW,
                element?.MinWidth,
                element?.MaxWidth,
                padding.Horizontal));
        double borderH = Math.Min(
            slotH,
            ConstrainDimension(
                stretchH ? slotH : borderDesiredH,
                element?.MinHeight,
                element?.MaxHeight,
                padding.Vertical));
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

        if (_tree.GetComponent<XsrUiPager>(entity) is { } pager)
        {
            XsrUiEntityId[] pages = [.. _tree.Children(entity).Where(IsVisible)];
            pager.PageCount = pages.Length;
            pager.PageIndex = Math.Clamp(pager.PageIndex, 0, Math.Max(0, pages.Length - 1));
            if (ReducedMotion && !pager.IsDragging) pager.Position = pager.PageIndex;
            for (int i = 0; i < pages.Length; i++)
                Layout(pages[i], new XsrUiRect(contentX, contentY + (i - pager.Position) * contentHeight,
                    contentWidth, contentHeight));
            return;
        }

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
        XsrUiEntityId[] visibleChildren = [.. _tree.Children(entity).Where(IsVisible)];
        double availableMain = stack.Direction == XsrUiOrientation.Vertical ? contentHeight : contentWidth;
        Dictionary<int, double> weightedMainSizes = AllocateWeightedMainSizes(
            stack.Direction,
            visibleChildren,
            availableMain,
            stack.Spacing);
        bool hasWeightedChildren = weightedMainSizes.Count != 0;
        double consumedMain = 0;
        for (int childIndex = 0; childIndex < visibleChildren.Length; childIndex++)
        {
            XsrUiEntityId child = visibleChildren[childIndex];

            XsrUiSize childDesired = _desiredSizes.TryGetValue(child.Index, out XsrUiSize size)
                ? size
                : Measure(child, new XsrUiSize(contentWidth, contentHeight));
            XsrUiElement? childElement = _tree.GetComponent<XsrUiElement>(child);
            XsrUiThickness childMargin = childElement?.Margin ?? default;
            double childMain = weightedMainSizes.TryGetValue(child.Index, out double weightedMain)
                ? weightedMain
                : stack.Direction == XsrUiOrientation.Vertical
                    ? childDesired.Height + childMargin.Vertical
                    : childDesired.Width + childMargin.Horizontal;
            if (!hasWeightedChildren
                && stack.StretchLastChild
                && childIndex == visibleChildren.Length - 1)
            {
                childMain = Math.Max(childMain, Math.Max(0, availableMain - consumedMain));
            }
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
            consumedMain += childMain + stack.Spacing;
        }
    }

    private Dictionary<int, double> AllocateWeightedMainSizes(
        XsrUiOrientation direction,
        XsrUiEntityId[] children,
        double availableMain,
        double spacing)
    {
        List<WeightedSlot> weighted = [];
        double remaining = Math.Max(0, availableMain - Math.Max(0, children.Length - 1) * spacing);
        foreach (XsrUiEntityId child in children)
        {
            XsrUiElement? element = _tree.GetComponent<XsrUiElement>(child);
            double weight = Math.Max(0, element?.Weight ?? 0);
            XsrUiSize desired = _desiredSizes.TryGetValue(child.Index, out XsrUiSize cached)
                ? cached
                : default;
            XsrUiThickness margin = element?.Margin ?? default;
            if (weight == 0)
            {
                remaining -= direction == XsrUiOrientation.Vertical
                    ? desired.Height + margin.Vertical
                    : desired.Width + margin.Horizontal;
                continue;
            }

            XsrUiThickness padding = element?.Padding ?? default;
            double minimum = direction == XsrUiOrientation.Vertical
                ? ConstraintWithBox(element?.MinHeight, padding.Vertical, margin.Vertical, fallback: 0)
                : ConstraintWithBox(element?.MinWidth, padding.Horizontal, margin.Horizontal, fallback: 0);
            double maximum = direction == XsrUiOrientation.Vertical
                ? ConstraintWithBox(element?.MaxHeight, padding.Vertical, margin.Vertical, double.PositiveInfinity)
                : ConstraintWithBox(element?.MaxWidth, padding.Horizontal, margin.Horizontal, double.PositiveInfinity);
            weighted.Add(new WeightedSlot(child.Index, weight, minimum, Math.Max(minimum, maximum)));
        }

        if (weighted.Count == 0)
        {
            return [];
        }

        Dictionary<int, double> result = [];
        List<WeightedSlot> unresolved = [.. weighted];
        remaining = Math.Max(0, remaining);
        double remainingWeight = unresolved.Sum(slot => slot.Weight);
        while (unresolved.Count > 0)
        {
            double unit = remainingWeight > 0 ? remaining / remainingWeight : 0;
            int constrainedIndex = -1;
            double constrainedSize = 0;
            for (int index = 0; index < unresolved.Count; index++)
            {
                WeightedSlot slot = unresolved[index];
                double proposed = unit * slot.Weight;
                if (proposed < slot.Minimum)
                {
                    constrainedIndex = index;
                    constrainedSize = slot.Minimum;
                    break;
                }

                if (proposed > slot.Maximum)
                {
                    constrainedIndex = index;
                    constrainedSize = slot.Maximum;
                    break;
                }
            }

            if (constrainedIndex < 0)
            {
                foreach (WeightedSlot slot in unresolved)
                {
                    result[slot.EntityIndex] = unit * slot.Weight;
                }

                break;
            }

            WeightedSlot constrained = unresolved[constrainedIndex];
            result[constrained.EntityIndex] = constrainedSize;
            remaining = Math.Max(0, remaining - constrainedSize);
            remainingWeight -= constrained.Weight;
            unresolved.RemoveAt(constrainedIndex);
        }

        return result;
    }

    private static double ConstraintWithBox(
        double? contentConstraint,
        double padding,
        double margin,
        double fallback) =>
        contentConstraint is { } value
            ? Math.Max(0, value) + padding + margin
            : fallback;

    private static double ConstrainDimension(
        double value,
        double? minimumContent,
        double? maximumContent,
        double padding)
    {
        double minimum = Math.Max(0, minimumContent ?? 0) + padding;
        double maximum = maximumContent is { } declaredMaximum
            ? Math.Max(minimum, Math.Max(0, declaredMaximum) + padding)
            : double.PositiveInfinity;
        return Math.Clamp(Math.Max(0, value), minimum, maximum);
    }

    private readonly record struct WeightedSlot(
        int EntityIndex,
        double Weight,
        double Minimum,
        double Maximum);

    /// <summary>Advances a transition group's scene geometry on the render thread.</summary>
    public void SetTransitionOffset(XsrUiEntityId entity, double offset)
    {
        if (!_tree.IsAlive(entity) || _tree.GetComponent<XsrUiTransition>(entity) is not { } transition) return;
        if (!double.IsFinite(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        double value = ReducedMotion ? 0 : offset;
        if (transition.PresentedOffsetX == value) return;
        transition.PresentedOffsetX = value;
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    public double GetTransitionOffset(XsrUiEntityId entity) =>
        _tree.IsAlive(entity) ? _tree.GetComponent<XsrUiTransition>(entity)?.PresentedOffsetX ?? 0 : 0;

    /// <summary>Advances local capsule geometry on the render thread, driven by a presentation clock.</summary>
    public void SetCapsulePresentationProgress(XsrUiEntityId entity, double progress)
    {
        if (!_tree.IsAlive(entity) || _tree.GetComponent<XsrUiInput>(entity) is not { } input
            || _tree.GetComponent<XsrUiVisualStyle>(entity)?.HoverExpand != true) return;
        double value = Math.Clamp(progress, 0, 1);
        if (input.CapsuleExpansionProgress == value) return;
        input.CapsuleExpansionProgress = value;
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private double? PresentedWidth(XsrUiEntityId entity, XsrUiElement? element)
    {
        if (element?.Width is not { } expanded || element.Height is not { } collapsed
            || _tree.GetComponent<XsrUiVisualStyle>(entity)?.HoverExpand != true
            || _tree.GetComponent<XsrUiInput>(entity) is not { } input) return element?.Width;
        if (ReducedMotion) input.CapsuleExpansionProgress = IsEnabled(input) && (input.IsHovered || input.IsFocusVisible) ? 1 : 0;
        return Math.Min(collapsed, expanded) + Math.Max(0, expanded - collapsed) * input.CapsuleExpansionProgress;
    }

    private void MarkInputDirty(XsrUiEntityId entity) => _tree.MarkDirty(entity,
        _tree.GetComponent<XsrUiVisualStyle>(entity)?.HoverExpand == true
            ? XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint : XsrUiDirtyKinds.Paint);

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
            if (node.IsAccessible && node.Rect.Contains(point) && (node.ClipRect is null || node.ClipRect.Value.Contains(point)))
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
        bool pagerGesture = BeginPagerGesture(point);
        XsrUiEntityId entity = InputAt(point);
        XsrUiInput? input = entity.IsAssigned ? _tree.GetComponent<XsrUiInput>(entity) : null;
        if (entity.IsAssigned && _tree.GetComponent<XsrUiTextInput>(entity) is not null && IsEnabled(input))
            return Focus(entity, showIndicator: false);
        if (entity.IsAssigned && input is { Clickable: true } && IsEnabled(input))
        {
            _ = Focus(entity, showIndicator: false);
            input.IsPressed = true;
            _pressed = entity;
            _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            return true;
        }

        return pagerGesture;
    }

    /// <summary>
    /// Routes a pointer release. Releasing over the pressed entity activates its command binding
    /// and emits one intent with a renderer-produced correlation ID.
    /// </summary>
    public bool PointerReleased(XsrUiPoint point)
    {
        if (EndPagerGesture()) return true;
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
        return InputAt(point).Equals(pressed) && Activate(pressed);
    }

    /// <summary>
    /// Routes a pointer move, updating hover state on input entities. The return value reports
    /// whether presentation state changed, so a backend can commit a frame when the pointer
    /// leaves an input as well as when it enters one.
    /// </summary>
    public bool PointerMoved(XsrUiPoint point)
    {
        if (MovePagerGesture(point)) return true;
        XsrUiEntityId entity = InputAt(point);
        XsrUiInput? input = entity.IsAssigned ? _tree.GetComponent<XsrUiInput>(entity) : null;
        bool overInput = input is not null && IsEnabled(input);
        bool changed = false;

        if (_hovered.IsAssigned && !_tree.IsAlive(_hovered))
        {
            _hovered = default;
        }

        if (_hovered.IsAssigned && !_hovered.Equals(entity))
        {
            XsrUiInput? previous = _tree.GetComponent<XsrUiInput>(_hovered);
            if (previous is { IsHovered: true })
            {
                previous.IsHovered = false;
                MarkInputDirty(_hovered);
                changed = true;
            }
        }

        _hovered = overInput ? entity : default;
        if (overInput && input is not null && !input.IsHovered)
        {
            input.IsHovered = true;
            MarkInputDirty(entity);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Moves keyboard focus to the next focusable entity in scene (tab) order, wrapping around.
    /// Returns false when no focusable entity exists.
    /// </summary>
    public bool FocusNext() => MoveFocus(1);

    public bool FocusPrevious() => MoveFocus(-1);

    private bool MoveFocus(int step)
    {
        if (_scene is null)
        {
            return false;
        }

        List<XsrUiEntityId> focusable = [];
        for (int index = 0; index < _scene.Count; index++)
        {
            if (_scene[index].IsFocusable && _scene[index].IsEnabled)
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
        int next = (currentIndex + step + focusable.Count) % focusable.Count;
        return Focus(focusable[next]);
    }

    /// <summary>
    /// Sets keyboard focus to one focusable entity.
    /// </summary>
    public bool Focus(XsrUiEntityId entity, bool showIndicator = true)
    {
        if (!_tree.IsAlive(entity))
        {
            _focused = default;
            return false;
        }

        XsrUiInput? input = _tree.GetComponent<XsrUiInput>(entity);
        if (input is not { Focusable: true } || !IsEnabled(input) || !IsInVisibleTree(entity))
        {
            return false;
        }

        if (_focused.IsAssigned && !_focused.Equals(entity))
        {
            XsrUiInput? previous = _tree.GetComponent<XsrUiInput>(_focused);
            if (previous is not null)
            {
                previous.IsFocused = false;
                previous.IsFocusVisible = false;
                MarkInputDirty(_focused);
            }
        }

        _focused = entity;
        input.IsFocused = true;
        input.IsFocusVisible = showIndicator;
        MarkInputDirty(entity);
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
            if (_tree.GetComponent<XsrUiPager>(entity) is not null && deltaY != 0)
            {
                _ = MovePager(entity, Math.Sign(deltaY));
                return true;
            }
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
            XsrUiKey.Up => MovePager(FindPager(_focused), -1),
            XsrUiKey.Down => MovePager(FindPager(_focused), 1),
            _ => false,
        };
    }

    /// <summary>
    /// Activates one command-bound entity and emits a renderer-generated correlation ID. Native
    /// input, keyboard input, and accessibility providers all use this one path.
    /// </summary>
    public bool Activate(XsrUiEntityId entity)
    {
        if (!_tree.IsAlive(entity))
        {
            return false;
        }

        if (_tree.GetComponent<XsrUiInput>(entity) is { } input
            && !IsEnabled(input))
        {
            return false;
        }

        if (!IsInVisibleTree(entity))
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

    private bool IsEnabled(XsrUiInput? input) => input is null || (input.Enabled
        && (!input.BoundEnabled.IsAssigned || _state.ReadAppliedValue(input.BoundEnabled) is true));

    private bool IsInVisibleTree(XsrUiEntityId entity)
    {
        while (entity.IsAssigned && _tree.IsAlive(entity))
        {
            if (!IsVisible(entity)) return false;
            if (entity == _root) return true;
            XsrUiEntityId parent = _tree.Parent(entity);
            if (parent.IsAssigned && _tree.GetComponent<XsrUiPager>(parent) is { } pager
                && _tree.Children(parent).Where(IsVisible).ElementAtOrDefault(pager.PageIndex) != entity)
                return false;
            entity = parent;
        }
        return false;
    }

    private XsrUiEntityId InputAt(XsrUiPoint point)
    {
        XsrUiEntityId entity = HitTest(point);
        while (entity.IsAssigned && _tree.IsAlive(entity))
        {
            if (_tree.GetComponent<XsrUiInput>(entity) is not null) return entity;
            entity = _tree.Parent(entity);
        }
        return default;
    }

    private XsrUiSceneNode[] CollectNodes(XsrUiEntityId root, int depth)
    {
        List<XsrUiSceneNode> nodes = [];
        CollectNode(root, depth, nodes);
        return [.. nodes];
    }

    private void CollectNode(XsrUiEntityId entity, int depth, List<XsrUiSceneNode> nodes,
        XsrUiRect? clip = null, bool accessible = true, double offsetX = 0)
    {
        if (!IsVisible(entity))
        {
            return;
        }

        XsrUiSemantic? semantic = _tree.GetComponent<XsrUiSemantic>(entity);
        XsrUiText? text = _tree.GetComponent<XsrUiText>(entity);
        string? content = text is null ? null : ResolveText(text);
        string? semanticLabel = ResolveSemanticLabel(semantic);

        XsrUiRect rect = _paintRects.TryGetValue(entity.Index, out XsrUiRect paintRect)
            ? paintRect
            : default;
        XsrUiAnimation? animation = _tree.GetComponent<XsrUiAnimation>(entity);
        XsrUiImage? image = _tree.GetComponent<XsrUiImage>(entity);
        XsrUiTransition? transition = _tree.GetComponent<XsrUiTransition>(entity);
        string? transitionKey = transition?.BoundKey.IsAssigned == true
            ? _state.ReadAppliedValue(transition.BoundKey) as string ?? string.Empty : transition?.Key;
        if (transition is not null)
        {
            if (transition.HasPresentedKey && transition.PresentedKey != transitionKey
                && Math.Abs(transition.PresentedOffsetX) < .01) transition.PresentedOffsetX = transition.OffsetX;
            transition.HasPresentedKey = true;
            transition.PresentedKey = transitionKey;
            if (ReducedMotion) transition.PresentedOffsetX = 0;
            offsetX += transition.PresentedOffsetX;
        }
        rect = rect with { X = rect.X + offsetX };
        XsrUiVisualStyle? visualStyle = _tree.GetComponent<XsrUiVisualStyle>(entity);
        XsrUiSelection? selection = _tree.GetComponent<XsrUiSelection>(entity);
        XsrUiInput? input = _tree.GetComponent<XsrUiInput>(entity);
        bool enabled = accessible && IsEnabled(input);
        if (!enabled && input is not null)
        {
            input.IsHovered = false;
            input.IsPressed = false;
            input.IsFocused = false;
            input.IsFocusVisible = false;
            if (_pressed == entity) _pressed = default;
            if (_hovered == entity) _hovered = default;
            if (_focused == entity) _focused = default;
        }
        XsrUiRect? visibleClip = clip is { } parentClip ? Intersect(rect, parentClip) : null;
        if (visibleClip is { Width: <= 0 } or { Height: <= 0 }) return;
        nodes.Add(new XsrUiSceneNode(
            entity,
            rect,
            depth,
            semantic?.Role ?? XsrUiSemanticRole.None,
            semanticLabel,
            content,
            image?.Source,
            enabled && (input?.IsFocused ?? false),
            animation?.Progress,
            animation is { Keyframes.Count: > 0 } ? animation.Value : null,
            visualStyle?.Snapshot() ?? default,
            selection?.IsSelected ?? false,
            enabled && (input?.Focusable ?? false),
            enabled && (input?.Clickable ?? false),
            enabled && (input?.IsHovered ?? false),
            enabled && (input?.IsPressed ?? false),
            enabled,
            visibleClip,
            enabled && (input?.IsFocusVisible ?? false),
            input?.CapsuleExpansionProgress ?? 0,
            _tree.GetComponent<XsrUiPager>(entity)?.Snapshot(),
            accessible,
            _tree.GetComponent<XsrUiTextInput>(entity)?.Snapshot(),
            image?.Raster,
            transitionKey, transition?.OffsetX ?? 0, transition?.PresentedOffsetX ?? 0));

        XsrUiPager? pageContainer = _tree.GetComponent<XsrUiPager>(entity);
        XsrUiRect? childClip = pageContainer is not null || _tree.GetComponent<XsrUiScroll>(entity) is not null
            ? visibleClip ?? rect
            : clip;

        int pageIndex = 0;
        foreach (XsrUiEntityId child in _tree.Children(entity).Where(IsVisible))
        {
            CollectNode(child, depth + 1, nodes, childClip,
                accessible && (pageContainer is null || pageContainer.PageIndex == pageIndex), offsetX);
            pageIndex++;
        }
    }

    private static XsrUiRect Intersect(XsrUiRect a, XsrUiRect b)
    {
        double x = Math.Max(a.X, b.X);
        double y = Math.Max(a.Y, b.Y);
        return new XsrUiRect(x, y, Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - x),
            Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - y));
    }

    private string ResolveText(XsrUiText text)
    {
        XsrStateId bound = text.BoundState;
        if (!bound.IsAssigned)
        {
            return text.Content;
        }

        object? value = _state.ReadAppliedValue(bound);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private string? ResolveSemanticLabel(XsrUiSemantic? semantic)
    {
        if (semantic is null || !semantic.BoundLabel.IsAssigned)
        {
            return semantic?.Label;
        }

        object? value = _state.ReadAppliedValue(semantic.BoundLabel);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    // UI.Next uses a deliberately deterministic, backend-neutral text metric. Native backends
    // select their own typeface at commit time, but intrinsic text sizing must be available before
    // any backend exists so PXML layout (including title and command text) has real hit geometry.
    private static XsrUiSize MeasureText(string text, double fontSize)
    {
        double width = 0;
        foreach (char character in text)
        {
            if (character == '\t')
            {
                width += 28;
            }
            else if (!char.IsControl(character))
            {
                width += character <= 0x7f ? 7 : 14;
            }
        }

        // Keep the default 14px metric stable; larger headings need a taller line box.
        double scale = fontSize > 0 ? fontSize / 14 : 1;
        return new XsrUiSize(width * scale, Math.Ceiling(20 * Math.Max(1, scale)));
    }
}
