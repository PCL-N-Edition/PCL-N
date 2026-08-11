// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// FIFO lifecycle journal. Events survive frame boundaries and are removed only by an explicit
/// consumer drain, so reduced-motion and already-settled completions cannot be lost mid-frame.
/// </summary>
public sealed class UiAnimationEventJournal
{
    private readonly Queue<UiAnimationEvent> _pending = [];
    private long _nextSequence = 1;

    public int Count => _pending.Count;

    public bool TryDequeue(out UiAnimationEvent animationEvent) =>
        _pending.TryDequeue(out animationEvent);

    public int Drain(List<UiAnimationEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int count = 0;
        while (_pending.TryDequeue(out UiAnimationEvent animationEvent))
        {
            destination.Add(animationEvent);
            count++;
        }
        return count;
    }

    internal void Publish(long frameIndex, in UiAnimationSettled settled)
    {
        _pending.Enqueue(new UiAnimationEvent(
            NextSequence(),
            frameIndex,
            UiAnimationEventKind.Settled,
            settled,
            default));
    }

    internal void Publish(long frameIndex, in UiTransitionGroupCompleted completed)
    {
        _pending.Enqueue(new UiAnimationEvent(
            NextSequence(),
            frameIndex,
            UiAnimationEventKind.TransitionGroupCompleted,
            default,
            completed));
    }

    internal void Clear() => _pending.Clear();

    private long NextSequence()
    {
        long sequence = _nextSequence++;
        if (_nextSequence <= 0)
            _nextSequence = 1;
        return sequence;
    }
}
