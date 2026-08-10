// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;

namespace PCL.UI.Next;

/// <summary>Production clock backed by a high-resolution stopwatch from construction.</summary>
public sealed class StopwatchUiClock : IUiClock
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public UiTimestamp Now =>
        new(Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds);
}
