// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Versioned presentation-state bag for Phase 2 bindings.
/// Business services must not be stored here — only UI-facing slices.
/// </summary>
public sealed class PresentationStore
{
    private readonly Dictionary<int, Slice> _slices = new();

    public void Set<T>(int sliceId, T value)
    {
        if (sliceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceId));

        if (_slices.TryGetValue(sliceId, out Slice existing))
        {
            _slices[sliceId] = new Slice(value, existing.Version + 1);
            return;
        }

        _slices[sliceId] = new Slice(value, 1);
    }

    public T Get<T>(int sliceId)
    {
        if (!_slices.TryGetValue(sliceId, out Slice slice))
            throw new KeyNotFoundException("Presentation slice not found: " + sliceId);
        if (slice.Value is T typed)
            return typed;
        if (slice.Value is null)
            return default!;
        throw new InvalidCastException(
            $"Slice {sliceId} is {slice.Value.GetType().Name}, not {typeof(T).Name}.");
    }

    public bool TryGet<T>(int sliceId, out T value)
    {
        if (_slices.TryGetValue(sliceId, out Slice slice) && slice.Value is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    public ulong Version(int sliceId) =>
        _slices.TryGetValue(sliceId, out Slice slice) ? slice.Version : 0;

    private readonly struct Slice(object? value, ulong version)
    {
        public object? Value { get; } = value;
        public ulong Version { get; } = version;
    }
}
