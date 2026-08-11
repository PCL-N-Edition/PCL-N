// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Deterministic retained backend used by tests, diagnostics and non-visual hosts.
/// It validates mutation ordering and generation safety while applying commits.
/// </summary>
public sealed class HeadlessUiBackend : IUiBackend
{
    private static readonly IReadOnlyList<RenderNodeId> EmptyChildren = Array.Empty<RenderNodeId>();
    private readonly Dictionary<RenderNodeId, UiRenderNodeSnapshot> _nodes = [];
    private readonly Dictionary<int, uint> _liveGenerations = [];
    private readonly Dictionary<RenderNodeId, List<RenderNodeId>> _children = [];
    private readonly List<RenderNodeId> _roots = [];
    private readonly Comparison<RenderNodeId> _renderOrderComparison;
    private bool _initialized;

    public HeadlessUiBackend()
    {
        _renderOrderComparison = CompareRenderOrder;
    }

    public UiBackendCapabilities Capabilities => UiBackendCapabilities.None;

    public UiBackendContext Context { get; private set; }

    public int NodeCount => _nodes.Count;

    public int CommitCount { get; private set; }

    public int RequestFrameCount { get; private set; }

    public long LastCommittedFrameId { get; private set; }

    public int AppliedMutationCount { get; private set; }

    public UiCommitBatch? LastBatch { get; private set; }

    public IReadOnlyList<RenderNodeId> Roots => _roots;

    public void Initialize(in UiBackendContext context)
    {
        if (_initialized)
            throw new InvalidOperationException("Backend is already initialized.");
        Context = context;
        _initialized = true;
    }

    public void Commit(in UiCommitBatch batch)
    {
        EnsureInitialized();
        if (batch.IsEmpty)
            throw new ArgumentException("Empty commit batches must not cross the backend boundary.", nameof(batch));
        if (batch.FrameId <= LastCommittedFrameId)
            throw new InvalidOperationException("Commit frame ids must be strictly increasing.");

        bool orderChanged = false;
        ReadOnlySpan<RenderMutation> mutations = batch.Mutations.Span;
        for (int i = 0; i < mutations.Length; i++)
            orderChanged |= Apply(in mutations[i]);
        if (orderChanged)
            SortRenderOrder();

        LastCommittedFrameId = batch.FrameId;
        LastBatch = batch;
        AppliedMutationCount += mutations.Length;
        CommitCount++;
    }

    public void RequestFrame()
    {
        EnsureInitialized();
        RequestFrameCount++;
    }

    public bool TryGetNode(RenderNodeId node, out UiRenderNodeSnapshot snapshot) =>
        _nodes.TryGetValue(node, out snapshot);

    public IReadOnlyList<RenderNodeId> GetChildren(RenderNodeId parent) =>
        _children.TryGetValue(parent, out List<RenderNodeId>? children)
            ? children
            : EmptyChildren;

    public void CopyNodesTo(List<UiRenderNodeSnapshot> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        foreach (UiRenderNodeSnapshot node in _nodes.Values)
            destination.Add(node);
    }

    private bool Apply(in RenderMutation mutation)
    {
        if (mutation.Node.IsNone)
            throw new InvalidOperationException("A render mutation referenced the None node.");

        if (mutation.Kind == RenderMutationKind.CreateNode)
        {
            if (mutation.Owner.IsNone)
                throw new InvalidOperationException("A render node requires a live owner identity.");
            if (_liveGenerations.TryGetValue(mutation.Node.Index, out uint generation))
            {
                throw new InvalidOperationException(
                    $"Render node slot {mutation.Node.Index} is already occupied by generation {generation}.");
            }
            if (_nodes.ContainsKey(mutation.Node))
                throw new InvalidOperationException("Render node already exists: " + mutation.Node);
            UiRenderNodeSnapshot created = new(
                mutation.Node,
                mutation.Owner,
                mutation.NodeKind,
                RenderNodeId.None,
                0,
                UiRect.Empty,
                System.Numerics.Matrix3x2.Identity,
                1f,
                UiColor.Transparent,
                0f,
                TextLayoutHandle.None);
            _nodes.Add(mutation.Node, created);
            _liveGenerations.Add(mutation.Node.Index, mutation.Node.Generation);
            _children.Add(mutation.Node, []);
            _roots.Add(mutation.Node);
            return true;
        }

        if (!_nodes.TryGetValue(mutation.Node, out UiRenderNodeSnapshot current))
            throw new InvalidOperationException("Render mutation referenced a stale node: " + mutation.Node);

        switch (mutation.Kind)
        {
            case RenderMutationKind.DestroyNode:
                if (_children[mutation.Node].Count != 0)
                    throw new InvalidOperationException("A render parent must not be destroyed before its children are detached.");
                RemoveFromParent(in current);
                _children.Remove(mutation.Node);
                _nodes.Remove(mutation.Node);
                _liveGenerations.Remove(mutation.Node.Index);
                return true;
            case RenderMutationKind.SetParent:
                if (!mutation.RelatedNode.IsNone && !_nodes.ContainsKey(mutation.RelatedNode))
                    throw new InvalidOperationException("Render parent is stale: " + mutation.RelatedNode);
                ValidateParent(mutation.Node, mutation.RelatedNode);
                if (current.Parent == mutation.RelatedNode)
                    return false;
                RemoveFromParent(in current);
                current = current with { Parent = mutation.RelatedNode };
                _nodes[mutation.Node] = current;
                AddToParent(in current);
                return true;
            case RenderMutationKind.SetZOrder:
                _nodes[mutation.Node] = current with { ZOrder = mutation.Integer };
                return true;
            case RenderMutationKind.SetBounds:
                _nodes[mutation.Node] = current with { Bounds = mutation.Bounds };
                return false;
            case RenderMutationKind.SetTransform:
                _nodes[mutation.Node] = current with { Transform = mutation.Transform };
                return false;
            case RenderMutationKind.SetOpacity:
                _nodes[mutation.Node] = current with { Opacity = Math.Clamp(mutation.Scalar, 0f, 1f) };
                return false;
            case RenderMutationKind.SetBrush:
                _nodes[mutation.Node] = current with { Brush = mutation.Color };
                return false;
            case RenderMutationKind.SetCornerRadius:
                _nodes[mutation.Node] = current with { CornerRadius = Math.Max(0f, mutation.Scalar) };
                return false;
            case RenderMutationKind.SetTextLayout:
                if (current.Kind != UiRenderNodeKind.Text)
                    throw new InvalidOperationException("Only text nodes accept text layout handles.");
                _nodes[mutation.Node] = current with { TextLayout = mutation.TextLayout };
                return false;
            case RenderMutationKind.SetNodeKind:
                _nodes[mutation.Node] = current with
                {
                    Kind = mutation.NodeKind,
                    TextLayout = mutation.NodeKind == UiRenderNodeKind.Text
                        ? current.TextLayout
                        : TextLayoutHandle.None
                };
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private void RemoveFromParent(in UiRenderNodeSnapshot node)
    {
        List<RenderNodeId> collection = node.Parent.IsNone
            ? _roots
            : _children[node.Parent];
        collection.Remove(node.Id);
    }

    private void AddToParent(in UiRenderNodeSnapshot node)
    {
        List<RenderNodeId> collection = node.Parent.IsNone
            ? _roots
            : _children[node.Parent];
        collection.Add(node.Id);
    }

    private void SortRenderOrder()
    {
        _roots.Sort(_renderOrderComparison);
        foreach (List<RenderNodeId> children in _children.Values)
            children.Sort(_renderOrderComparison);
    }

    private void ValidateParent(RenderNodeId node, RenderNodeId parent)
    {
        RenderNodeId current = parent;
        int guard = 0;
        while (!current.IsNone && guard++ <= _nodes.Count)
        {
            if (current == node)
                throw new InvalidOperationException("Render parent assignment would create a cycle.");
            current = _nodes[current].Parent;
        }
        if (guard > _nodes.Count)
            throw new InvalidOperationException("Retained backend contains a parent cycle.");
    }

    private int CompareRenderOrder(RenderNodeId left, RenderNodeId right)
    {
        long leftOrder = _nodes[left].ZOrder;
        long rightOrder = _nodes[right].ZOrder;
        int order = leftOrder.CompareTo(rightOrder);
        return order != 0 ? order : left.Index.CompareTo(right.Index);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Backend must be initialized before use.");
    }
}
