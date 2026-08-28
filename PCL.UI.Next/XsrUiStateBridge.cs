using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next;

/// <summary>
/// Receives applied state changes from the XSR state store and marks the entities bound to the
/// changed entry dirty. The bridge observes; it never mutates state back.
/// </summary>
public sealed class XsrUiStateBridge(XsrUiTree tree) : IXsrStateObserver
{
    public void OnChanged(XsrStateChange change)
    {
        foreach (XsrUiEntityId entity in tree.StateDependents(change.Id))
        {
            tree.MarkDirty(entity, XsrUiDirtyKinds.State);
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
