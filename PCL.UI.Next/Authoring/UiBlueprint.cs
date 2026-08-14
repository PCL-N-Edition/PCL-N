// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Immutable compiled UI graph (architecture §20). Built once; instantiated many times.
/// Public accessors return copies / spans — the backing arrays are not exposed for mutation.
/// </summary>
public sealed class UiBlueprint
{
    internal UiBlueprint(
        string name,
        BlueprintNode[] nodes,
        BlueprintBinding[] bindings,
        int rootIndex,
        BlueprintDependencyIndex dependencyIndex)
    {
        Name = name;
        NodesCore = nodes;
        BindingsCore = bindings;
        RootIndex = rootIndex;
        DependencyIndex = dependencyIndex;
    }

    public string Name { get; }

    internal int RootIndex { get; }

    internal int NodeCount => NodesCore.Length;

    internal int BindingCount => BindingsCore.Length;

    /// <summary>Node by index (struct copy — cannot mutate the compiled graph).</summary>
    internal BlueprintNode GetNode(int index) => NodesCore[index];

    /// <summary>Binding by index (struct copy).</summary>
    internal BlueprintBinding GetBinding(int index) => BindingsCore[index];

    internal BlueprintNode[] NodesCore { get; }

    internal BlueprintBinding[] BindingsCore { get; }

    internal BlueprintDependencyIndex DependencyIndex { get; }
}

/// <summary>One static node in a blueprint graph (sibling/child via indices).</summary>
internal readonly struct BlueprintNode
{
    private readonly int[] _styleClassIds;

    internal BlueprintNode(
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
        int conditionBindingIndex,
        LayoutStyle layout,
        float layoutGap,
        ReadOnlySpan<UiGridTrack> gridColumns,
        ReadOnlySpan<UiGridTrack> gridRows,
        GridPlacement gridPlacement,
        bool hasGridPlacement,
        AbsolutePlacement absolutePlacement,
        bool hasAbsolutePlacement,
        TextFormat textFormat,
        bool isHitTestVisible,
        int tabIndex,
        bool isFocusScope,
        bool isFocusTrap,
        bool restorePreviousFocus,
        UiGestureMask gestures,
        UiTransitionSet transitions,
        UiMotionToken layoutTransition,
        ScrollViewport scrollViewport,
        Virtualization virtualization,
        NativeHostComponent nativeHost,
        SemanticDefinition semantic)
    {
        Kind = kind;
        ParentIndex = parentIndex;
        FirstChildIndex = firstChildIndex;
        NextSiblingIndex = nextSiblingIndex;
        _styleClassIds = styleClassIds.Length == 0
            ? Array.Empty<int>()
            : (int[])styleClassIds.Clone();
        Behaviors = behaviors;
        CommandId = commandId;
        StaticText = staticText;
        TrueBranchRoot = trueBranchRoot;
        FalseBranchRoot = falseBranchRoot;
        ConditionBindingIndex = conditionBindingIndex;
        Layout = layout;
        LayoutGap = layoutGap;
        GridColumnsCore = gridColumns.ToArray();
        GridRowsCore = gridRows.ToArray();
        GridPlacement = gridPlacement;
        HasGridPlacement = hasGridPlacement;
        AbsolutePlacement = absolutePlacement;
        HasAbsolutePlacement = hasAbsolutePlacement;
        TextFormat = textFormat;
        IsHitTestVisible = isHitTestVisible;
        TabIndex = tabIndex;
        IsFocusScope = isFocusScope;
        IsFocusTrap = isFocusTrap;
        RestorePreviousFocus = restorePreviousFocus;
        Gestures = gestures;
        Transitions = transitions;
        LayoutTransition = layoutTransition;
        ScrollViewport = scrollViewport;
        Virtualization = virtualization;
        NativeHost = nativeHost;
        Semantic = semantic;
    }

    public UiNodeKind Kind { get; }
    public int ParentIndex { get; }
    public int FirstChildIndex { get; }
    public int NextSiblingIndex { get; }
    public ReadOnlySpan<int> StyleClassIds => _styleClassIds;
    public UiBehavior Behaviors { get; }
    public int CommandId { get; }
    public string? StaticText { get; }
    public int TrueBranchRoot { get; }
    public int FalseBranchRoot { get; }
    public int ConditionBindingIndex { get; }
    public LayoutStyle Layout { get; }
    public float LayoutGap { get; }
    public ReadOnlySpan<UiGridTrack> GridColumns => GridColumnsCore;
    public ReadOnlySpan<UiGridTrack> GridRows => GridRowsCore;
    public GridPlacement GridPlacement { get; }
    public bool HasGridPlacement { get; }
    public AbsolutePlacement AbsolutePlacement { get; }
    public bool HasAbsolutePlacement { get; }
    public TextFormat TextFormat { get; }
    public bool IsHitTestVisible { get; }
    public int TabIndex { get; }
    public bool IsFocusScope { get; }
    public bool IsFocusTrap { get; }
    public bool RestorePreviousFocus { get; }
    public UiGestureMask Gestures { get; }
    public UiTransitionSet Transitions { get; }
    public UiMotionToken LayoutTransition { get; }
    public ScrollViewport ScrollViewport { get; }
    public Virtualization Virtualization { get; }
    public NativeHostComponent NativeHost { get; }
    public SemanticDefinition Semantic { get; }
    public bool IsStructural => Kind == UiNodeKind.If;

    private UiGridTrack[] GridColumnsCore { get; }
    private UiGridTrack[] GridRowsCore { get; }
}

/// <summary>Compiled binding slot (selector → node property).</summary>
internal readonly struct BlueprintBinding
{
    private readonly int[] _dependencySlices;

    internal BlueprintBinding(
        int bindingId,
        int nodeIndex,
        ReadOnlySpan<int> dependencySlices,
        BlueprintBindingKind kind,
        Func<PresentationStore, string>? readString = null,
        Func<PresentationStore, bool>? readBool = null)
    {
        if (dependencySlices.IsEmpty)
            throw new ArgumentException("Binding requires at least one dependency slice.", nameof(dependencySlices));

        BindingId = bindingId;
        NodeIndex = nodeIndex;
        _dependencySlices = dependencySlices.ToArray();
        Kind = kind;
        ReadString = readString;
        ReadBool = readBool;
    }

    public int BindingId { get; }
    public int NodeIndex { get; }
    public int DependencySlice => _dependencySlices[0];
    public ReadOnlySpan<int> DependencySlices => _dependencySlices;
    public BlueprintBindingKind Kind { get; }
    public Func<PresentationStore, string>? ReadString { get; }
    public Func<PresentationStore, bool>? ReadBool { get; }
}

internal enum BlueprintBindingKind : byte
{
    None = 0,
    Text = 1,
    Condition = 2,
    NativeValue = 3
}

/// <summary>
/// Applied binding stamp: state fingerprint + target entity generation.
/// Remounts (new generation) re-apply even when state version is unchanged.
/// </summary>
internal struct BindingStamp
{
    public ulong StateVersion { get; set; }
    public UiEntity Entity { get; set; }

    public static BindingStamp None => default;

    public bool Matches(ulong stateVersion, UiEntity entity) =>
        StateVersion == stateVersion && Entity == entity;
}
