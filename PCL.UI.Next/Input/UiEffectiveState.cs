// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Ancestor-aware visibility and enabled state shared by interactive subsystems.</summary>
public static class UiEffectiveState
{
    public static bool IsVisible(UiWorld world, UiEntity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Entities.IsAlive(entity))
            return false;
        UiEntity current = entity;
        int guard = 0;
        while (world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (world.Components.TryGet(current, out HitTestableComponent hit) && !hit.IsVisible)
                return false;
            if (world.Components.TryGet(current, out VirtualItemSlot slot) && !slot.IsRealized)
                return false;
            if (!world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent.IsNone)
                return true;
            current = node.Parent;
        }
        return false;
    }

    public static bool IsEnabled(UiWorld world, UiEntity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Entities.IsAlive(entity))
            return false;
        UiEntity current = entity;
        int guard = 0;
        while (world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            if (world.Components.TryGet(current, out HitTestableComponent hit) && !hit.IsEnabled)
                return false;
            if (world.Components.TryGet(current, out InteractionStateComponent interaction) &&
                (interaction.Value & InteractionState.Disabled) != 0)
            {
                return false;
            }
            if (!world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent.IsNone)
                return true;
            current = node.Parent;
        }
        return false;
    }

    public static bool IsInteractive(UiWorld world, UiEntity entity) =>
        IsVisible(world, entity) && IsEnabled(world, entity);
}
