// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Reasons that keep the runtime in continuous frame mode.</summary>
[Flags]
public enum UiContinuousReason : uint
{
    None = 0,
    Animation = 1u << 0,
    ScrollInertia = 1u << 1,
    CaretBlink = 1u << 2,
    Video = 1u << 3,
    RealtimeEffect = 1u << 4
}

/// <summary>
/// Idle / reactive / continuous frame scheduler (architecture §32).
/// Does not pump OS frames itself — the host polls <see cref="NeedsFrame"/>.
/// </summary>
public sealed class UiFrameScheduler
{
    private bool _reactiveRequested;
    private UiContinuousReason _continuous;

    public bool NeedsFrame => _reactiveRequested || _continuous != UiContinuousReason.None;

    public bool HasContinuous => _continuous != UiContinuousReason.None;

    public UiContinuousReason ContinuousReasons => _continuous;

    public void RequestReactiveFrame() => _reactiveRequested = true;

    public void RequestContinuousFrame(UiContinuousReason reason)
    {
        if (reason == UiContinuousReason.None)
            return;
        _continuous |= reason;
    }

    public void ReleaseContinuousFrame(UiContinuousReason reason)
    {
        if (reason == UiContinuousReason.None)
            return;
        _continuous &= ~reason;
    }

    /// <summary>Consumes the reactive flag after a frame is executed.</summary>
    public void AcknowledgeReactiveFrame() => _reactiveRequested = false;

    public void Reset()
    {
        _reactiveRequested = false;
        _continuous = UiContinuousReason.None;
    }
}
