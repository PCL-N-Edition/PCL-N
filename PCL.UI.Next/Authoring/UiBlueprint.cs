// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Immutable compiled UI graph (architecture §20). Built once; instantiated many times.
/// </summary>
public sealed class UiBlueprint
{
    internal UiBlueprint(
        string name,
        BlueprintNode[] nodes,
        BlueprintBinding[] bindings,
        int rootIndex)
    {
        Name = name;
        NodesCore = nodes;
        BindingsCore = bindings;
        RootIndex = rootIndex;
    }

    public string Name { get; }

    public IReadOnlyList<BlueprintNode> Nodes => NodesCore;

    public IReadOnlyList<BlueprintBinding> Bindings => BindingsCore;

    public int RootIndex { get; }

    public int NodeCount => NodesCore.Length;

    internal BlueprintNode[] NodesCore { get; }

    internal BlueprintBinding[] BindingsCore { get; }
}

/// <summary>One static node in a blueprint graph (sibling/child via indices).</summary>
public readonly struct BlueprintNode
{
    public BlueprintNode(
        UiNodeKind kind,
        int parentIndex,
        int firstChildIndex,
        int nextSiblingIndex,
        int[] styleClassIds,
        UiBehavior behaviors,
        int commandId,
        string? staticText,
        int trueBranchRoot,
        int falseBranchRoot,
        int conditionBindingIndex)
    {
        Kind = kind;
        ParentIndex = parentIndex;
        FirstChildIndex = firstChildIndex;
        NextSiblingIndex = nextSiblingIndex;
        StyleClassIds = styleClassIds;
        Behaviors = behaviors;
        CommandId = commandId;
        StaticText = staticText;
        TrueBranchRoot = trueBranchRoot;
        FalseBranchRoot = falseBranchRoot;
        ConditionBindingIndex = conditionBindingIndex;
    }

    public UiNodeKind Kind { get; }
    public int ParentIndex { get; }
    public int FirstChildIndex { get; }
    public int NextSiblingIndex { get; }
    public int[] StyleClassIds { get; }
    public UiBehavior Behaviors { get; }
    public int CommandId { get; }
    public string? StaticText { get; }

    /// <summary>For <see cref="UiNodeKind.If"/>: root index of true template (-1 none).</summary>
    public int TrueBranchRoot { get; }

    /// <summary>For <see cref="UiNodeKind.If"/>: root index of false template (-1 none).</summary>
    public int FalseBranchRoot { get; }

    /// <summary>Index into <see cref="UiBlueprint.Bindings"/> for the condition (-1 none).</summary>
    public int ConditionBindingIndex { get; }

    public bool IsStructural => Kind == UiNodeKind.If;
}

/// <summary>Compiled binding slot (selector → node property).</summary>
public readonly struct BlueprintBinding
{
    public BlueprintBinding(
        int bindingId,
        int nodeIndex,
        int dependencySlice,
        BlueprintBindingKind kind,
        Func<PresentationStore, string>? readString = null,
        Func<PresentationStore, bool>? readBool = null)
    {
        BindingId = bindingId;
        NodeIndex = nodeIndex;
        DependencySlice = dependencySlice;
        Kind = kind;
        ReadString = readString;
        ReadBool = readBool;
    }

    public int BindingId { get; }
    public int NodeIndex { get; }
    public int DependencySlice { get; }
    public BlueprintBindingKind Kind { get; }
    public Func<PresentationStore, string>? ReadString { get; }
    public Func<PresentationStore, bool>? ReadBool { get; }
}

public enum BlueprintBindingKind : byte
{
    None = 0,
    Text = 1,
    Condition = 2
}
