// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Per-frame scratch buffers drained from queues for system consumption.
/// Lists are reused across frames (Clear, not re-allocate) to avoid GC on the hot path
/// once real workloads land; the current implementation uses managed lists intentionally.
/// </summary>
public sealed class UiFrameBuffers
{
    public List<UiPlatformEvent> PlatformEvents { get; } = [];

    public List<UiStatePatch> StatePatches { get; } = [];

    public void ClearPlatformEvents() => PlatformEvents.Clear();

    public void ClearStatePatches() => StatePatches.Clear();

    public void ClearAll()
    {
        PlatformEvents.Clear();
        StatePatches.Clear();
    }
}
