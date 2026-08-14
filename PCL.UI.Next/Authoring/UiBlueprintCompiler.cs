// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Source-generator-<b>compatible</b> runtime compiler prototype (not a Roslyn generator).
/// Walks an authoring tree once and emits a packed blueprint + binding program
/// (no reflection / expression-tree parse). A future
/// <c>PCL.UI.Next.SourceGenerators</c> project can emit the same arrays at compile time.
/// </summary>
public static class UiBlueprintCompiler
{
    public static UiBlueprint Compile(UiNode root, string name = "Blueprint")
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(name))
            name = "Blueprint";

        List<NodeDraft> drafts = [];
        List<BlueprintBinding> bindings = [];
        int rootIndex = Emit(root, parentIndex: -1, drafts, bindings);

        BlueprintNode[] nodes = new BlueprintNode[drafts.Count];
        for (int i = 0; i < drafts.Count; i++)
        {
            NodeDraft d = drafts[i];
            nodes[i] = new BlueprintNode(
                d.Kind,
                d.ParentIndex,
                d.FirstChildIndex,
                d.NextSiblingIndex,
                d.StyleClassIds,
                d.Behaviors,
                d.CommandId,
                d.StaticText,
                d.TrueBranchRoot,
                d.FalseBranchRoot,
                d.ConditionBindingIndex,
                d.Layout,
                d.LayoutGap,
                d.GridColumns,
                d.GridRows,
                d.GridPlacement,
                d.HasGridPlacement,
                d.AbsolutePlacement,
                d.HasAbsolutePlacement,
                d.TextFormat,
                d.IsHitTestVisible,
                d.TabIndex,
                d.IsFocusScope,
                d.IsFocusTrap,
                d.RestorePreviousFocus,
                d.Gestures,
                d.Transitions,
                d.LayoutTransition,
                d.ScrollViewport,
                d.Virtualization);
        }

        BlueprintBinding[] bindingArray = bindings.ToArray();
        BlueprintDependencyIndex dependencyIndex = BlueprintDependencyIndex.Build(bindingArray);
        return new UiBlueprint(name, nodes, bindingArray, rootIndex, dependencyIndex);
    }

    private static int Emit(
        UiNode node,
        int parentIndex,
        List<NodeDraft> drafts,
        List<BlueprintBinding> bindings)
    {
        if (node.StyleClassIds.Count > StyleClassSet.MaxInlineCount)
        {
            throw new InvalidOperationException(
                $"Node kind {node.Kind} declares {node.StyleClassIds.Count} style classes; " +
                $"the inline maximum is {StyleClassSet.MaxInlineCount}.");
        }

        int index = drafts.Count;
        drafts.Add(new NodeDraft
        {
            Kind = node.Kind,
            ParentIndex = parentIndex,
            FirstChildIndex = -1,
            NextSiblingIndex = -1,
            StyleClassIds = node.StyleClassIds.ToArray(),
            Behaviors = node.Behaviors,
            CommandId = node.CommandId,
            StaticText = node.StaticText,
            TrueBranchRoot = -1,
            FalseBranchRoot = -1,
            ConditionBindingIndex = -1,
            Layout = node.Layout,
            LayoutGap = node.LayoutGap,
            GridColumns = node.GridDefinition?.Columns.ToArray() ?? Array.Empty<UiGridTrack>(),
            GridRows = node.GridDefinition?.Rows.ToArray() ?? Array.Empty<UiGridTrack>(),
            GridPlacement = node.GridPlacement,
            HasGridPlacement = node.HasGridPlacement,
            AbsolutePlacement = node.AbsolutePlacement,
            HasAbsolutePlacement = node.HasAbsolutePlacement,
            TextFormat = node.TextFormat,
            IsHitTestVisible = node.HitTestVisibleOverride ??
                               (node.Behaviors != UiBehavior.None ||
                                node.GestureMask != UiGestureMask.None),
            TabIndex = node.TabIndexValue,
            IsFocusScope = node.IsFocusScope,
            IsFocusTrap = node.IsFocusTrap,
            RestorePreviousFocus = node.RestorePreviousFocus,
            Gestures = node.GestureMask,
            Transitions = node.Transitions,
            LayoutTransition = node.LayoutTransition,
            ScrollViewport = node.ScrollViewport,
            Virtualization = node.Virtualization
        });

        if (node.TextBinding is { } textSelector)
        {
            bindings.Add(new BlueprintBinding(
                textSelector.Id,
                index,
                textSelector.DependencySlices,
                BlueprintBindingKind.Text,
                readString: textSelector.Read));
        }

        if (node.Kind == UiNodeKind.If)
        {
            if (node.Condition is not { } condition)
                throw new InvalidOperationException("Ui.If requires a condition selector.");
            if (node.WhenTrue is null)
                throw new InvalidOperationException("Ui.If requires a whenTrue branch.");

            int conditionBindingIndex = bindings.Count;
            bindings.Add(new BlueprintBinding(
                condition.Id,
                index,
                condition.DependencySlices,
                BlueprintBindingKind.Condition,
                readBool: condition.Read));

            int trueRoot = Emit(node.WhenTrue, parentIndex: index, drafts, bindings);
            int falseRoot = node.WhenFalse is null
                ? -1
                : Emit(node.WhenFalse, parentIndex: index, drafts, bindings);

            NodeDraft draft = drafts[index];
            draft.ConditionBindingIndex = conditionBindingIndex;
            draft.TrueBranchRoot = trueRoot;
            draft.FalseBranchRoot = falseRoot;
            draft.FirstChildIndex = -1; // structural children are not always-mounted
            drafts[index] = draft;
            return index;
        }

        int previousSibling = -1;
        int firstChild = -1;
        foreach (UiNode child in node.ChildNodes)
        {
            int childIndex = Emit(child, parentIndex: index, drafts, bindings);
            if (firstChild < 0)
                firstChild = childIndex;
            if (previousSibling >= 0)
            {
                NodeDraft prev = drafts[previousSibling];
                prev.NextSiblingIndex = childIndex;
                drafts[previousSibling] = prev;
            }

            previousSibling = childIndex;
        }

        NodeDraft self = drafts[index];
        self.FirstChildIndex = firstChild;
        drafts[index] = self;
        return index;
    }

    private struct NodeDraft
    {
        public UiNodeKind Kind;
        public int ParentIndex;
        public int FirstChildIndex;
        public int NextSiblingIndex;
        public int[] StyleClassIds;
        public UiBehavior Behaviors;
        public int CommandId;
        public string? StaticText;
        public int TrueBranchRoot;
        public int FalseBranchRoot;
        public int ConditionBindingIndex;
        public LayoutStyle Layout;
        public float LayoutGap;
        public UiGridTrack[] GridColumns;
        public UiGridTrack[] GridRows;
        public GridPlacement GridPlacement;
        public bool HasGridPlacement;
        public AbsolutePlacement AbsolutePlacement;
        public bool HasAbsolutePlacement;
        public TextFormat TextFormat;
        public bool IsHitTestVisible;
        public int TabIndex;
        public bool IsFocusScope;
        public bool IsFocusTrap;
        public bool RestorePreviousFocus;
        public UiGestureMask Gestures;
        public UiTransitionSet Transitions;
        public UiMotionToken LayoutTransition;
        public ScrollViewport ScrollViewport;
        public Virtualization Virtualization;
    }
}
