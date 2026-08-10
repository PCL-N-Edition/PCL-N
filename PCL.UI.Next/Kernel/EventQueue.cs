// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>FIFO platform event queue drained once per reactive frame.</summary>
public sealed class EventQueue
{
    private readonly Queue<UiPlatformEvent> _queue = new();

    public int Count => _queue.Count;

    public bool IsEmpty => _queue.Count == 0;

    public void Enqueue(in UiPlatformEvent platformEvent) => _queue.Enqueue(platformEvent);

    public bool TryDequeue(out UiPlatformEvent platformEvent) => _queue.TryDequeue(out platformEvent);

    public void Clear() => _queue.Clear();

    /// <summary>
    /// Drains all events into <paramref name="destination"/>, optionally skipping
    /// events whose scope is no longer alive.
    /// </summary>
    public int Drain(List<UiPlatformEvent> destination, ScopeRegistry? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int accepted = 0;
        while (_queue.TryDequeue(out UiPlatformEvent e))
        {
            if (scopes is not null && !e.Scope.IsNone && !scopes.IsAlive(e.Scope))
                continue;
            destination.Add(e);
            accepted++;
        }

        return accepted;
    }
}
