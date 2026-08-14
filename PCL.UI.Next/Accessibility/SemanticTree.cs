// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Generation-safe identity in the semantic tree; distinct from render-node identity.</summary>
public readonly record struct UiSemanticNodeId(int Index, uint Generation)
{
    public static UiSemanticNodeId None => default;
    public bool IsNone => Index == 0;
    public override string ToString() => IsNone ? "SemanticNode(None)" : $"SemanticNode({Index}:{Generation})";

    internal static UiSemanticNodeId FromEntity(UiEntity entity) =>
        entity.IsNone ? None : new UiSemanticNodeId(entity.Index, entity.Generation);
}

public readonly record struct UiSemanticNode(
    UiSemanticNodeId Id,
    UiEntity Owner,
    UiSemanticNodeId Parent,
    int ChildOrder,
    UiSemanticRole Role,
    string Name,
    string Description,
    string Value,
    UiAccessibleState State,
    UiAccessibleAction Actions,
    UiRect Bounds);

/// <summary>Immutable, independently-topologized semantic tree submitted to a platform backend.</summary>
public sealed class UiSemanticTreeSnapshot
{
    private readonly UiSemanticNode[] _nodes;

    internal UiSemanticTreeSnapshot(long frameId, uint version, UiSemanticNode[] nodes)
    {
        FrameId = frameId;
        Version = version;
        _nodes = nodes;
    }

    public static UiSemanticTreeSnapshot Empty { get; } = new(0, 0, []);

    public long FrameId { get; }
    public uint Version { get; }
    public int NodeCount => _nodes.Length;
    public ReadOnlyMemory<UiSemanticNode> Nodes => _nodes;

    public bool TryGetNode(UiSemanticNodeId id, out UiSemanticNode node)
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (_nodes[i].Id != id)
                continue;
            node = _nodes[i];
            return true;
        }
        node = default;
        return false;
    }
}

/// <summary>Optional backend facet for exposing a platform accessibility tree.</summary>
public interface IAccessibilityBackend
{
    event Action<UiAccessibilityActionRequest>? AccessibilityActionRaised;

    void CommitAccessibility(UiSemanticTreeSnapshot tree);
}

public readonly record struct UiAccessibilityActionRequest(
    UiEntity Owner,
    UiAccessibleAction Action,
    UiTimestamp Timestamp,
    string? Value = null);

public readonly record struct UiAccessibilityFrameAction(
    UiEntity Owner,
    UiAccessibleAction Action,
    UiTimestamp Timestamp,
    string? Value);
