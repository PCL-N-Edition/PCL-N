using System.Collections.ObjectModel;

namespace PCL.Xsr;

/// <summary>
/// Provides an immutable, concurrently readable view of a sealed XSR registry.
/// </summary>
public sealed class XsrRegistrySnapshot<TDescriptor>
    where TDescriptor : notnull
{
    private readonly XsrRegistryEntry<TDescriptor>[] _entries;
    private readonly ReadOnlyCollection<XsrRegistryEntry<TDescriptor>> _readOnlyEntries;
    private readonly Dictionary<XsrSemanticId, XsrRuntimeId> _runtimeIdsBySemanticId;

    internal XsrRegistrySnapshot(XsrRegistryEntry<TDescriptor>[] entries)
    {
        _entries = entries;
        _readOnlyEntries = Array.AsReadOnly(entries);
        _runtimeIdsBySemanticId = new Dictionary<XsrSemanticId, XsrRuntimeId>(entries.Length);

        foreach (XsrRegistryEntry<TDescriptor> entry in entries)
        {
            _runtimeIdsBySemanticId.Add(entry.SemanticId, entry.RuntimeId);
        }
    }

    /// <summary>
    /// Gets the registrations in ascending runtime-ID order.
    /// </summary>
    public IReadOnlyList<XsrRegistryEntry<TDescriptor>> Entries => _readOnlyEntries;

    /// <summary>
    /// Gets the number of registrations in this snapshot.
    /// </summary>
    public int Count => _entries.Length;

    /// <summary>
    /// Resolves an entry through its numeric hot-path identifier.
    /// </summary>
    public bool TryGet(XsrRuntimeId runtimeId, out XsrRegistryEntry<TDescriptor> entry)
    {
        if (!runtimeId.IsAssigned || runtimeId.Value > _entries.Length)
        {
            entry = default;
            return false;
        }

        entry = _entries[checked((int)runtimeId.Value - 1)];
        return true;
    }

    /// <summary>
    /// Resolves an entry through its development identifier outside the hot path.
    /// </summary>
    public bool TryGet(XsrSemanticId semanticId, out XsrRegistryEntry<TDescriptor> entry)
    {
        if (!_runtimeIdsBySemanticId.TryGetValue(semanticId, out XsrRuntimeId runtimeId))
        {
            entry = default;
            return false;
        }

        return TryGet(runtimeId, out entry);
    }

    /// <summary>
    /// Resolves the compact runtime identifier assigned to a semantic identifier.
    /// </summary>
    public bool TryGetRuntimeId(XsrSemanticId semanticId, out XsrRuntimeId runtimeId) =>
        _runtimeIdsBySemanticId.TryGetValue(semanticId, out runtimeId);
}
