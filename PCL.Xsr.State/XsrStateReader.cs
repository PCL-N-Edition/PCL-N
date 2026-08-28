namespace PCL.Xsr.State;

/// <summary>
/// Computes one derived value from declared dependencies.
/// </summary>
public delegate TValue XsrDerivedCompute<TValue>(XsrStateReader reader, CancellationToken cancellationToken);

/// <summary>
/// Resolves typed state inside a derived computation. The reader sees dependencies at their
/// current applied revision and must not be retained after the computation returns.
/// </summary>
public sealed class XsrStateReader
{
    private readonly XsrStateStore _store;

    internal XsrStateReader(XsrStateStore store)
    {
        _store = store;
    }

    public bool TryResolve(XsrSemanticId semanticId, out XsrStateId stateId) =>
        _store.TryResolve(semanticId, out stateId);

    public XsrStateId Resolve(XsrSemanticId semanticId) => _store.Resolve(semanticId);

    public XsrStateValue<TValue> Read<TValue>(XsrStateId id, CancellationToken cancellationToken = default) =>
        _store.Read<TValue>(id, cancellationToken);

    public XsrCollectionSnapshot<TItem> ReadCollection<TItem>(
        XsrStateId id,
        CancellationToken cancellationToken = default) =>
        _store.ReadCollection<TItem>(id, cancellationToken);
}
