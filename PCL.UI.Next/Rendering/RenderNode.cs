// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;

namespace PCL.UI.Next;

/// <summary>Generation-safe identifier for one retained render node.</summary>
public readonly record struct RenderNodeId(int Index, uint Generation)
{
    public static RenderNodeId None => default;

    public bool IsNone => Index <= 0 || Generation == 0;
}

/// <summary>Backend-neutral retained primitive kind.</summary>
public enum UiRenderNodeKind : byte
{
    Layer = 0,
    Rectangle = 1,
    RoundedRectangle = 2,
    Text = 3,
    Image = 4,
    Vector = 5,
    Clip = 6,
    Effect = 7,
    NativeHostPlaceholder = 8
}

/// <summary>
/// Immutable inspection snapshot of one node in a retained render scene.
/// Transform and opacity are local to <see cref="Parent"/> and compose in the backend.
/// </summary>
public readonly record struct UiRenderNodeSnapshot(
    RenderNodeId Id,
    UiEntity Owner,
    UiRenderNodeKind Kind,
    RenderNodeId Parent,
    long ZOrder,
    UiRect Bounds,
    Matrix3x2 Transform,
    float Opacity,
    UiColor Brush,
    float CornerRadius,
    TextLayoutHandle TextLayout);

internal struct RenderNodeState
{
    public UiEntity Owner { get; set; }
    public UiRenderNodeKind Kind { get; set; }
    public RenderNodeId Parent { get; set; }
    public long ZOrder { get; set; }
    public UiRect Bounds { get; set; }
    public Matrix3x2 Transform { get; set; }
    public float Opacity { get; set; }
    public UiColor Brush { get; set; }
    public float CornerRadius { get; set; }
    public TextLayoutHandle TextLayout { get; set; }
    public TextCacheEntryHandle TextCacheEntry { get; set; }
}
