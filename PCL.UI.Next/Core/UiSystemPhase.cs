// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Fixed, deterministic system pipeline phases (architecture §34).
/// Order is ordinal; do not reorder without a versioned migration.
/// </summary>
public enum UiSystemPhase : byte
{
    DrainPlatformEvents = 0,
    DrainStatePatches = 1,
    InputNormalize = 2,
    HitTest = 3,
    Interaction = 4,
    FocusGestureShortcut = 5,
    BindingUpdate = 6,
    StructuralReconcile = 7,
    StyleResolve = 8,
    VirtualizationPlan = 9,
    TextImageMeasure = 10,
    LayoutMeasure = 11,
    LayoutArrange = 12,
    TransitionPlanning = 13,
    AnimationTick = 14,
    Transform = 15,
    ClipHitTestUpdate = 16,
    AccessibilityUpdate = 17,
    RenderDiff = 18,
    BackendCommit = 19
}
