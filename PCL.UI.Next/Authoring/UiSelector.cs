// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Compiled selector with an explicit dependency set.
/// Readers must only observe listed slices — production pages should use
/// source-generated selectors so the dependency table matches the reader body.
/// </summary>
public readonly struct UiSelector<T>
{
    private readonly int[] _dependencies;

    /// <summary>Single-dependency convenience constructor.</summary>
    public UiSelector(int id, int dependencySlice, Func<PresentationStore, T> read)
        : this(id, [dependencySlice], read)
    {
    }

    /// <summary>
    /// Multi-dependency constructor. <paramref name="dependencySlices"/> is copied;
    /// the reader must only read those slices for correct invalidation.
    /// </summary>
    public UiSelector(int id, ReadOnlySpan<int> dependencySlices, Func<PresentationStore, T> read)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (dependencySlices.IsEmpty)
            throw new ArgumentException("Selector requires at least one dependency slice.", nameof(dependencySlices));
        ArgumentNullException.ThrowIfNull(read);

        for (int i = 0; i < dependencySlices.Length; i++)
        {
            if (dependencySlices[i] <= 0)
                throw new ArgumentOutOfRangeException(nameof(dependencySlices), "Slice ids must be positive.");
        }

        Id = id;
        _dependencies = dependencySlices.ToArray();
        Read = read;
    }

    public int Id { get; }

    /// <summary>First dependency (compat / single-slice cases).</summary>
    public int DependencySlice => _dependencies[0];

    public ReadOnlySpan<int> DependencySlices => _dependencies;

    public Func<PresentationStore, T> Read { get; }

    public T Evaluate(PresentationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Read(store);
    }
}

/// <summary>
/// Test / prototype helpers. Prefer generated selectors in production so
/// dependency sets cannot drift from reader bodies.
/// </summary>
public static class UiSelectors
{
    public static UiSelector<T> Create<T>(int id, int dependencySlice, Func<PresentationStore, T> read) =>
        new(id, dependencySlice, read);

    public static UiSelector<T> Create<T>(int id, ReadOnlySpan<int> dependencySlices, Func<PresentationStore, T> read) =>
        new(id, dependencySlices, read);

    public static UiSelector<string> String(int id, int dependencySlice, Func<PresentationStore, string> read) =>
        new(id, dependencySlice, read);

    public static UiSelector<string> String(int id, ReadOnlySpan<int> dependencySlices, Func<PresentationStore, string> read) =>
        new(id, dependencySlices, read);

    public static UiSelector<bool> Bool(int id, int dependencySlice, Func<PresentationStore, bool> read) =>
        new(id, dependencySlice, read);

    public static UiSelector<bool> Bool(int id, ReadOnlySpan<int> dependencySlices, Func<PresentationStore, bool> read) =>
        new(id, dependencySlices, read);
}
