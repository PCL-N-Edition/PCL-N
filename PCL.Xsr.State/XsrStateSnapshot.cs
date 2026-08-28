using System.Collections.ObjectModel;

namespace PCL.Xsr.State;

/// <summary>
/// One immutable whole-store read view. Entries are ordered by runtime ID, so captures from the
/// same topology are directly comparable and replayable.
/// </summary>
public sealed class XsrStateSnapshot
{
    private readonly ReadOnlyCollection<XsrStateSnapshotEntry> _entries;

    internal XsrStateSnapshot(XsrStateSnapshotEntry[] entries)
    {
        _entries = new ReadOnlyCollection<XsrStateSnapshotEntry>(entries);
    }

    public IReadOnlyList<XsrStateSnapshotEntry> Entries => _entries;
}

/// <summary>
/// One state entry inside a snapshot. The value is boxed because a snapshot spans every value
/// contract; typed hot paths read cells directly instead.
/// </summary>
public readonly record struct XsrStateSnapshotEntry(
    XsrStateId Id,
    XsrSemanticId SemanticId,
    XsrStateKind Kind,
    string Owner,
    long Revision,
    XsrStateAvailability Availability,
    object? Value);
