// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Runtime clock for animation and frame scheduling.
/// Production uses wall/monotonic time; tests inject a deterministic clock.
/// </summary>
public interface IUiClock
{
    UiTimestamp Now { get; }
}
