using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next;

/// <summary>
/// Bridges applied state changes into renderer dirt across the thread boundary. State publishers
/// run on arbitrary threads; the tree is render-thread owned. The bridge therefore only enqueues
/// changed state IDs (coalescing duplicates) on the publisher thread; the render thread drains
/// the queue at frame start, resolves every affected entry — including derived entries that
/// transitively depend on a changed one — and marks the bound entities dirty.
/// </summary>
public sealed class XsrUiStateBridge : IXsrStateObserver
{
    private readonly XsrUiTree _tree;
    private readonly object _gate = new();
    private readonly HashSet<XsrStateId> _pending = [];

    public XsrUiStateBridge(XsrUiTree tree)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
    }

    /// <summary>
    /// Gets the number of state entries waiting to be drained. Thread-safe.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Observes one applied state change. Safe on any thread; never touches the tree.
    /// </summary>
    public void OnChanged(XsrStateChange change)
    {
        if (!change.Id.IsAssigned)
        {
            return;
        }

        lock (_gate)
        {
            _pending.Add(change.Id);
        }
    }

    /// <summary>
    /// Drains the pending queue on the render thread and marks every bound entity dirty. Call
    /// once per frame before rendering.
    /// </summary>
    public void DrainAndMark(XsrStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        XsrStateId[] pending;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (XsrStateId changed in pending)
        {
            foreach (XsrStateId affected in store.AffectedBy(changed))
            {
                _tree.MarkStateDirty(affected);
            }
        }
    }
}

/// <summary>
/// Receives renderer intent. UI.Next reads state and emits intent; it never resolves concrete
/// services or calls a Sidecar. The composition root bridges emitted commands into the XSR
/// command router.
/// </summary>
public interface IXsrUiIntentSink
{
    /// <summary>
    /// Emits one command intent produced by renderer input or activation.
    /// </summary>
    void Emit(XsrSemanticId command, XsrUiEntityId source, XsrCorrelationId correlationId);
}

/// <summary>
/// Collects intents in memory. Used by tests and by composition roots that drain intent queues
/// on their own scheduling; never by the renderer internals.
/// </summary>
public sealed class XsrUiIntentBuffer : IXsrUiIntentSink
{
    private readonly object _gate = new();
    private readonly List<(XsrSemanticId Command, XsrUiEntityId Source, XsrCorrelationId CorrelationId)> _intents = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _intents.Count;
            }
        }
    }

    public void Emit(XsrSemanticId command, XsrUiEntityId source, XsrCorrelationId correlationId)
    {
        lock (_gate)
        {
            _intents.Add((command, source, correlationId));
        }
    }

    public (XsrSemanticId Command, XsrUiEntityId Source, XsrCorrelationId CorrelationId)[] Drain()
    {
        lock (_gate)
        {
            (XsrSemanticId, XsrUiEntityId, XsrCorrelationId)[] drained = [.. _intents];
            _intents.Clear();
            return drained;
        }
    }
}
