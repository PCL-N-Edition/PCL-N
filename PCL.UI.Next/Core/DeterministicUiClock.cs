// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Test/dev clock that advances only when told.</summary>
public sealed class DeterministicUiClock : IUiClock
{
    private double _seconds;

    public DeterministicUiClock(double startSeconds = 0d)
    {
        _seconds = startSeconds;
    }

    public UiTimestamp Now => new(_seconds);

    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds < 0d)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        _seconds += deltaSeconds;
    }

    public void Set(double seconds)
    {
        if (seconds < 0d)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        _seconds = seconds;
    }
}
