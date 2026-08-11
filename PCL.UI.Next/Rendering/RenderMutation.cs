// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;

namespace PCL.UI.Next;

public enum RenderMutationKind : byte
{
    CreateNode = 0,
    DestroyNode = 1,
    SetParent = 2,
    SetZOrder = 3,
    SetBounds = 4,
    SetTransform = 5,
    SetOpacity = 6,
    SetBrush = 7,
    SetCornerRadius = 8,
    SetTextLayout = 9,
    SetNodeKind = 10
}

/// <summary>
/// Compact discriminated mutation passed across the Runtime/backend boundary.
/// Only the payload selected by <see cref="Kind"/> is meaningful.
/// </summary>
public readonly struct RenderMutation
{
    private RenderMutation(
        RenderMutationKind kind,
        RenderNodeId node,
        RenderNodeId relatedNode = default,
        UiEntity owner = default,
        UiRenderNodeKind nodeKind = default,
        long integer = default,
        UiRect bounds = default,
        Matrix3x2 transform = default,
        float scalar = default,
        UiColor color = default,
        TextLayoutHandle textLayout = default)
    {
        Kind = kind;
        Node = node;
        RelatedNode = relatedNode;
        Owner = owner;
        NodeKind = nodeKind;
        Integer = integer;
        Bounds = bounds;
        Transform = transform;
        Scalar = scalar;
        Color = color;
        TextLayout = textLayout;
    }

    public RenderMutationKind Kind { get; }
    public RenderNodeId Node { get; }
    public RenderNodeId RelatedNode { get; }
    public UiEntity Owner { get; }
    public UiRenderNodeKind NodeKind { get; }
    public long Integer { get; }
    public UiRect Bounds { get; }
    public Matrix3x2 Transform { get; }
    public float Scalar { get; }
    public UiColor Color { get; }
    public TextLayoutHandle TextLayout { get; }

    public static RenderMutation Create(
        RenderNodeId node,
        UiEntity owner,
        UiRenderNodeKind kind) =>
        new(RenderMutationKind.CreateNode, node, owner: owner, nodeKind: kind);

    public static RenderMutation Destroy(RenderNodeId node) =>
        new(RenderMutationKind.DestroyNode, node);

    public static RenderMutation SetParent(RenderNodeId node, RenderNodeId parent) =>
        new(RenderMutationKind.SetParent, node, relatedNode: parent);

    public static RenderMutation SetZOrder(RenderNodeId node, long zOrder) =>
        new(RenderMutationKind.SetZOrder, node, integer: zOrder);

    public static RenderMutation SetBounds(RenderNodeId node, UiRect bounds) =>
        new(RenderMutationKind.SetBounds, node, bounds: bounds);

    public static RenderMutation SetTransform(RenderNodeId node, Matrix3x2 transform) =>
        new(RenderMutationKind.SetTransform, node, transform: transform);

    public static RenderMutation SetOpacity(RenderNodeId node, float opacity) =>
        new(RenderMutationKind.SetOpacity, node, scalar: opacity);

    public static RenderMutation SetBrush(RenderNodeId node, UiColor color) =>
        new(RenderMutationKind.SetBrush, node, color: color);

    public static RenderMutation SetCornerRadius(RenderNodeId node, float cornerRadius) =>
        new(RenderMutationKind.SetCornerRadius, node, scalar: cornerRadius);

    public static RenderMutation SetTextLayout(RenderNodeId node, TextLayoutHandle textLayout) =>
        new(RenderMutationKind.SetTextLayout, node, textLayout: textLayout);

    public static RenderMutation SetNodeKind(RenderNodeId node, UiRenderNodeKind kind) =>
        new(RenderMutationKind.SetNodeKind, node, nodeKind: kind);
}
