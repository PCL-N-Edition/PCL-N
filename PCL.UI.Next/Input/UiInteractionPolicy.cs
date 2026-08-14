// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Shared overlay-barrier policy for input, focus, commands, accessibility and native hosts.</summary>
public static class UiInteractionPolicy
{
    private static readonly UiInteractionCapability[] Capabilities =
    [
        UiInteractionCapability.Pointer,
        UiInteractionCapability.KeyboardFocus,
        UiInteractionCapability.Accessibility,
        UiInteractionCapability.CommandInvoke,
        UiInteractionCapability.NativeHost
    ];

    public static bool IsAllowed(
        UiWorld world,
        UiEntity entity,
        UiInteractionCapability capabilities)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Entities.IsAlive(entity) ||
            !world.Entities.TryGetScope(entity, out UiScopeId entityScope))
        {
            return false;
        }
        if (capabilities == UiInteractionCapability.None)
            return true;

        UiInteractionCapability unknown = capabilities & ~UiInteractionCapability.All;
        if (unknown != UiInteractionCapability.None)
            throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities, "Unknown interaction capability.");

        for (int i = 0; i < Capabilities.Length; i++)
        {
            UiInteractionCapability capability = Capabilities[i];
            if ((capabilities & capability) == 0)
                continue;
            if (!IsAllowedForCapability(world, entityScope, capability))
                return false;
        }
        return true;
    }

    internal static bool IsScopeWithin(UiWorld world, UiScopeId scope, UiScopeId ancestor)
    {
        int guard = 0;
        while (world.Scopes.IsAlive(scope) && guard++ < 1_000_000)
        {
            if (scope == ancestor)
                return true;
            if (!world.Scopes.TryGetParent(scope, out scope) || scope.IsNone)
                break;
        }
        return false;
    }

    private static bool IsAllowedForCapability(
        UiWorld world,
        UiScopeId entityScope,
        UiInteractionCapability capability)
    {
        ReadOnlySpan<UiEntity> barrierEntities = world.Components.Pool<UiInteractionBarrier>().Entities;
        UiInteractionBarrier selected = default;
        int selectedZ = int.MinValue;
        bool found = false;
        for (int i = 0; i < barrierEntities.Length; i++)
        {
            UiEntity barrierEntity = barrierEntities[i];
            if (!world.Entities.IsAlive(barrierEntity) ||
                !UiEffectiveState.IsVisible(world, barrierEntity))
            {
                continue;
            }

            UiInteractionBarrier barrier = world.Components.Get<UiInteractionBarrier>(barrierEntity);
            if ((barrier.BlockedCapabilities & capability) == 0 ||
                !IsScopeWithin(world, entityScope, barrier.RootScope) ||
                (found && barrier.ZIndex < selectedZ))
            {
                continue;
            }
            selected = barrier;
            selectedZ = barrier.ZIndex;
            found = true;
        }

        return !found || IsScopeWithin(world, entityScope, selected.AllowedScope);
    }
}
