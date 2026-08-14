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

    public static UiNode Overlay(params UiNode[] children)
    {
        UiNode node = new(UiNodeKind.Overlay);
        if (children is { Length: > 0 })
            node.AddChildren(children);
        return node;
    }

    public static UiNode Absolute(params UiNode[] children)
    {
        UiNode node = new(UiNodeKind.Absolute);
        if (children is { Length: > 0 })
            node.AddChildren(children);
        return node;
    }

    public static UiNode Scroll(UiNode content, UiOrientation orientation = UiOrientation.Vertical)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (orientation is not (UiOrientation.Vertical or UiOrientation.Horizontal))
            throw new ArgumentOutOfRangeException(nameof(orientation));
        return new UiNode(UiNodeKind.Scroll)
        {
            ScrollViewport = orientation == UiOrientation.Vertical
                ? ScrollViewport.Vertical
                : ScrollViewport.Horizontal,
            GestureMask = UiGestureMask.Pan,
            HitTestVisibleOverride = true
        }.Child(content);
    }

    public static UiNode VirtualList(
        float estimatedItemExtent = 48f,
        ushort overscanBefore = 6,
        ushort overscanAfter = 6,
        UiOrientation orientation = UiOrientation.Vertical)
    {
        if (!float.IsFinite(estimatedItemExtent) || estimatedItemExtent <= 0f)
            throw new ArgumentOutOfRangeException(nameof(estimatedItemExtent));
        if (orientation is not (UiOrientation.Vertical or UiOrientation.Horizontal))
            throw new ArgumentOutOfRangeException(nameof(orientation));
        return new UiNode(UiNodeKind.VirtualList)
        {
            ScrollViewport = orientation == UiOrientation.Vertical
                ? ScrollViewport.Vertical
                : ScrollViewport.Horizontal,
            Virtualization = new Virtualization
            {
                EstimatedItemExtent = estimatedItemExtent,
                OverscanBefore = overscanBefore,
                OverscanAfter = overscanAfter
            },
            GestureMask = UiGestureMask.Pan,
            HitTestVisibleOverride = true
        };
    }

    public static UiNode Grid(UiGridDefinition definition, params UiNode[] children)
    {
        ArgumentNullException.ThrowIfNull(definition);
        UiNode node = new(UiNodeKind.Grid) { GridDefinition = definition };
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

    public static UiNode TextBox(
        string? value = null,
        string? placeholder = null,
        bool password = false)
    {
        UiNode node = new(UiNodeKind.NativeHost)
        {
            NativeHost = new NativeHostComponent
            {
                Kind = password ? UiNativeHostKind.PasswordBox : UiNativeHostKind.TextBox,
                Value = value,
                Placeholder = placeholder
            },
            HitTestVisibleOverride = true,
            Behaviors = UiBehavior.Focusable
        };
        return node.Height(UiLength.Pixels(36f));
    }

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
