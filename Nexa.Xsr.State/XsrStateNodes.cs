namespace Nexa.Xsr.State;

/// <summary>
/// Shared revisioned machinery for one state entry. All mutation happens under the entry gate.
/// Each entry carries its own <see cref="Revision"/> (its own version, used by deltas and
/// snapshots) plus the store-global <see cref="ChangeStamp"/> of its last applied mutation
/// (diagnostic allocation order, not a dependency invalidation watermark).
/// </summary>
internal abstract class XsrStateNode
{
    private readonly object _gate = new();
    private long _revision;
    private long _changeStamp;
    private XsrStateAvailability _availability = XsrStateAvailability.Unavailable;

    protected XsrStateNode(XsrSemanticId semanticId, XsrRuntimeId runtimeId, XsrStateDescriptor descriptor)
    {
        SemanticId = semanticId;
        RuntimeId = runtimeId;
        Descriptor = descriptor;
    }

    public XsrSemanticId SemanticId { get; }

    public XsrRuntimeId RuntimeId { get; }

    public XsrStateDescriptor Descriptor { get; }

    public XsrStateKind Kind => Descriptor.Kind;

    protected object Gate => _gate;

    public long Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// Gets the store-global change stamp of this entry's last applied mutation. Stamps are
    /// allocated globally; concurrent publishers can commit out of allocation order.
    /// </summary>
    public long ChangeStamp => Volatile.Read(ref _changeStamp);

    protected long CurrentRevisionLocked => _revision;

    protected XsrStateAvailability AvailabilityLocked => _availability;

    protected void AdvanceLocked(long changeStamp, XsrStateAvailability availability)
    {
        _availability = availability;
        _revision++;
        _changeStamp = changeStamp;
    }

    /// <summary>
    /// Applies any deferred coalesced publication. Only cells can carry deferred work.
    /// </summary>
    public virtual void ApplyPending(XsrStateId id, long changeStamp, out XsrStateChange? flushed)
    {
        _ = changeStamp;
        flushed = null;
    }

    /// <summary>
    /// Counts coalesced publications replaced before they became a revision. Only cells coalesce.
    /// </summary>
    public virtual long CoalescedCount => 0;

    public bool SetAvailability(XsrStateAvailability availability, long changeStamp, out XsrStateChange? change)
    {
        if (!Enum.IsDefined(availability))
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        lock (Gate)
        {
            if (_availability == availability)
            {
                change = null;
                return false;
            }

            AdvanceLocked(changeStamp, availability);
            change = new XsrStateChange(
                new XsrStateId(RuntimeId),
                SemanticId,
                Kind,
                _revision,
                _availability,
                XsrStateChangeReason.AvailabilityChanged);
            return true;
        }
    }

    public XsrStateSnapshotEntry Capture(XsrStateId id)
    {
        lock (Gate)
        {
            return new XsrStateSnapshotEntry(
                id,
                SemanticId,
                Kind,
                Descriptor.Owner,
                _revision,
                _availability,
                CaptureValueLocked());
        }
    }

    protected abstract object? CaptureValueLocked();
}

/// <summary>
/// Dispatches non-generic applied reads on cells for consumers that cannot know the value
/// contract at compile time.
/// </summary>
internal interface IXsrStateCellNode
{
    object? ReadApplied(XsrStateId id, long flushStamp, out XsrStateChange? flushed);
}

/// <summary>
/// Dispatches typed reads on collections when only the item contract is known at the call site.
/// </summary>
internal interface IXsrStateCollectionNode
{
    XsrCollectionSnapshot<TItem> ReadAs<TItem>(XsrStateId id, CancellationToken cancellationToken);
}

/// <summary>
/// Exposes declared dependencies so the store can flush deferred publications before watermarking,
/// plus a non-generic applied read for consumers that cannot know the value contract.
/// </summary>
internal interface IXsrStateDerivedNode
{
    IReadOnlyList<XsrStateId> DependencyIds { get; }

    object? ReadAppliedObject(XsrStateStore store, XsrStateId id, CancellationToken cancellationToken, out XsrStateChange? change);
}

internal sealed class XsrStateCellNode<TValue> : XsrStateNode, IXsrStateCellNode
{
    private TValue? _value;
    private bool _hasValue;
    private bool _hasPending;
    private TValue? _pendingValue;
    private long _coalescedCount;

    internal XsrStateCellNode(XsrSemanticId semanticId, XsrRuntimeId runtimeId, XsrStateDescriptor descriptor)
        : base(semanticId, runtimeId, descriptor)
    {
    }

    /// <summary>
    /// Counts coalesced publications that were replaced before they became a revision.
    /// </summary>
    public override long CoalescedCount => Volatile.Read(ref _coalescedCount);

    public XsrStateValue<TValue> Read(
        XsrStateId id,
        long flushStamp,
        CancellationToken cancellationToken,
        out XsrStateChange? flushed)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (Gate)
        {
            flushed = FlushPendingLocked(id, flushStamp);
            return new XsrStateValue<TValue>(
                id,
                CurrentRevisionLocked,
                AvailabilityLocked,
                _hasValue,
                _value!);
        }
    }

    public XsrStateChange Publish(
        XsrStateId id,
        TValue value,
        long flushStamp,
        long publishStamp,
        out XsrStateChange? flushed)
    {
        lock (Gate)
        {
            flushed = FlushPendingLocked(id, flushStamp);
            _value = value;
            _hasValue = true;
            AdvanceLocked(publishStamp, XsrStateAvailability.Available);
            return new XsrStateChange(
                id,
                SemanticId,
                Kind,
                CurrentRevisionLocked,
                AvailabilityLocked,
                XsrStateChangeReason.ValuePublished);
        }
    }

    public void PublishCoalesced(TValue value)
    {
        lock (Gate)
        {
            if (_hasPending)
            {
                _coalescedCount++;
            }

            _pendingValue = value;
            _hasPending = true;
        }
    }

    public override void ApplyPending(XsrStateId id, long changeStamp, out XsrStateChange? flushed)
    {
        lock (Gate)
        {
            flushed = FlushPendingLocked(id, changeStamp);
        }
    }

    /// <summary>
    /// Reads the applied value, flushing any deferred coalesced publication first.
    /// </summary>
    public object? ReadApplied(XsrStateId id, long flushStamp, out XsrStateChange? flushed)
    {
        lock (Gate)
        {
            flushed = FlushPendingLocked(id, flushStamp);
            return _hasValue ? _value : null;
        }
    }

    private XsrStateChange? FlushPendingLocked(XsrStateId id, long changeStamp)
    {
        if (!_hasPending)
        {
            return null;
        }

        _hasPending = false;
        _value = _pendingValue;
        _hasValue = true;
        AdvanceLocked(changeStamp, XsrStateAvailability.Available);
        return new XsrStateChange(
            id,
            SemanticId,
            Kind,
            CurrentRevisionLocked,
            AvailabilityLocked,
            XsrStateChangeReason.CoalescedApplied);
    }

    protected override object? CaptureValueLocked() => _hasValue ? _value : null;
}

internal sealed class XsrStateCollectionNode<TItem, TKey> : XsrStateNode, IXsrStateCollectionNode
    where TKey : notnull
{
    private readonly Func<TItem, TKey> _keySelector;
    private readonly IComparer<TKey> _comparer;
    private TItem[] _items = [];

    internal XsrStateCollectionNode(
        XsrSemanticId semanticId,
        XsrRuntimeId runtimeId,
        XsrStateDescriptor descriptor,
        Func<TItem, TKey> keySelector,
        IComparer<TKey> comparer)
        : base(semanticId, runtimeId, descriptor)
    {
        _keySelector = keySelector;
        _comparer = comparer;
    }

    public XsrCollectionApplyResult PublishDelta(
        XsrStateId id,
        XsrCollectionDelta<TItem, TKey> delta,
        long changeStamp,
        out XsrStateChange? change)
    {
        lock (Gate)
        {
            if (delta.BaseRevision != CurrentRevisionLocked)
            {
                change = null;
                return XsrCollectionApplyResult.Rejected(CurrentRevisionLocked);
            }

            Dictionary<TKey, TItem> merged = [];
            foreach (TItem item in _items)
            {
                merged[_keySelector(item)] = item;
            }

            foreach (TItem item in delta.Upserts)
            {
                merged[_keySelector(item)] = item;
            }

            foreach (TKey key in delta.Removals)
            {
                _ = merged.Remove(key);
            }

            TItem[] ordered = [.. merged.Values.OrderBy(_keySelector, _comparer)];
            _items = ordered;
            AdvanceLocked(changeStamp, XsrStateAvailability.Available);
            change = new XsrStateChange(
                id,
                SemanticId,
                Kind,
                CurrentRevisionLocked,
                AvailabilityLocked,
                XsrStateChangeReason.CollectionDeltaApplied);
            return XsrCollectionApplyResult.Applied(CurrentRevisionLocked);
        }
    }

    public XsrCollectionSnapshot<TItem> Read(XsrStateId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (Gate)
        {
            return new XsrCollectionSnapshot<TItem>(
                id,
                CurrentRevisionLocked,
                AvailabilityLocked,
                _items);
        }
    }

    public XsrCollectionSnapshot<TOther> ReadAs<TOther>(XsrStateId id, CancellationToken cancellationToken)
    {
        if (typeof(TOther) != typeof(TItem))
        {
            throw new InvalidOperationException(
                $"State '{id}' is a collection of '{typeof(TItem)}', not '{typeof(TOther)}'.");
        }

        return (XsrCollectionSnapshot<TOther>)(object)Read(id, cancellationToken);
    }

    protected override object? CaptureValueLocked() => _items;
}

internal sealed class XsrStateDerivedNode<TValue> : XsrStateNode, IXsrStateDerivedNode
{
    private const int MaxComputeAttempts = 4;

    private readonly XsrStateId[] _dependencies;
    private readonly XsrDerivedCompute<TValue> _compute;
    private TValue? _value;
    private bool _hasValue;
    private bool _computed;
    private (long Revision, XsrStateAvailability Availability)[] _versions = [];

    internal XsrStateDerivedNode(
        XsrSemanticId semanticId,
        XsrRuntimeId runtimeId,
        XsrStateDescriptor descriptor,
        XsrStateId[] dependencies,
        XsrDerivedCompute<TValue> compute)
        : base(semanticId, runtimeId, descriptor)
    {
        _dependencies = dependencies;
        _compute = compute;
    }

    public IReadOnlyList<XsrStateId> DependencyIds => _dependencies;

    public object? ReadAppliedObject(
        XsrStateStore store,
        XsrStateId id,
        CancellationToken cancellationToken,
        out XsrStateChange? change)
    {
        XsrStateValue<TValue> value = Read(store, id, cancellationToken, out change);
        return value.HasValue ? value.Value : null;
    }

    public XsrStateValue<TValue> Read(
        XsrStateStore store,
        XsrStateId id,
        CancellationToken cancellationToken,
        out XsrStateChange? change)
    {
        cancellationToken.ThrowIfCancellationRequested();

        store.FlushNode(this, id);

        for (int attempt = 1; ; attempt++)
        {
            var before = store.CaptureDependencies(_dependencies, cancellationToken);
            lock (Gate)
            {
                if (_computed && _versions.AsSpan().SequenceEqual(before))
                {
                    change = null;
                    return Snapshot(id);
                }
            }
            var availability = before.Any(item => item.Availability == XsrStateAvailability.Unavailable)
                ? XsrStateAvailability.Unavailable
                : before.Any(item => item.Availability == XsrStateAvailability.Stale)
                    ? XsrStateAvailability.Stale : XsrStateAvailability.Available;
            bool compute = availability == XsrStateAvailability.Available;
            TValue? computed = compute ? _compute(new XsrStateReader(store), cancellationToken) : default;
            var after = store.CaptureDependencies(_dependencies, cancellationToken);
            if (before.AsSpan().SequenceEqual(after))
            {
                lock (Gate)
                {
                    // Another reader may have committed a newer dependency window.
                    bool superseded = _computed && _versions.Where((item, index) => item.Revision > after[index].Revision).Any();
                    if (!superseded)
                    {
                        bool changed = AvailabilityLocked != availability
                            || (compute && (!_hasValue || !EqualityComparer<TValue>.Default.Equals(_value, computed)));
                        if (compute) { _value = computed; _hasValue = true; }
                        _computed = true;
                        _versions = after;
                        change = null;
                        if (changed)
                        {
                            AdvanceLocked(store.NextChangeStamp(), availability);
                            change = new(id, SemanticId, Kind, CurrentRevisionLocked, AvailabilityLocked,
                                XsrStateChangeReason.DerivedRecomputed);
                        }
                        return Snapshot(id);
                    }
                }
            }
            if (attempt >= MaxComputeAttempts)
            {
                lock (Gate)
                {
                    _computed = false;
                    var fallback = _hasValue ? XsrStateAvailability.Stale : XsrStateAvailability.Unavailable;
                    change = null;
                    if (AvailabilityLocked != fallback)
                    {
                        AdvanceLocked(store.NextChangeStamp(), fallback);
                        change = new(id, SemanticId, Kind, CurrentRevisionLocked, AvailabilityLocked,
                            XsrStateChangeReason.DerivedRecomputed);
                    }
                    return Snapshot(id);
                }
            }
        }
    }

    private XsrStateValue<TValue> Snapshot(XsrStateId id) =>
        new(id, CurrentRevisionLocked, AvailabilityLocked, _hasValue, _value!);

    protected override object? CaptureValueLocked() => _hasValue ? _value : null;
}
