// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>Maps the runtime semantic tree to virtual Avalonia automation peers.</summary>
internal sealed class AvaloniaAccessibilityBridge
{
    private readonly PclUiSurface _surface;
    private readonly HashSet<UiEntity> _nativeOwners = [];
    private PclUiSurfaceAutomationPeer? _peer;

    public AvaloniaAccessibilityBridge(PclUiSurface surface)
    {
        _surface = surface;
    }

    public UiSemanticTreeSnapshot Tree { get; private set; } = UiSemanticTreeSnapshot.Empty;

    public AutomationPeer CreatePeer()
    {
        _peer ??= new PclUiSurfaceAutomationPeer(_surface);
        _peer.Update(Tree, _nativeOwners);
        return _peer;
    }

    public void Update(UiSemanticTreeSnapshot tree, IReadOnlySet<UiEntity> nativeOwners)
    {
        Tree = tree ?? throw new ArgumentNullException(nameof(tree));
        ArgumentNullException.ThrowIfNull(nativeOwners);
        _nativeOwners.Clear();
        _nativeOwners.UnionWith(nativeOwners);
        _peer?.Update(tree, _nativeOwners);
    }
}

internal sealed class PclUiSurfaceAutomationPeer : ControlAutomationPeer
{
    private readonly PclUiSurface _surface;
    private readonly Dictionary<UiSemanticNodeId, SemanticAutomationPeer> _peers = [];
    private IReadOnlyList<AutomationPeer> _roots = Array.Empty<AutomationPeer>();

    public PclUiSurfaceAutomationPeer(PclUiSurface owner) : base(owner)
    {
        _surface = owner;
    }

    public void Update(UiSemanticTreeSnapshot tree, IReadOnlySet<UiEntity> nativeOwners)
    {
        HashSet<UiSemanticNodeId> retained = [];
        Dictionary<UiSemanticNodeId, UiSemanticNode> nodesById = [];
        ReadOnlySpan<UiSemanticNode> nodes = tree.Nodes.Span;
        for (int i = 0; i < nodes.Length; i++)
        {
            UiSemanticNode node = nodes[i];
            nodesById[node.Id] = node;
            if (nativeOwners.Contains(node.Owner))
                continue;
            retained.Add(node.Id);
            if (!_peers.TryGetValue(node.Id, out SemanticAutomationPeer? peer))
            {
                peer = new SemanticAutomationPeer(_surface, node);
                _peers.Add(node.Id, peer);
            }
            else
            {
                peer.Update(node);
            }
            peer.ResetRelations();
        }

        foreach (UiSemanticNodeId stale in _peers.Keys.Where(id => !retained.Contains(id)).ToArray())
            _peers.Remove(stale);

        List<AutomationPeer> roots = [];
        for (int i = 0; i < nodes.Length; i++)
        {
            UiSemanticNode node = nodes[i];
            if (!_peers.TryGetValue(node.Id, out SemanticAutomationPeer? peer))
                continue;
            UiSemanticNodeId parentId = node.Parent;
            while (!parentId.IsNone && !_peers.ContainsKey(parentId))
            {
                parentId = nodesById.TryGetValue(parentId, out UiSemanticNode skipped)
                    ? skipped.Parent
                    : UiSemanticNodeId.None;
            }
            if (parentId.IsNone || !_peers.TryGetValue(parentId, out SemanticAutomationPeer? parent))
            {
                peer.SetSemanticParent(this);
                roots.Add(peer);
            }
            else
            {
                peer.SetSemanticParent(parent);
                parent.AddChild(peer);
            }
        }
        _roots = roots;
        InvalidateChildren();
        RaiseChildrenChangedEvent();
    }

    protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => _roots;

    protected override string GetNameCore() => "PCL UI";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
}

internal sealed class SemanticAutomationPeer : AutomationPeer, IInvokeProvider
{
    private readonly PclUiSurface _surface;
    private readonly List<AutomationPeer> _children = [];
    private UiSemanticNode _node;
    private AutomationPeer? _parent;

    public SemanticAutomationPeer(PclUiSurface surface, UiSemanticNode node)
    {
        _surface = surface;
        _node = node;
    }

    public void Update(UiSemanticNode node) => _node = node;

    public void ResetRelations()
    {
        _parent = null;
        _children.Clear();
    }

    public void SetSemanticParent(AutomationPeer parent) => _parent = parent;

    public void AddChild(AutomationPeer child) => _children.Add(child);

    public void Invoke()
    {
        if ((_node.Actions & UiAccessibleAction.Invoke) == 0 || !IsEnabledCore())
            return;
        _surface.RaiseAccessibilityAction(new UiAccessibilityActionRequest(
            _node.Owner,
            UiAccessibleAction.Invoke,
            UiTimestamp.Zero));
    }

    protected override void BringIntoViewCore() { }
    protected override string GetAcceleratorKeyCore() => string.Empty;
    protected override string GetAccessKeyCore() => string.Empty;
    protected override AutomationControlType GetAutomationControlTypeCore() => MapRole(_node.Role);
    protected override string GetAutomationIdCore() => $"pcl-semantic-{_node.Id.Index}-{_node.Id.Generation}";
    protected override Rect GetBoundingRectangleCore() => new(
        _node.Bounds.X,
        _node.Bounds.Y,
        _node.Bounds.Width,
        _node.Bounds.Height);
    protected override string GetClassNameCore() => "PclSemantic" + _node.Role;
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override string GetNameCore() => _node.Name;
    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => _children;
    protected override AutomationPeer? GetParentCore() => _parent;
    protected override bool HasKeyboardFocusCore() => (_node.State & UiAccessibleState.Focused) != 0;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => (_node.State & UiAccessibleState.Disabled) == 0;
    protected override bool IsKeyboardFocusableCore() => (_node.Actions & UiAccessibleAction.Focus) != 0;
    protected override bool IsOffscreenCore() => (_node.State & UiAccessibleState.Hidden) != 0;
    protected override void SetFocusCore()
    {
        if ((_node.Actions & UiAccessibleAction.Focus) != 0 && IsEnabledCore())
        {
            _surface.RaiseAccessibilityAction(new UiAccessibilityActionRequest(
                _node.Owner,
                UiAccessibleAction.Focus,
                UiTimestamp.Zero));
        }
    }
    protected override bool ShowContextMenuCore() => false;
    protected override object? GetProviderCore(Type providerType) =>
        providerType == typeof(IInvokeProvider) && (_node.Actions & UiAccessibleAction.Invoke) != 0
            ? this
            : base.GetProviderCore(providerType);
    protected override bool TrySetParent(AutomationPeer? parent)
    {
        _parent = parent;
        return true;
    }

    private static AutomationControlType MapRole(UiSemanticRole role) => role switch
    {
        UiSemanticRole.Group => AutomationControlType.Group,
        UiSemanticRole.StaticText => AutomationControlType.Text,
        UiSemanticRole.Button => AutomationControlType.Button,
        UiSemanticRole.TextBox or UiSemanticRole.PasswordBox => AutomationControlType.Edit,
        UiSemanticRole.Image => AutomationControlType.Image,
        UiSemanticRole.Link => AutomationControlType.Hyperlink,
        UiSemanticRole.CheckBox => AutomationControlType.CheckBox,
        UiSemanticRole.RadioButton => AutomationControlType.RadioButton,
        UiSemanticRole.Slider => AutomationControlType.Slider,
        UiSemanticRole.ProgressBar => AutomationControlType.ProgressBar,
        UiSemanticRole.List => AutomationControlType.List,
        UiSemanticRole.ListItem => AutomationControlType.ListItem,
        UiSemanticRole.Dialog => AutomationControlType.Window,
        UiSemanticRole.Tooltip => AutomationControlType.ToolTip,
        UiSemanticRole.Heading => AutomationControlType.Header,
        _ => AutomationControlType.Custom
    };
}
