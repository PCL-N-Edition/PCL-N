namespace PCL.Xsr.State;

/// <summary>
/// Reports the outcome of applying one collection delta against a known base revision.
/// </summary>
public readonly record struct XsrCollectionApplyResult(bool IsApplied, long Revision)
{
    internal static XsrCollectionApplyResult Applied(long revision) => new(true, revision);

    internal static XsrCollectionApplyResult Rejected(long revision) => new(false, revision);
}

/// <summary>
/// One ordered collection change from a known base revision. When the base revision no longer
/// matches, the receiver refreshes a snapshot instead of mutating best-effort.
/// </summary>
public sealed class XsrCollectionDelta<TItem, TKey>
    where TKey : notnull
{
    public XsrCollectionDelta(
        long baseRevision,
        IReadOnlyList<TItem> upserts,
        IReadOnlyList<TKey> removals)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);

        if (baseRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseRevision),
                "A collection delta base revision cannot be negative.");
        }

        BaseRevision = baseRevision;
        Upserts = upserts;
        Removals = removals;
    }

    public long BaseRevision { get; }

    public IReadOnlyList<TItem> Upserts { get; }

    public IReadOnlyList<TKey> Removals { get; }

    /// <summary>
    /// Applies this delta to an ordered base list. The result is applied only when the base
    /// revision still matches; otherwise the caller refreshes a snapshot.
    /// </summary>
    public XsrCollectionApplyResult TryApplyTo(
        IReadOnlyList<TItem> baseItems,
        long baseRevision,
        Func<TItem, TKey> keySelector,
        IComparer<TKey> comparer,
        out IReadOnlyList<TItem> applied)
    {
        ArgumentNullException.ThrowIfNull(baseItems);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(comparer);

        if (BaseRevision != baseRevision)
        {
            applied = baseItems;
            return XsrCollectionApplyResult.Rejected(baseRevision);
        }

        Dictionary<TKey, TItem> merged = [];
        foreach (TItem item in baseItems)
        {
            merged[keySelector(item)] = item;
        }

        foreach (TItem item in Upserts)
        {
            merged[keySelector(item)] = item;
        }

        foreach (TKey key in Removals)
        {
            _ = merged.Remove(key);
        }

        TItem[] ordered = [.. merged.Values.OrderBy(keySelector, comparer)];
        applied = ordered;
        return XsrCollectionApplyResult.Applied(BaseRevision + 1);
    }
}
