using System.Collections.ObjectModel;

namespace PCL.Xsr.State;

/// <summary>
/// One immutable ordered collection view at a revision. The items never change after capture.
/// </summary>
public sealed class XsrCollectionSnapshot<TItem>
{
    private readonly ReadOnlyCollection<TItem> _items;

    internal XsrCollectionSnapshot(
        XsrStateId id,
        long revision,
        XsrStateAvailability availability,
        TItem[] items)
    {
        Id = id;
        Revision = revision;
        Availability = availability;
        _items = new ReadOnlyCollection<TItem>(items);
    }

    public XsrStateId Id { get; }

    public long Revision { get; }

    public XsrStateAvailability Availability { get; }

    public int Count => _items.Count;

    public IReadOnlyList<TItem> Items => _items;
}
