// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Compiled selector: depends on a presentation slice and reads a value without reflection.
/// Source generators can later emit the <see cref="Read"/> body as direct field access.
/// </summary>
public readonly struct UiSelector<T>
{
    public UiSelector(int id, int dependencySlice, Func<PresentationStore, T> read)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (dependencySlice <= 0)
            throw new ArgumentOutOfRangeException(nameof(dependencySlice));
        ArgumentNullException.ThrowIfNull(read);
        Id = id;
        DependencySlice = dependencySlice;
        Read = read;
    }

    public int Id { get; }
    public int DependencySlice { get; }
    public Func<PresentationStore, T> Read { get; }

    public T Evaluate(PresentationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Read(store);
    }
}

/// <summary>Helpers for building selectors in tests / presentation layer.</summary>
public static class UiSelectors
{
    public static UiSelector<T> Create<T>(int id, int dependencySlice, Func<PresentationStore, T> read) =>
        new(id, dependencySlice, read);

    public static UiSelector<string> String(int id, int dependencySlice, Func<PresentationStore, string> read) =>
        new(id, dependencySlice, read);

    public static UiSelector<bool> Bool(int id, int dependencySlice, Func<PresentationStore, bool> read) =>
        new(id, dependencySlice, read);
}
