// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Mutable authoring node. Pages build trees of these; compilers emit
/// <see cref="UiBlueprint"/>. Never create runtime entities from a page.
/// </summary>
public sealed class UiNode
{
    internal UiNode(UiNodeKind kind)
    {
        Kind = kind;
    }

    public UiNodeKind Kind { get; }

    internal List<UiNode> ChildNodes { get; } = [];

    internal List<int> StyleClassIds { get; } = [];

    internal UiBehavior Behaviors { get; set; }

    internal int CommandId { get; set; }

    internal string? StaticText { get; set; }

    internal UiSelector<string>? TextBinding { get; set; }

    internal UiSelector<bool>? Condition { get; set; }

    internal UiNode? WhenTrue { get; set; }

    internal UiNode? WhenFalse { get; set; }

    internal LayoutStyle Layout { get; set; } = LayoutStyle.Default;

    internal float LayoutGap { get; set; }

    internal UiGridDefinition? GridDefinition { get; set; }

    internal GridPlacement GridPlacement { get; set; } = GridPlacement.Default;

    internal bool HasGridPlacement { get; set; }

    internal AbsolutePlacement AbsolutePlacement { get; set; }

    internal bool HasAbsolutePlacement { get; set; }

    internal TextFormat TextFormat { get; set; } = TextFormat.Default;

    internal bool? HitTestVisibleOverride { get; set; }

    internal int TabIndexValue { get; set; }

    internal bool IsFocusScope { get; set; }

    internal bool IsFocusTrap { get; set; }

    internal bool RestorePreviousFocus { get; set; } = true;

    internal UiGestureMask GestureMask { get; set; }

    internal UiTransitionSet Transitions { get; set; }

    internal UiMotionToken LayoutTransition { get; set; }

    public UiNode Class(UiClass styleClass)
    {
        if (StyleClassIds.Contains(styleClass.Id))
            return this;
        // Runtime StyleClassSet is inline-4; overflow requires a future StyleClassStore.
        if (StyleClassIds.Count >= StyleClassSet.MaxInlineCount)
        {
            throw new InvalidOperationException(
                $"A node may declare at most {StyleClassSet.MaxInlineCount} inline style classes. " +
                "Class '" + (styleClass.Name ?? styleClass.Id.ToString()) + "' was rejected.");
        }

        StyleClassIds.Add(styleClass.Id);
        return this;
    }

    public UiNode Behavior(UiBehavior behavior)
    {
        Behaviors |= behavior;
        return this;
    }

    public UiNode Command(UiCommand command)
    {
        CommandId = command.Id;
        return this;
    }

    public UiNode HitTestVisible(bool visible = true)
    {
        HitTestVisibleOverride = visible;
        return this;
    }

    public UiNode TabIndex(int tabIndex)
    {
        TabIndexValue = tabIndex;
        Behaviors |= UiBehavior.Focusable;
        return this;
    }

    public UiNode FocusScope(bool trap = false, bool restorePrevious = true)
    {
        IsFocusScope = true;
        IsFocusTrap = trap;
        RestorePreviousFocus = restorePrevious;
        return this;
    }

    public UiNode Gestures(UiGestureMask gestures)
    {
        GestureMask |= gestures;
        if (gestures != UiGestureMask.None)
            HitTestVisibleOverride = true;
        return this;
    }

    public UiNode Transition(UiAnimationProperty property, UiMotionToken motion)
    {
        UiTransitionSet transitions = Transitions;
        UiTransitionDefinition definition = new(property, motion);
        transitions.Set(in definition);
        Transitions = transitions;
        return this;
    }

    public UiNode Transition(
        UiAnimationProperty property,
        UiMotionToken motion,
        UiAnimationContinuity continuity)
    {
        UiTransitionSet transitions = Transitions;
        UiTransitionDefinition definition = new(property, motion, continuity);
        transitions.Set(in definition);
        Transitions = transitions;
        return this;
    }

    public UiNode AnimateLayout(UiMotionToken motion)
    {
        if (motion.IsNone)
            throw new ArgumentOutOfRangeException(nameof(motion));
        LayoutTransition = motion;
        return this;
    }

    public UiNode BindText(UiSelector<string> selector)
    {
        TextBinding = selector;
        return this;
    }

    public UiNode Text(string value)
    {
        StaticText = value;
        TextBinding = null;
        return this;
    }

    public UiNode Child(UiNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ChildNodes.Add(child);
        return this;
    }

    public UiNode AddChildren(params UiNode[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        foreach (UiNode child in children)
            Child(child);
        return this;
    }

    public UiNode Width(UiLength width)
    {
        LayoutStyle layout = Layout;
        layout.Width = width;
        Layout = layout;
        return this;
    }

    public UiNode Height(UiLength height)
    {
        LayoutStyle layout = Layout;
        layout.Height = height;
        Layout = layout;
        return this;
    }

    public UiNode MinSize(float width, float height)
    {
        LayoutStyle layout = Layout;
        layout.MinSize = new UiSize(width, height);
        Layout = layout;
        return this;
    }

    public UiNode MaxSize(float width, float height)
    {
        LayoutStyle layout = Layout;
        layout.MaxSize = new UiSize(width, height);
        Layout = layout;
        return this;
    }

    public UiNode Margin(UiThickness margin)
    {
        LayoutStyle layout = Layout;
        layout.Margin = margin;
        Layout = layout;
        return this;
    }

    public UiNode Padding(UiThickness padding)
    {
        LayoutStyle layout = Layout;
        layout.Padding = padding;
        Layout = layout;
        return this;
    }

    public UiNode Align(UiHorizontalAlignment horizontal, UiVerticalAlignment vertical)
    {
        LayoutStyle layout = Layout;
        layout.HorizontalAlignment = horizontal;
        layout.VerticalAlignment = vertical;
        Layout = layout;
        return this;
    }

    public UiNode LayoutBoundary(bool enabled = true)
    {
        LayoutStyle layout = Layout;
        layout.IsMeasureBoundary = enabled;
        Layout = layout;
        return this;
    }

    public UiNode Gap(float gap)
    {
        LayoutGap = Math.Max(0f, gap);
        return this;
    }

    public UiNode GridCell(int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        if (row < 0)
            throw new ArgumentOutOfRangeException(nameof(row));
        if (column < 0)
            throw new ArgumentOutOfRangeException(nameof(column));
        if (rowSpan <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowSpan));
        if (columnSpan <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnSpan));
        GridPlacement = new GridPlacement
        {
            Row = row,
            Column = column,
            RowSpan = rowSpan,
            ColumnSpan = columnSpan
        };
        HasGridPlacement = true;
        return this;
    }

    public UiNode At(float left, float top)
    {
        AbsolutePlacement = new AbsolutePlacement { Left = left, Top = top };
        HasAbsolutePlacement = true;
        return this;
    }

    public UiNode WrapText(float maxWidth)
    {
        if (maxWidth <= 0f || !float.IsFinite(maxWidth))
            throw new ArgumentOutOfRangeException(nameof(maxWidth));
        TextFormat = new TextFormat
        {
            Wrapping = UiTextWrapping.Wrap,
            MaxWidth = maxWidth,
            Direction = TextFormat.Direction
        };
        return this;
    }
}
