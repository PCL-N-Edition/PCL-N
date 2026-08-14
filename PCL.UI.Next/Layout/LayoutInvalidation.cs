// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public static class LayoutInvalidation
{
    /// <summary>Marks an entity and its measure-dependent ancestors, stopping at a boundary.</summary>
    public static void MarkMeasure(UiWorld world, UiEntity entity, bool requestFrame = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        UiEntity current = entity;
        UiEntity source = UiEntity.None;
        int guard = 0;
        while (world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            world.Dirty.Mark(
                current,
                UiDirtyFlags.LayoutMeasure | UiDirtyFlags.LayoutArrange,
                source);
            if (current != entity &&
                world.Components.TryGet(current, out LayoutStyle style) &&
                style.IsMeasureBoundary)
            {
                break;
            }

            if (!world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            source = current;
            current = node.Parent;
        }

        if (requestFrame)
            world.Scheduler.RequestReactiveFrame();
    }

    public static void MarkArrange(UiWorld world, UiEntity entity, bool requestFrame = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Entities.IsAlive(entity))
            world.Dirty.Mark(entity, UiDirtyFlags.LayoutArrange);
        if (requestFrame)
            world.Scheduler.RequestReactiveFrame();
    }
}
