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

    public UiNode Class(UiClass styleClass)
    {
        if (!StyleClassIds.Contains(styleClass.Id))
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
}
