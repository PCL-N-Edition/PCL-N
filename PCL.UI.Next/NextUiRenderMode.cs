// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Selects how the launcher builds and updates interactive UI frames.
/// This is an application architecture switch (classic visual tree vs ECS),
/// not a GPU driver / ANGLE / Vulkan platform option.
/// </summary>
public enum NextUiRenderMode
{
    /// <summary>Production path: Avalonia controls + existing layout/render tree.</summary>
    Classic = 0,

    /// <summary>
    /// Experimental path: data-oriented ECS (entities / components / systems) drives
    /// layout, dirty propagation, and draw batching to improve UI frame performance.
    /// </summary>
    Ecs = 1
}
