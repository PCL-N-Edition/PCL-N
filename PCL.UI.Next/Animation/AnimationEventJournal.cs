// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiAnimationEventReaderStart : byte
{
    OldestAvailable = 0,
    NextPublished = 1
}

/// <summary>
/// Independent sequence cursor over a bounded animation event journal. A slow reader that falls
/// behind retention resumes at the oldest available event and reports the skipped sequence count.
/// </summary>
public sealed class UiAnimationEventReader
{
    private readonly UiAnimationEventJournal _journal;

    internal UiAnimationEventReader(UiAnimationEventJournal journal, long nextSequence)
    {
        _journal = journal;
        NextSequence = nextSequence;
    }

    public long DroppedCount { get; private set; }

    internal long NextSequence { get; set; }

    public bool TryRead(out UiAnimationEvent animationEvent) =>
        _journal.TryRead(this, out animationEvent);

    public int Drain(List<UiAnimationEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int count = 0;
        while (TryRead(out UiAnimationEvent animationEvent))
        {
            destination.Add(animationEvent);
            count++;
        }
        return count;
    }

    internal void AddDropped(long count) => DroppedCount = checked(DroppedCount + count);
}

/// <summary>
/// Bounded, sequence-based multi-reader lifecycle journal. Readers never remove events needed by
/// other consumers; fixed retention prevents an absent diagnostics consumer from growing memory.
/// </summary>
public sealed class UiAnimationEventJournal
{
    public const int DefaultCapacity = 1_024;

    private readonly object _gate = new();
    private readonly UiAnimationEvent[] _buffer;
    private readonly UiAnimationEventReader _defaultReader;
    private int _head;
    private int _retainedCount;
    private long _nextSequence = 1;

    public UiAnimationEventJournal(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new UiAnimationEvent[capacity];
        _defaultReader = new UiAnimationEventReader(this, _nextSequence);
    }

    public int Capacity => _buffer.Length;

    /// <summary>Unread event count for the compatibility <see cref="TryDequeue"/> reader.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return GetUnreadCount(_defaultReader);
        }
    }

    /// <summary>Number of entries currently retained for all readers.</summary>
    public int RetainedCount
    {
        get
        {
            lock (_gate)
                return _retainedCount;
        }
    }

    public UiAnimationEventReader CreateReader(
        UiAnimationEventReaderStart start = UiAnimationEventReaderStart.OldestAvailable)
    {
        if (!Enum.IsDefined(start))
            throw new ArgumentOutOfRangeException(nameof(start));
        lock (_gate)
        {
            long sequence = start == UiAnimationEventReaderStart.NextPublished
                ? _nextSequence
                : FirstAvailableSequence();
            return new UiAnimationEventReader(this, sequence);
        }
    }

    /// <summary>
    /// Compatibility reader retained for single-consumer call sites. Reading here does not remove
    /// entries from independent readers created with <see cref="CreateReader"/>.
    /// </summary>
    public bool TryDequeue(out UiAnimationEvent animationEvent) =>
        TryRead(_defaultReader, out animationEvent);

    public int Drain(List<UiAnimationEvent> destination) => _defaultReader.Drain(destination);

    internal void Publish(long frameIndex, in UiAnimationSettled settled)
    {
        lock (_gate)
        {
            Append(new UiAnimationEvent(
                NextSequence(),
                frameIndex,
                UiAnimationEventKind.Settled,
                settled,
                default));
        }
    }

    internal void Publish(long frameIndex, in UiTransitionGroupCompleted completed)
    {
        lock (_gate)
        {
            Append(new UiAnimationEvent(
                NextSequence(),
                frameIndex,
                UiAnimationEventKind.TransitionGroupCompleted,
                default,
                completed));
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _head = 0;
            _retainedCount = 0;
            _defaultReader.NextSequence = _nextSequence;
        }
    }

    internal bool TryRead(UiAnimationEventReader reader, out UiAnimationEvent animationEvent)
    {
        lock (_gate)
        {
            long first = FirstAvailableSequence();
            if (reader.NextSequence < first)
            {
                reader.AddDropped(first - reader.NextSequence);
                reader.NextSequence = first;
            }
            if (reader.NextSequence >= _nextSequence || _retainedCount == 0)
            {
                animationEvent = default;
                return false;
            }

            int offset = checked((int)(reader.NextSequence - first));
            if (offset < 0 || offset >= _retainedCount)
            {
                animationEvent = default;
                return false;
            }
            int index = (_head + offset) % _buffer.Length;
            animationEvent = _buffer[index];
            reader.NextSequence = checked(animationEvent.Sequence + 1);
            return true;
        }
    }

    private int GetUnreadCount(UiAnimationEventReader reader)
    {
        long first = FirstAvailableSequence();
        long cursor = Math.Max(first, reader.NextSequence);
        return checked((int)Math.Min(_retainedCount, Math.Max(0L, _nextSequence - cursor)));
    }

    private long FirstAvailableSequence() =>
        _retainedCount == 0 ? _nextSequence : _buffer[_head].Sequence;

    private void Append(UiAnimationEvent animationEvent)
    {
        if (_retainedCount == _buffer.Length)
        {
            _head = (_head + 1) % _buffer.Length;
            _retainedCount--;
        }
        int index = (_head + _retainedCount) % _buffer.Length;
        _buffer[index] = animationEvent;
        _retainedCount++;
    }

    private long NextSequence()
    {
        if (_nextSequence == long.MaxValue)
            throw new InvalidOperationException("Animation event sequence space is exhausted.");
        return _nextSequence++;
    }
}
