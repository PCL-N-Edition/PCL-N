// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Fine-grained dirty reasons for reactive ECS system dispatch.</summary>
[Flags]
public enum UiDirtyFlags : uint
{
    None = 0,
    Binding = 1u << 0,
    Structure = 1u << 1,
    Style = 1u << 2,
    TextMeasure = 1u << 3,
    LayoutMeasure = 1u << 4,
    LayoutArrange = 1u << 5,
    Transform = 1u << 6,
    Clip = 1u << 7,
    HitTest = 1u << 8,
    Render = 1u << 9,
    Accessibility = 1u << 10,
    Animation = 1u << 11,

    /// <summary>Common cascade after structural mutation.</summary>
    StructuralCascade = Structure | LayoutMeasure | LayoutArrange | HitTest | Render | Accessibility,

    /// <summary>Common cascade after visual-only mutation.</summary>
    VisualCascade = Transform | Clip | Render
}
