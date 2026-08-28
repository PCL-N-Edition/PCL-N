namespace PCL.Xsr.State;

/// <summary>
/// Shared revisioned machinery for one state entry. All mutation happens under the entry gate;
/// revisions use volatile reads so dependency watermarks observe the newest applied change.
/// </summary>
internal abstract class XsrStateNode
{
    private readonly object _gate = new();
    private long _revision;
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

    protected long CurrentRevisionLocked => _revision;

    protected XsrStateAvailability AvailabilityLocked => _availability;

    protected void AdvanceLocked(XsrStateAvailability availability)
    {
        _availability = availability;
        _revision++;
    }

    /// <summary>
    /// Applies any deferred coalesced publication. Only cells can carry deferred work.
    /// </summary>
    public virtual void ApplyPending(XsrStateId id, out XsrStateChange? flushed)
    {
        flushed = null;
    }

    /// <summary>
    /// Counts coalesced publications replaced before they became a revision. Only cells coalesce.
    /// </summary>
    public virtual long CoalescedCount => 0;

    public bool SetAvailability(XsrStateAvailability availability, out XsrStateChange? change)
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

            AdvanceLocked(availability);
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
/// Dispatches typed reads on collections when only the item contract is known at the call site.
/// </summary>
internal interface IXsrStateCollectionNode
{
    XsrCollectionSnapshot<TItem> ReadAs<TItem>(XsrStateId id, CancellationToken cancellationToken);
}

/// <summary>
/// Exposes declared dependencies so the store can flush deferred publications before watermarking.
/// </summary>
internal interface IXsrStateDerivedNode
{
    IReadOnlyList<XsrStateId> DependencyIds { get; }
}

internal sealed class XsrStateCellNode<TValue> : XsrStateNode
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

    public XsrStateValue<TValue> Read(XsrStateId id, CancellationToken cancellationToken, out XsrStateChange? flushed)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (Gate)
        {
            flushed = FlushPendingLocked(id);
            return new XsrStateValue<TValue>(
                id,
                CurrentRevisionLocked,
                AvailabilityLocked,
                _hasValue,
                _value!);
        }
    }

    public XsrStateChange Publish(XsrStateId id, TValue value, out XsrStateChange? flushed)
    {
        lock (Gate)
        {
            flushed = FlushPendingLocked(id);
            _value = value;
            _hasValue = true;
            AdvanceLocked(XsrStateAvailability.Available);
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

    public override void ApplyPending(XsrStateId id, out XsrStateChange? flushed)
    {
        lock (Gate)
        {
            flushed = FlushPendingLocked(id);
        }
    }

    private XsrStateChange? FlushPendingLocked(XsrStateId id)
    {
        if (!_hasPending)
        {
            return null;
        }

        _hasPending = false;
        _value = _pendingValue;
        _hasValue = true;
        AdvanceLocked(XsrStateAvailability.Available);
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
            AdvanceLocked(XsrStateAvailability.Available);
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
    private readonly XsrStateId[] _dependencies;
    private readonly XsrDerivedCompute<TValue> _compute;
    private TValue? _value;
    private bool _hasValue;
    private bool _computed;
    private long _watermark;

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

    public XsrStateValue<TValue> Read(
        XsrStateStore store,
        XsrStateId id,
        CancellationToken cancellationToken,
        out XsrStateChange? change)
    {
        cancellationToken.ThrowIfCancellationRequested();

        store.FlushNode(this, id);
        long watermark = Watermark(store);

        lock (Gate)
        {
            if (_computed && _watermark == watermark)
            {
                change = null;
                return new XsrStateValue<TValue>(
                    id,
                    CurrentRevisionLocked,
                    XsrStateAvailability.Available,
                    true,
                    _value!);
            }
        }

        TValue computed = _compute(new XsrStateReader(store), cancellationToken);
        long observed = Watermark(store);

        lock (Gate)
        {
            if (!_computed || observed == watermark)
            {
                bool valueChanged = !_hasValue
                    || !EqualityComparer<TValue>.Default.Equals(_value, computed);

                _value = computed;
                _hasValue = true;
                _computed = true;
                _watermark = observed;

                if (valueChanged)
                {
                    AdvanceLocked(XsrStateAvailability.Available);
                    change = new XsrStateChange(
                        id,
                        SemanticId,
                        Kind,
                        CurrentRevisionLocked,
                        AvailabilityLocked,
                        XsrStateChangeReason.DerivedRecomputed);
                }
                else
                {
                    change = null;
                }
            }
            else
            {
                change = null;
            }

            return new XsrStateValue<TValue>(
                id,
                CurrentRevisionLocked,
                _computed ? XsrStateAvailability.Available : XsrStateAvailability.Unavailable,
                _hasValue,
                _value!);
        }
    }

    private long Watermark(XsrStateStore store)
    {
        long watermark = 0;
        foreach (XsrStateId dependency in _dependencies)
        {
            long revision = store.EffectiveRevision(dependency);
            if (revision > watermark)
            {
                watermark = revision;
            }
        }

        return watermark;
    }

    protected override object? CaptureValueLocked() => _hasValue ? _value : null;
}
