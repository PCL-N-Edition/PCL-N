// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;

namespace PCL.UI.Next;

public enum UiTextWrapping : byte
{
    NoWrap = 0,
    Wrap = 1
}

public enum UiTextDirection : byte
{
    Auto = 0,
    LeftToRight = 1,
    RightToLeft = 2
}

public struct TextFormat
{
    public UiTextWrapping Wrapping { get; set; }
    public float MaxWidth { get; set; }
    public UiTextDirection Direction { get; set; }

    public static TextFormat Default => new()
    {
        Wrapping = UiTextWrapping.NoWrap,
        MaxWidth = float.PositiveInfinity,
        Direction = UiTextDirection.Auto
    };
}

public readonly record struct TextLayoutHandle(int Index, uint Generation)
{
    public static TextLayoutHandle None => default;

    public bool IsNone => Index <= 0 || Generation == 0;
}

public readonly record struct TextLayoutRequest(
    string Text,
    int FontFamilyId,
    float FontSize,
    int FontWeight,
    float WidthConstraint,
    UiTextWrapping Wrapping,
    UiTextDirection Direction);

/// <summary>Backend text shaping/measurement contract. Handles have explicit ownership.</summary>
public interface ITextEngine
{
    TextLayoutHandle Layout(in TextLayoutRequest request);

    UiSize Measure(TextLayoutHandle handle);

    void Release(TextLayoutHandle handle);
}

internal readonly record struct TextCacheEntryHandle(int Index, uint Generation)
{
    public static TextCacheEntryHandle None => default;

    public bool IsNone => Index <= 0 || Generation == 0;
}

public struct TextLayout
{
    public TextLayoutHandle Handle { get; set; }
    public UiSize Size { get; set; }
    internal TextCacheEntryHandle CacheEntry { get; set; }
}

/// <summary>
/// Reference-aware bounded LRU. Unused entries are evicted down to <see cref="MaxEntries"/>;
/// layouts referenced by live entities or retained render scenes are pinned and may temporarily
/// exceed the cache cap.
/// </summary>
public sealed class TextLayoutCache : IDisposable
{
    private readonly ITextEngine _engine;
    private readonly Dictionary<TextLayoutRequest, int> _byRequest = new();
    private readonly List<Entry> _entries = [new Entry()];
    private readonly Stack<int> _free = new();
    private long _accessClock;
    private int _count;
    private int _borrowCount;
    private bool _disposed;

    public TextLayoutCache(ITextEngine engine, int maxEntries = 512)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        MaxEntries = maxEntries;
    }

    public int Count => _count;

    public int MaxEntries { get; }

    internal int BorrowCount => _borrowCount;

    internal IDisposable AcquireBorrowLease()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _borrowCount = checked(_borrowCount + 1);
        return new BorrowLease(this);
    }

    internal void EnsureCanDispose()
    {
        if (_disposed)
            return;
        if (_borrowCount != 0)
        {
            throw new InvalidOperationException(
                "Text layout cache cannot be disposed while rendering runtimes still hold borrow leases.");
        }
    }

    internal void Retain(TextCacheEntryHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryGet(handle, out Entry entry))
            throw new InvalidOperationException("Cannot retain a stale text layout cache entry: " + handle);
        entry.ReferenceCount = checked(entry.ReferenceCount + 1);
        entry.LastAccess = NextAccess();
    }

    internal TextLayout Acquire(in TextLayoutRequest request, TextCacheEntryHandle previous)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (TryGet(previous, out Entry previousEntry) && previousEntry.Request == request)
        {
            previousEntry.LastAccess = NextAccess();
            return ToLayout(previous, previousEntry);
        }

        TextCacheEntryHandle acquired;
        Entry acquiredEntry;
        if (_byRequest.TryGetValue(request, out int existingIndex))
        {
            acquiredEntry = _entries[existingIndex];
            acquiredEntry.ReferenceCount++;
            acquiredEntry.LastAccess = NextAccess();
            acquired = new TextCacheEntryHandle(existingIndex, acquiredEntry.Generation);
        }
        else
        {
            TextLayoutHandle engineHandle = _engine.Layout(in request);
            if (engineHandle.IsNone)
                throw new InvalidOperationException("Text engine returned an invalid layout handle.");

            int index = AllocateEntry();
            acquiredEntry = _entries[index];
            acquiredEntry.Alive = true;
            acquiredEntry.Request = request;
            acquiredEntry.EngineHandle = engineHandle;
            acquiredEntry.Size = _engine.Measure(engineHandle);
            acquiredEntry.ReferenceCount = 1;
            acquiredEntry.LastAccess = NextAccess();
            _byRequest.Add(request, index);
            _count++;
            acquired = new TextCacheEntryHandle(index, acquiredEntry.Generation);
        }

        Release(previous);
        TrimToCapacity();
        return ToLayout(acquired, acquiredEntry);
    }

    internal void Release(TextCacheEntryHandle handle)
    {
        if (!TryGet(handle, out Entry entry))
            return;
        if (entry.ReferenceCount <= 0)
            throw new InvalidOperationException("Text layout cache reference count underflow.");
        entry.ReferenceCount--;
        entry.LastAccess = NextAccess();
        TrimToCapacity();
    }

    /// <summary>Evicts every currently unused entry, regardless of capacity.</summary>
    public void ClearUnused()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (TryFindOldestUnused(out int index))
            Evict(index);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        EnsureCanDispose();
        for (int i = 1; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (entry.Alive)
            {
                _engine.Release(entry.EngineHandle);
                entry.Alive = false;
                entry.ReferenceCount = 0;
                entry.EngineHandle = TextLayoutHandle.None;
                entry.Size = UiSize.Zero;
                entry.Generation = NextGeneration(entry.Generation);
            }
        }
        _byRequest.Clear();
        _free.Clear();
        _count = 0;
        _disposed = true;
    }

    private void ReleaseBorrowLease()
    {
        if (_borrowCount <= 0)
            throw new InvalidOperationException("Text layout cache borrow count underflow.");
        _borrowCount--;
    }

    private int AllocateEntry()
    {
        if (_free.Count > 0)
            return _free.Pop();
        _entries.Add(new Entry { Generation = 1 });
        return _entries.Count - 1;
    }

    private void TrimToCapacity()
    {
        while (_count > MaxEntries && TryFindOldestUnused(out int index))
            Evict(index);
    }

    private bool TryFindOldestUnused(out int index)
    {
        index = 0;
        long oldest = long.MaxValue;
        for (int i = 1; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Alive || entry.ReferenceCount != 0 || entry.LastAccess >= oldest)
                continue;
            oldest = entry.LastAccess;
            index = i;
        }
        return index != 0;
    }

    private void Evict(int index)
    {
        Entry entry = _entries[index];
        if (!entry.Alive || entry.ReferenceCount != 0)
            throw new InvalidOperationException("Only unused live text layouts may be evicted.");

        _byRequest.Remove(entry.Request);
        _engine.Release(entry.EngineHandle);
        entry.Alive = false;
        entry.Request = default;
        entry.EngineHandle = TextLayoutHandle.None;
        entry.Size = UiSize.Zero;
        entry.LastAccess = 0;
        entry.Generation = NextGeneration(entry.Generation);
        _free.Push(index);
        _count--;
    }

    private bool TryGet(TextCacheEntryHandle handle, out Entry entry)
    {
        if (handle.IsNone || handle.Index >= _entries.Count)
        {
            entry = null!;
            return false;
        }
        entry = _entries[handle.Index];
        return entry.Alive && entry.Generation == handle.Generation;
    }

    private static TextLayout ToLayout(TextCacheEntryHandle cacheHandle, Entry entry) => new()
    {
        CacheEntry = cacheHandle,
        Handle = entry.EngineHandle,
        Size = entry.Size
    };

    private long NextAccess() => unchecked(++_accessClock);

    private static uint NextGeneration(uint generation)
    {
        uint next = unchecked(generation + 1);
        return next == 0 ? 1 : next;
    }

    private sealed class Entry
    {
        public uint Generation { get; set; } = 1;
        public bool Alive { get; set; }
        public TextLayoutRequest Request { get; set; }
        public TextLayoutHandle EngineHandle { get; set; }
        public UiSize Size { get; set; }
        public int ReferenceCount { get; set; }
        public long LastAccess { get; set; }
    }

    private sealed class BorrowLease(TextLayoutCache owner) : IDisposable
    {
        private TextLayoutCache? _owner = owner;

        public void Dispose()
        {
            TextLayoutCache? current = _owner;
            if (current is null)
                return;
            _owner = null;
            current.ReleaseBorrowLease();
        }
    }
}

/// <summary>
/// Headless deterministic metrics for tests and early playground work. Production backends
/// replace this with a mature shaping engine; this class intentionally does not claim Unicode
/// shaping or font fallback support.
/// </summary>
public sealed class DeterministicTextEngine : ITextEngine
{
    private readonly List<Entry> _entries = [default];
    private readonly Stack<int> _free = new();
    private int _layoutCount;

    public int LayoutCount => _layoutCount;

    public int ReleaseCount { get; private set; }

    public TextLayoutHandle Layout(in TextLayoutRequest request)
    {
        float fontSize = Math.Max(1f, request.FontSize);
        float lineHeight = fontSize * 1.2f;
        float maxWidth = NormalizeConstraint(request.WidthConstraint);
        float currentWidth = 0f;
        float widest = 0f;
        int lines = 1;

        foreach (Rune rune in request.Text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                widest = Math.Max(widest, currentWidth);
                currentWidth = 0f;
                lines++;
                continue;
            }

            float advance = rune.Value <= 0x7f ? fontSize * 0.6f : fontSize;
            if (request.Wrapping == UiTextWrapping.Wrap &&
                float.IsFinite(maxWidth) &&
                currentWidth > 0f &&
                currentWidth + advance > maxWidth)
            {
                widest = Math.Max(widest, currentWidth);
                currentWidth = advance;
                lines++;
            }
            else
            {
                currentWidth += advance;
            }
        }

        widest = Math.Max(widest, currentWidth);
        if (float.IsFinite(maxWidth))
            widest = Math.Min(widest, maxWidth);

        int index;
        Entry entry;
        if (_free.Count > 0)
        {
            index = _free.Pop();
            entry = _entries[index];
        }
        else
        {
            index = _entries.Count;
            entry = new Entry { Generation = 1 };
            _entries.Add(entry);
        }

        entry.Alive = true;
        entry.Size = new UiSize(widest, lines * lineHeight);
        _entries[index] = entry;
        _layoutCount++;
        return new TextLayoutHandle(index, entry.Generation);
    }

    public UiSize Measure(TextLayoutHandle handle)
    {
        if (!TryGet(handle, out Entry entry))
            throw new InvalidOperationException("Text layout handle is stale or invalid: " + handle);
        return entry.Size;
    }

    public void Release(TextLayoutHandle handle)
    {
        if (!TryGet(handle, out Entry entry))
            return;
        entry.Alive = false;
        entry.Size = UiSize.Zero;
        entry.Generation = NextGeneration(entry.Generation);
        _entries[handle.Index] = entry;
        _free.Push(handle.Index);
        _layoutCount--;
        ReleaseCount++;
    }

    private bool TryGet(TextLayoutHandle handle, out Entry entry)
    {
        if (handle.IsNone || handle.Index >= _entries.Count)
        {
            entry = default;
            return false;
        }
        entry = _entries[handle.Index];
        return entry.Alive && entry.Generation == handle.Generation;
    }

    private static float NormalizeConstraint(float value) =>
        value < 0f || float.IsNaN(value) ? float.PositiveInfinity : value;

    private static uint NextGeneration(uint generation)
    {
        uint next = unchecked(generation + 1);
        return next == 0 ? 1 : next;
    }

    private struct Entry
    {
        public uint Generation { get; set; }
        public bool Alive { get; set; }
        public UiSize Size { get; set; }
    }
}
