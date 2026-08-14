// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.DevTools;

public sealed record UiEntityInspection(
    UiEntity Entity,
    UiScopeId Scope,
    UiEntity Parent,
    ushort Depth,
    UiDirtyFlags DirtyFlags,
    IReadOnlyList<UiEntity> Children,
    IReadOnlyList<string> Components);

public readonly record struct UiLayoutInspection(
    UiEntity Entity,
    UiEntity LayoutParent,
    UiSize? DesiredSize,
    UiRect? LayoutRect,
    UiSize? LastMeasureConstraint,
    bool IsMeasureBoundary,
    UiDirtyFlags DirtyFlags);

public sealed record UiInteractionInspection(
    UiEntity Entity,
    UiInputRootId InputRoot,
    UiRect? HitTestBounds,
    UiEntity Focused,
    UiEntity Hovered,
    UiEntity Pressed,
    UiEntity Captured,
    IReadOnlyList<UiEntity> BubbleRoute);

public readonly record struct UiMotionTraceSample(
    long FrameIndex,
    UiTimestamp Timestamp,
    UiAnimationSnapshot Animation);

