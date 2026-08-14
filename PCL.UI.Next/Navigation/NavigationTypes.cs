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

public sealed class UiNavigationEventJournal
{
    private readonly Queue<UiNavigationEvent> _pending = [];
    private long _nextSequence = 1;

    public int Count => _pending.Count;

    public bool TryDequeue(out UiNavigationEvent navigationEvent) =>
        _pending.TryDequeue(out navigationEvent);

    public int Drain(List<UiNavigationEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int count = 0;
        while (_pending.TryDequeue(out UiNavigationEvent navigationEvent))
        {
            destination.Add(navigationEvent);
            count++;
        }
        return count;
    }

    internal void Publish(
        long frameIndex,
        UiNavigationEventKind kind,
        uint generation,
        UiPageKey page,
        UiNavigationPageState state)
    {
        _pending.Enqueue(new UiNavigationEvent(
            NextSequence(),
            frameIndex,
            kind,
            generation,
            page,
            state));
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
