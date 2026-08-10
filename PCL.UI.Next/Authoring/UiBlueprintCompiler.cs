// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Phase-2 "source generator prototype": walks an authoring tree once and emits a
/// packed blueprint + compiled binding program (no reflection, no expression trees).
/// A future Roslyn generator can emit the same shape without the runtime walk.
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

        // Link next-sibling for children of each parent.
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
                d.ConditionBindingIndex);
        }

        return new UiBlueprint(name, nodes, bindings.ToArray(), rootIndex);
    }

    private static int Emit(
        UiNode node,
        int parentIndex,
        List<NodeDraft> drafts,
        List<BlueprintBinding> bindings)
    {
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
            ConditionBindingIndex = -1
        });

        if (node.TextBinding is { } textSelector)
        {
            int bindingIndex = bindings.Count;
            bindings.Add(new BlueprintBinding(
                textSelector.Id,
                index,
                textSelector.DependencySlice,
                BlueprintBindingKind.Text,
                readString: textSelector.Read));
            // Binding targets this node; StaticText is fallback until first apply.
            _ = bindingIndex;
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
                condition.DependencySlice,
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
    }
}
