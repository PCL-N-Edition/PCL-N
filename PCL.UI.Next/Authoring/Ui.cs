// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Fluent authoring entry points (architecture §19 / §109).
/// Produces <see cref="UiNode"/> trees that compile to <see cref="UiBlueprint"/>.
/// </summary>
public static class Ui
{
    public static UiNode Column(params UiNode[] children)
    {
        UiNode node = new(UiNodeKind.Column);
        if (children is { Length: > 0 })
            node.AddChildren(children);
        return node;
    }

    public static UiNode Row(params UiNode[] children)
    {
        UiNode node = new(UiNodeKind.Row);
        if (children is { Length: > 0 })
            node.AddChildren(children);
        return node;
    }

    public static UiNode Container(params UiNode[] children)
    {
        UiNode node = new(UiNodeKind.Container);
        if (children is { Length: > 0 })
            node.AddChildren(children);
        return node;
    }

    public static UiNode Text(string? value = null)
    {
        UiNode node = new(UiNodeKind.Text);
        if (value is not null)
            node.StaticText = value;
        return node;
    }

    public static UiNode Button(UiNode content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new UiNode(UiNodeKind.Button)
            .Behavior(UiBehavior.ButtonDefaults)
            .Class(UiClass.Button)
            .Child(content);
    }

    public static UiNode Button(string label) => Button(Text(label));

    /// <summary>
    /// Structural condition. True/false branches are templates; only one is
    /// instantiated at a time and reconciled when the condition version changes.
    /// </summary>
    public static UiNode If(UiSelector<bool> condition, UiNode whenTrue, UiNode? whenFalse = null)
    {
        ArgumentNullException.ThrowIfNull(whenTrue);
        return new UiNode(UiNodeKind.If)
        {
            Condition = condition,
            WhenTrue = whenTrue,
            WhenFalse = whenFalse
        };
    }

    /// <summary>Compile authoring tree into an immutable blueprint.</summary>
    public static UiBlueprint Compile(UiNode root, string name = "Blueprint") =>
        UiBlueprintCompiler.Compile(root, name);
}
