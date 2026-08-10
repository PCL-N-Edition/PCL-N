// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.UI.Next.Ecs;

namespace PCL.UI.Next;

/// <summary>
/// Host-facing resolver for the experimental ECS UI render architecture.
/// Does not replace Avalonia's platform GPU backend (ANGLE/Vulkan/GL).
/// </summary>
public static class NextUiRenderRuntime
{
    /// <summary>
    /// Resolves the architecture that this process may actually run.
    /// </summary>
    public static NextUiRenderMode Resolve(bool experimentalNextRenderBackendEnabled) =>
        NextRenderAvailability.FromExperimentalSetting(experimentalNextRenderBackendEnabled);

    /// <summary>
    /// Short log label for startup diagnostics.
    /// </summary>
    public static string Describe(NextUiRenderMode mode) =>
        mode switch
        {
            NextUiRenderMode.Ecs => "ecs UI architecture (data-oriented layout/draw systems)",
            _ => "classic UI architecture (Avalonia visual tree)"
        };

    /// <summary>
    /// Creates a host instance. Safe while unimplemented — <see cref="EcsUiRenderHost.TryStart"/>
    /// will refuse to activate the pipeline.
    /// </summary>
    public static EcsUiRenderHost CreateHost() => new();
}
