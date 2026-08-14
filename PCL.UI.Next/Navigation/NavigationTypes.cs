// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public readonly struct UiPageKey : IEquatable<UiPageKey>
{
    public UiPageKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Page key cannot be empty.", nameof(value));
        Value = value;
    }

    public string? Value { get; }
    public bool IsNone => string.IsNullOrEmpty(Value);
    public bool Equals(UiPageKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is UiPageKey other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);
    public static bool operator ==(UiPageKey left, UiPageKey right) => left.Equals(right);
    public static bool operator !=(UiPageKey left, UiPageKey right) => !left.Equals(right);
    public override string ToString() => Value ?? "Page(None)";
}

public enum UiNavigationPageState : byte
{
    Created = 0,
    Preparing = 1,
    Entering = 2,
    Active = 3,
    Leaving = 4,
    Dormant = 5,
    Destroyed = 6
}

public enum UiPageCachePolicy : byte
{
    None = 0,
    KeepPresentationState = 1,
    KeepEntities = 2,
    Lru = 3,
    Pinned = 4
}

public sealed class UiPageDefinition
{
    public UiPageDefinition(
        UiPageKey key,
        UiBlueprint blueprint,
        UiPageCachePolicy cachePolicy = UiPageCachePolicy.None)
    {
        if (key.IsNone)
            throw new ArgumentOutOfRangeException(nameof(key));
        if (!Enum.IsDefined(cachePolicy))
            throw new ArgumentOutOfRangeException(nameof(cachePolicy));
        Key = key;
        Blueprint = blueprint ?? throw new ArgumentNullException(nameof(blueprint));
        CachePolicy = cachePolicy;
    }

    public UiPageKey Key { get; }
    public UiBlueprint Blueprint { get; }
    public UiPageCachePolicy CachePolicy { get; }
}

public readonly record struct UiNavigationOptions(
    int LruCapacity,
    float EnterOffset,
    float ExitOffset,
    UiMotionToken Motion)
{
    public static UiNavigationOptions Default => new(3, 24f, -16f, UiMotion.Navigation);
}

public readonly record struct UiNavigationRequest(UiPageKey Page, uint Generation);

public struct NavigationPageComponent
{
    public UiPageKey Page { get; set; }
    public UiNavigationPageState State { get; set; }
    public uint NavigationGeneration { get; set; }
}

public readonly record struct UiNavigationPageSnapshot(
    UiPageKey Page,
    UiNavigationPageState State,
    UiPageCachePolicy CachePolicy,
    UiScopeId Scope,
    UiEntity RootEntity,
    uint NavigationGeneration,
    long LastUsedSequence);

public enum UiNavigationEventKind : byte
{
    Requested = 0,
    StateChanged = 1,
    Completed = 2,
    Canceled = 3,
    CacheEvicted = 4
}

public readonly record struct UiNavigationEvent(
    long Sequence,
    long FrameIndex,
    UiNavigationEventKind Kind,
    uint NavigationGeneration,
    UiPageKey Page,
    UiNavigationPageState State);

public enum UiNavigationEventReaderStart : byte
{
    OldestAvailable = 0,
    NextPublished = 1
}

public sealed class UiNavigationEventReader
{
    private readonly UiNavigationEventJournal _journal;

    internal UiNavigationEventReader(UiNavigationEventJournal journal, long nextSequence)
    {
        _journal = journal;
        NextSequence = nextSequence;
    }

    public long DroppedCount { get; private set; }

    internal long NextSequence { get; set; }

    public bool TryRead(out UiNavigationEvent navigationEvent) =>
        _journal.TryRead(this, out navigationEvent);

    public int Drain(List<UiNavigationEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int count = 0;
        while (TryRead(out UiNavigationEvent navigationEvent))
        {
            destination.Add(navigationEvent);
            count++;
        }
        return count;
    }

    internal void AddDropped(long count) => DroppedCount = checked(DroppedCount + count);
}

public sealed class UiNavigationEventJournal
{
    public const int DefaultCapacity = 1_024;

    private readonly object _gate = new();
    private readonly UiNavigationEvent[] _buffer;
    private readonly UiNavigationEventReader _defaultReader;
    private int _head;
    private int _retainedCount;
    private long _nextSequence = 1;

    public UiNavigationEventJournal(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new UiNavigationEvent[capacity];
        _defaultReader = new UiNavigationEventReader(this, _nextSequence);
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get
        {
            lock (_gate)
                return GetUnreadCount(_defaultReader);
        }
    }

    public int RetainedCount
    {
        get
        {
            lock (_gate)
                return _retainedCount;
        }
    }

    public UiNavigationEventReader CreateReader(
        UiNavigationEventReaderStart start = UiNavigationEventReaderStart.OldestAvailable)
    {
        if (!Enum.IsDefined(start))
            throw new ArgumentOutOfRangeException(nameof(start));
        lock (_gate)
        {
            long sequence = start == UiNavigationEventReaderStart.NextPublished
                ? _nextSequence
                : FirstAvailableSequence();
            return new UiNavigationEventReader(this, sequence);
        }
    }

    public bool TryDequeue(out UiNavigationEvent navigationEvent) =>
        TryRead(_defaultReader, out navigationEvent);

    public int Drain(List<UiNavigationEvent> destination) => _defaultReader.Drain(destination);

    internal void Publish(
        long frameIndex,
        UiNavigationEventKind kind,
        uint generation,
        UiPageKey page,
        UiNavigationPageState state)
    {
        lock (_gate)
        {
            Append(new UiNavigationEvent(
                NextSequence(),
                frameIndex,
                kind,
                generation,
                page,
                state));
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

    internal bool TryRead(UiNavigationEventReader reader, out UiNavigationEvent navigationEvent)
    {
        lock (_gate)
        {
            long first = FirstAvailableSequence();
            if (reader.NextSequence < first)
            {
                reader.AddDropped(first - reader.NextSequence);
                reader.NextSequence = first;
            }
            if (_retainedCount == 0 || reader.NextSequence >= _nextSequence)
            {
                navigationEvent = default;
                return false;
            }
            int offset = checked((int)(reader.NextSequence - first));
            if ((uint)offset >= (uint)_retainedCount)
            {
                navigationEvent = default;
                return false;
            }
            navigationEvent = _buffer[(_head + offset) % _buffer.Length];
            reader.NextSequence = checked(navigationEvent.Sequence + 1);
            return true;
        }
    }

    private int GetUnreadCount(UiNavigationEventReader reader)
    {
        long first = FirstAvailableSequence();
        long cursor = Math.Max(first, reader.NextSequence);
        return checked((int)Math.Min(_retainedCount, Math.Max(0L, _nextSequence - cursor)));
    }

    private long FirstAvailableSequence() =>
        _retainedCount == 0 ? _nextSequence : _buffer[_head].Sequence;

    private void Append(UiNavigationEvent navigationEvent)
    {
        if (_retainedCount == _buffer.Length)
        {
            _head = (_head + 1) % _buffer.Length;
            _retainedCount--;
        }
        int index = (_head + _retainedCount) % _buffer.Length;
        _buffer[index] = navigationEvent;
        _retainedCount++;
    }

    private long NextSequence()
    {
        if (_nextSequence == long.MaxValue)
            throw new InvalidOperationException("Navigation event sequence space is exhausted.");
        return _nextSequence++;
    }
}
