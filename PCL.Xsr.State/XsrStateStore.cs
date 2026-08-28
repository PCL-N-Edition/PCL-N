namespace PCL.Xsr.State;

/// <summary>
/// The immutable-topology, revisioned state store. Reads pull coherent applied state; writers
/// assign the next revision per entry. Derived entries recompute only after an input revision
/// changes, and availability is carried separately from the last value.
/// </summary>
public sealed class XsrStateStore
{
    private readonly XsrRegistrySnapshot<XsrStateDescriptor> _registry;
    private readonly XsrStateNode[] _nodes;
    private readonly IXsrStateObserver? _observer;
    private long _changeStamp;

    private readonly Dictionary<XsrStateId, List<XsrStateId>> _derivedDependents;

    internal XsrStateStore(
        XsrRegistrySnapshot<XsrStateDescriptor> registry,
        XsrStateNode[] nodes,
        IXsrStateObserver? observer)
    {
        _registry = registry;
        _nodes = nodes;
        _observer = observer;

        _derivedDependents = [];
        foreach (XsrRegistryEntry<XsrStateDescriptor> entry in registry.Entries)
        {
            if (nodes[entry.RuntimeId.Value - 1] is IXsrStateDerivedNode derived)
            {
                foreach (XsrStateId dependency in derived.DependencyIds)
                {
                    if (!_derivedDependents.TryGetValue(dependency, out List<XsrStateId>? dependents))
                    {
                        dependents = [];
                        _derivedDependents[dependency] = dependents;
                    }

                    dependents.Add(new XsrStateId(entry.RuntimeId));
                }
            }
        }
    }

    public int Count => _registry.Count;

    public bool TryResolve(XsrSemanticId semanticId, out XsrStateId stateId)
    {
        if (_registry.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId))
        {
            stateId = new XsrStateId(runtimeId);
            return true;
        }

        stateId = default;
        return false;
    }

    /// <summary>
    /// Resolves a declared semantic identifier to its state identifier, rejecting undeclared input.
    /// </summary>
    public XsrStateId Resolve(XsrSemanticId semanticId) =>
        TryResolve(semanticId, out XsrStateId stateId)
            ? stateId
            : throw new InvalidOperationException($"The XSR state '{semanticId}' is not registered.");

    public XsrStateDescriptor Describe(XsrStateId stateId) => RequireNode(stateId).Descriptor;

    /// <summary>
    /// Allocates the next store-global change stamp. Stamps are strictly monotonic, so any
    /// applied mutation raises the stamp of exactly the entry it mutated.
    /// </summary>
    internal long NextChangeStamp() => Interlocked.Increment(ref _changeStamp);

    /// <summary>
    /// Reads one typed cell, applying any deferred coalesced publication first.
    /// </summary>
    public XsrStateValue<TValue> Read<TValue>(XsrStateId stateId, CancellationToken cancellationToken = default)
    {
        XsrStateNode node = RequireNode(stateId);

        if (node is XsrStateCellNode<TValue> cell)
        {
            XsrStateValue<TValue> value = cell.Read(
                stateId,
                NextChangeStamp(),
                cancellationToken,
                out XsrStateChange? flushed);
            Notify(flushed);
            return value;
        }

        if (node is XsrStateDerivedNode<TValue> derived)
        {
            XsrStateValue<TValue> value = derived.Read(this, stateId, cancellationToken, out XsrStateChange? change);
            Notify(change);
            return value;
        }

        throw MismatchedContract(stateId, node, typeof(TValue));
    }

    /// <summary>
    /// Publishes one typed cell value immediately, assigning the next revision. Any deferred
    /// coalesced publication is applied first, in publication order.
    /// </summary>
    public long Publish<TValue>(XsrStateId stateId, TValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        XsrStateNode node = RequireNode(stateId);

        if (node is not XsrStateCellNode<TValue> cell)
        {
            throw MismatchedContract(stateId, node, typeof(TValue));
        }

        XsrStateChange change = cell.Publish(
            stateId,
            value,
            NextChangeStamp(),
            NextChangeStamp(),
            out XsrStateChange? flushed);
        Notify(flushed);
        Notify(change);
        return change.Revision;
    }

    /// <summary>
    /// Publishes one replaceable cell value with latest-wins coalescing. The value becomes visible
    /// with one revision at the next read or snapshot capture; replaced intermediate publications
    /// are counted in <see cref="CoalescedCount"/>.
    /// </summary>
    public void PublishCoalesced<TValue>(XsrStateId stateId, TValue value)
    {
        XsrStateNode node = RequireNode(stateId);

        if (node is not XsrStateCellNode<TValue> cell)
        {
            throw MismatchedContract(stateId, node, typeof(TValue));
        }

        cell.PublishCoalesced(value);
        Notify(new XsrStateChange(
            stateId,
            node.SemanticId,
            XsrStateKind.Cell,
            node.Revision,
            XsrStateAvailability.Unavailable,
            XsrStateChangeReason.CoalescedPublished));
    }

    /// <summary>
    /// Reports how many coalesced publications were replaced before they became a revision.
    /// </summary>
    public long CoalescedCount(XsrStateId stateId) => RequireNode(stateId).CoalescedCount;

    /// <summary>
    /// Reads the applied value of one entry boxed. Cells flush deferred coalesced publications;
    /// derived entries recompute when their dependencies changed. Typed hot paths use
    /// <see cref="Read{TValue}"/>.
    /// </summary>
    public object? ReadAppliedValue(XsrStateId stateId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        XsrStateNode node = RequireNode(stateId);

        if (node is IXsrStateCellNode cell)
        {
            object? value = cell.ReadApplied(stateId, NextChangeStamp(), out XsrStateChange? flushed);
            Notify(flushed);
            return value;
        }

        if (node is IXsrStateDerivedNode derived)
        {
            object? value = derived.ReadAppliedObject(this, stateId, cancellationToken, out XsrStateChange? change);
            Notify(change);
            return value;
        }

        throw MismatchedContract(stateId, node, typeof(object));
    }

    /// <summary>
    /// Resolves every state entry whose applied value can change when one entry changes: the
    /// entry itself plus all derived entries that transitively depend on it.
    /// </summary>
    public IReadOnlyList<XsrStateId> AffectedBy(XsrStateId changed)
    {
        if (!_derivedDependents.TryGetValue(changed, out List<XsrStateId>? direct))
        {
            return [changed];
        }

        List<XsrStateId> affected = [changed];
        HashSet<XsrStateId> visited = [changed];
        for (int index = 0; index < affected.Count; index++)
        {
            if (_derivedDependents.TryGetValue(affected[index], out List<XsrStateId>? next))
            {
                foreach (XsrStateId dependent in next)
                {
                    if (visited.Add(dependent))
                    {
                        affected.Add(dependent);
                    }
                }
            }
        }

        return affected;
    }

    /// <summary>
    /// Reads one ordered collection snapshot. The returned items never change after capture.
    /// </summary>
    public XsrCollectionSnapshot<TItem> ReadCollection<TItem>(
        XsrStateId stateId,
        CancellationToken cancellationToken = default)
    {
        XsrStateNode node = RequireNode(stateId);

        if (node is not IXsrStateCollectionNode collection)
        {
            throw MismatchedContract(stateId, node, typeof(TItem));
        }

        return collection.ReadAs<TItem>(stateId, cancellationToken);
    }

    /// <summary>
    /// Applies one collection delta. When the delta base revision no longer matches, the store
    /// rejects the delta without mutation and the caller refreshes a snapshot.
    /// </summary>
    public XsrCollectionApplyResult PublishDelta<TItem, TKey>(
        XsrStateId stateId,
        XsrCollectionDelta<TItem, TKey> delta,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(delta);
        cancellationToken.ThrowIfCancellationRequested();
        XsrStateNode node = RequireNode(stateId);

        if (node is not XsrStateCollectionNode<TItem, TKey> collection)
        {
            throw MismatchedContract(stateId, node, typeof(TItem));
        }

        XsrCollectionApplyResult result = collection.PublishDelta(
            stateId,
            delta,
            NextChangeStamp(),
            out XsrStateChange? change);
        Notify(change);
        return result;
    }

    /// <summary>
    /// Marks entry availability without touching its value. Remote outages mark mirrors stale or
    /// unavailable while retaining the last value.
    /// </summary>
    public bool MarkAvailability(XsrStateId stateId, XsrStateAvailability availability)
    {
        XsrStateNode node = RequireNode(stateId);

        if (node is IXsrStateDerivedNode)
        {
            throw new InvalidOperationException(
                $"Derived state '{stateId}' derives availability from its dependencies.");
        }

        bool changed = node.SetAvailability(availability, NextChangeStamp(), out XsrStateChange? change);
        Notify(change);
        return changed;
    }

    /// <summary>
    /// Captures one whole-store snapshot. Deferred coalesced publications are applied first, and
    /// entries are ordered by runtime ID.
    /// </summary>
    public XsrStateSnapshot CaptureSnapshot(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        XsrStateSnapshotEntry[] entries = new XsrStateSnapshotEntry[_nodes.Length];
        for (int index = 0; index < _nodes.Length; index++)
        {
            XsrStateId stateId = new(_nodes[index].RuntimeId);
            FlushNode(_nodes[index], stateId);
            entries[index] = _nodes[index].Capture(stateId);
        }

        return new XsrStateSnapshot(entries);
    }

    /// <summary>
    /// Resolves the dependency stamp of one entry: the store-global change stamp of its last
    /// applied mutation, or for derived entries the newest stamp reachable through their declared
    /// dependencies. Per-entry revisions cannot serve this purpose because they are local
    /// counters; stamps are globally monotonic, so any dependency mutation raises this value.
    /// </summary>
    internal long ChangeStampOf(XsrStateId stateId)
    {
        XsrStateNode node = RequireNode(stateId);

        if (node is not IXsrStateDerivedNode derived)
        {
            return node.ChangeStamp;
        }

        long watermark = 0;
        foreach (XsrStateId dependency in derived.DependencyIds)
        {
            long stamp = ChangeStampOf(dependency);
            if (stamp > watermark)
            {
                watermark = stamp;
            }
        }

        return watermark;
    }

    internal void FlushNode(XsrStateNode node, XsrStateId stateId)
    {
        if (node is IXsrStateDerivedNode derived)
        {
            foreach (XsrStateId dependency in derived.DependencyIds)
            {
                FlushNode(RequireNode(dependency), dependency);
            }

            return;
        }

        node.ApplyPending(stateId, NextChangeStamp(), out XsrStateChange? flushed);
        Notify(flushed);
    }

    private XsrStateNode RequireNode(XsrStateId stateId)
    {
        if (!stateId.IsAssigned || stateId.Value.Value > (uint)_nodes.Length)
        {
            throw new ArgumentException($"The XSR state identifier '{stateId}' is not registered.", nameof(stateId));
        }

        return _nodes[(int)stateId.Value.Value - 1];
    }

    private static InvalidOperationException MismatchedContract(
        XsrStateId stateId,
        XsrStateNode node,
        Type requested) =>
        new(
            $"State '{node.SemanticId}' ({stateId}) is a {node.Kind} owned by '{node.Descriptor.Owner}' "
            + $"and does not match the requested contract '{requested.Name}'.");

    private void Notify(XsrStateChange? change)
    {
        if (change is not { } observed || _observer is null)
        {
            return;
        }

        try
        {
            _observer.OnChanged(observed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // State publication must not be changed by a diagnostics observer failure.
        }
    }
}
