// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Per-frame inputs for system pipeline execution.</summary>
public readonly struct UiFrameContext
{
    public UiFrameContext(long frameIndex, double deltaSeconds, UiTimestamp now)
    {
        FrameIndex = frameIndex;
        DeltaSeconds = deltaSeconds;
        Now = now;
    }

    public long FrameIndex { get; }
    public double DeltaSeconds { get; }
    public UiTimestamp Now { get; }
}
