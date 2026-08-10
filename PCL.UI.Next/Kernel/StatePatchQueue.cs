// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>FIFO presentation state patch queue with generation filtering on drain.</summary>
public sealed class StatePatchQueue
{
    private readonly Queue<UiStatePatch> _queue = new();

    public int Count => _queue.Count;

    public bool IsEmpty => _queue.Count == 0;

    public void Enqueue(in UiStatePatch patch) => _queue.Enqueue(patch);

    public bool TryDequeue(out UiStatePatch patch) => _queue.TryDequeue(out patch);

    public void Clear() => _queue.Clear();

    /// <summary>
    /// Drains patches. Drops items when the target scope is dead or when
    /// <see cref="UiStatePatch.RequestGeneration"/> does not match the live scope generation
    /// (stale async completion).
    /// </summary>
    public int Drain(List<UiStatePatch> destination, ScopeRegistry scopes)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(scopes);

        int accepted = 0;
        while (_queue.TryDequeue(out UiStatePatch patch))
        {
            if (!patch.Scope.IsNone)
            {
                if (!scopes.IsAlive(patch.Scope))
                    continue;
                if (patch.RequestGeneration != 0 && patch.RequestGeneration != patch.Scope.Generation)
                    continue;
            }

            destination.Add(patch);
            accepted++;
        }

        return accepted;
    }
}
