using System.Globalization;
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
    private readonly Dictionary<int, XsrUiSize> _desiredSizes = [];
    private readonly Dictionary<int, XsrUiRect> _paintRects = [];
    private readonly HashSet<int> _measuredThisPass = [];
    private XsrUiScene? _scene;
    private long _sceneVersion;
    private XsrUiEntityId _root;
    private XsrUiSize _viewport = new(800, 600);
    private int _layoutVisits;

    public XsrUiRenderer(XsrUiTree tree, XsrStateStore state)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

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
        foreach (int id in _desiredSizes.Keys.Where(id => !_tree.IsAlive(new XsrUiEntityId(id))).ToArray())
        {
            _ = _desiredSizes.Remove(id);
        }

        foreach (int id in _paintRects.Keys.Where(id => !_tree.IsAlive(new XsrUiEntityId(id))).ToArray())
        {
            _ = _paintRects.Remove(id);
        }
    }

    private void Layout(XsrUiEntityId entity, XsrUiRect slot)
    {
        if (!_tree.HasDirtySubtree(entity))
        {
            return;
        }

        XsrUiSize desired = Measure(entity, new XsrUiSize(slot.Width, slot.Height));
        Arrange(entity, slot, desired);
    }

    private XsrUiSize Measure(XsrUiEntityId entity, XsrUiSize available)
    {
        // Fully clean subtrees keep the previous pass's desired size; anything with a dirty
        // descendant re-aggregates so containers pick up new child measurements.
        if (!_tree.HasDirtySubtree(entity) && _desiredSizes.TryGetValue(entity.Value, out XsrUiSize cached))
        {
            return cached;
        }

        if (_measuredThisPass.Add(entity.Value))
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
        _desiredSizes[entity.Value] = desired;
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
        _paintRects[entity.Value] = new XsrUiRect(contentX, contentY, contentWidth, contentHeight);

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

        double cursor = stack.Direction == XsrUiOrientation.Vertical ? contentY : contentX;
        double crossAvailable = stack.Direction == XsrUiOrientation.Vertical ? contentWidth : contentHeight;
        foreach (XsrUiEntityId child in _tree.Children(entity))
        {
            if (!IsVisible(child))
            {
                continue;
            }

            XsrUiSize childDesired = _desiredSizes.TryGetValue(child.Value, out XsrUiSize size)
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
                ? new XsrUiRect(contentX + crossOffset, cursor, crossSize, childMain)
                : new XsrUiRect(cursor, contentY + crossOffset, childMain, crossSize);

            Layout(child, childSlot);
            cursor += childMain + stack.Spacing;
        }
    }

    private bool IsVisible(XsrUiEntityId entity) =>
        _tree.GetComponent<XsrUiElement>(entity)?.IsVisible ?? true;

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
            object? value = _state.ReadValue(bound);
            content = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        XsrUiRect rect = _paintRects.TryGetValue(entity.Value, out XsrUiRect paintRect)
            ? paintRect
            : default;
        nodes.Add(new XsrUiSceneNode(
            entity,
            rect,
            depth,
            semantic?.Role ?? XsrUiSemanticRole.None,
            semantic?.Label,
            content));

        foreach (XsrUiEntityId child in _tree.Children(entity))
        {
            CollectNode(child, depth + 1, nodes);
        }
    }
}
