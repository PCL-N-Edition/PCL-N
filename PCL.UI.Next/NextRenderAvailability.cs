// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Gates the experimental ECS UI render architecture in <c>PCL.UI.Next</c>.
/// Until <see cref="IsImplemented"/> is true, hosts must not offer an enable path
/// and must keep the classic control/visual tree path.
/// </summary>
public static class NextRenderAvailability
{
    /// <summary>
    /// When false, the ECS UI render pipeline is scaffolding only: the settings toggle
    /// stays disabled and <see cref="ResolveEffectiveMode"/> always returns
    /// <see cref="NextUiRenderMode.Classic"/>.
    /// </summary>
    public const bool IsImplemented = false;

    /// <summary>
    /// The ECS world, systems, and host bridge are process-wide; switching requires a
    /// full launcher restart. Callers must tell the user to restart once enable is allowed.
    /// </summary>
    public const bool RequiresLauncherRestart = true;

    /// <summary>Whether the experimental toggle may be turned on in settings.</summary>
    public static bool CanEnable => IsImplemented;

    /// <summary>
    /// Collapses a requested mode to what the host is allowed to apply this process.
    /// </summary>
    public static NextUiRenderMode ResolveEffectiveMode(NextUiRenderMode requested) =>
        IsImplemented && requested == NextUiRenderMode.Ecs
            ? NextUiRenderMode.Ecs
            : NextUiRenderMode.Classic;

    /// <summary>
    /// Interprets the experimental settings flag. Always classic while not implemented.
    /// </summary>
    public static NextUiRenderMode FromExperimentalSetting(bool experimentalNextRenderBackendEnabled) =>
        ResolveEffectiveMode(
            experimentalNextRenderBackendEnabled ? NextUiRenderMode.Ecs : NextUiRenderMode.Classic);
}
