namespace PCL.Xsr.State;

internal abstract class XsrStateDeclaration
{
    protected XsrStateDeclaration(XsrSemanticId semanticId, XsrStateDescriptor descriptor)
    {
        SemanticId = semanticId;
        Descriptor = descriptor;
    }

    public XsrSemanticId SemanticId { get; }

    public XsrStateDescriptor Descriptor { get; }

    public abstract XsrStateNode CreateNode(XsrRuntimeId runtimeId);
}

internal sealed class XsrCellDeclaration<TValue> : XsrStateDeclaration
{
    public XsrCellDeclaration(XsrSemanticId semanticId, XsrStateDescriptor descriptor)
        : base(semanticId, descriptor)
    {
    }

    public override XsrStateNode CreateNode(XsrRuntimeId runtimeId) =>
        new XsrStateCellNode<TValue>(SemanticId, runtimeId, Descriptor);
}

internal sealed class XsrCollectionDeclaration<TItem, TKey> : XsrStateDeclaration
    where TKey : notnull
{
    public XsrCollectionDeclaration(
        XsrSemanticId semanticId,
        XsrStateDescriptor descriptor,
        Func<TItem, TKey> keySelector,
        IComparer<TKey> comparer)
        : base(semanticId, descriptor)
    {
        KeySelector = keySelector;
        Comparer = comparer;
    }

    public Func<TItem, TKey> KeySelector { get; }

    public IComparer<TKey> Comparer { get; }

    public override XsrStateNode CreateNode(XsrRuntimeId runtimeId) =>
        new XsrStateCollectionNode<TItem, TKey>(SemanticId, runtimeId, Descriptor, KeySelector, Comparer);
}

internal interface IXsrDerivedDeclarationInfo
{
    IReadOnlyList<XsrSemanticId> Dependencies { get; }

    XsrStateId[]? ResolvedDependencies { get; set; }
}

internal sealed class XsrDerivedDeclaration<TValue> : XsrStateDeclaration, IXsrDerivedDeclarationInfo
{
    public XsrDerivedDeclaration(
        XsrSemanticId semanticId,
        XsrStateDescriptor descriptor,
        IReadOnlyList<XsrSemanticId> dependencies,
        XsrDerivedCompute<TValue> compute)
        : base(semanticId, descriptor)
    {
        Dependencies = dependencies;
        Compute = compute;
    }

    public IReadOnlyList<XsrSemanticId> Dependencies { get; }

    public XsrDerivedCompute<TValue> Compute { get; }

    public override XsrStateNode CreateNode(XsrRuntimeId runtimeId) =>
        new XsrStateDerivedNode<TValue>(SemanticId, runtimeId, Descriptor, ResolvedDependencies!, Compute);

    public XsrStateId[]? ResolvedDependencies { get; set; }
}

/// <summary>
/// Collects state declarations during startup and builds an immutable, revisioned state store.
/// </summary>
public sealed class XsrStateStoreBuilder
{
    private readonly List<XsrStateDeclaration> _declarations = [];
    private readonly Dictionary<XsrSemanticId, XsrStateDeclaration> _declarationsById = new();
    private bool _built;

    /// <summary>
    /// Declares one typed value cell. The key identity of a collection uses the default equality
    /// of its key contract; ordering uses the provided comparer.
    /// </summary>
    public void Cell<TValue>(XsrSemanticId semanticId, string owner)
    {
        Register(new XsrCellDeclaration<TValue>(
            semanticId,
            new XsrStateDescriptor(XsrStateKind.Cell, owner)));
    }

    /// <summary>
    /// Declares one ordered collection. Item identity comes from <paramref name="keySelector"/>;
    /// item ordering comes from <paramref name="comparer"/> and is deterministic per build.
    /// </summary>
    public void Collection<TItem, TKey>(
        XsrSemanticId semanticId,
        string owner,
        Func<TItem, TKey> keySelector,
        IComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        Register(new XsrCollectionDeclaration<TItem, TKey>(
            semanticId,
            new XsrStateDescriptor(XsrStateKind.Collection, owner),
            keySelector,
            comparer ?? Comparer<TKey>.Default));
    }

    /// <summary>
    /// Declares one derived value computed from declared dependencies. The dependency graph must
    /// be acyclic; cycles are rejected when the store is built.
    /// </summary>
    public void Derived<TValue>(
        XsrSemanticId semanticId,
        string owner,
        IReadOnlyList<XsrSemanticId> dependencies,
        XsrDerivedCompute<TValue> compute)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(compute);

        if (dependencies.Count == 0)
        {
            throw new ArgumentException(
                $"Derived state '{semanticId}' must declare at least one dependency.",
                nameof(dependencies));
        }

        foreach (XsrSemanticId dependency in dependencies)
        {
            if (!dependency.IsAssigned)
            {
                throw new ArgumentException(
                    $"Derived state '{semanticId}' declared an unassigned dependency.",
                    nameof(dependencies));
            }
        }

        Register(new XsrDerivedDeclaration<TValue>(
            semanticId,
            new XsrStateDescriptor(XsrStateKind.Cell, owner),
            dependencies,
            compute));
    }

    /// <summary>
    /// Seals registration, assigns deterministic runtime IDs, validates the dependency graph, and
    /// returns the immutable store.
    /// </summary>
    public XsrStateStore Build(IXsrStateObserver? observer = null)
    {
        if (_built)
        {
            throw new InvalidOperationException("The XSR state store builder has already been built.");
        }

        XsrRegistry<XsrStateDescriptor> registry = new();
        foreach (XsrStateDeclaration declaration in _declarations)
        {
            registry.Register(declaration.SemanticId, declaration.Descriptor);
        }

        XsrRegistrySnapshot<XsrStateDescriptor> snapshot = registry.Seal();

        Dictionary<XsrSemanticId, XsrStateId> stateIds = new();
        foreach (XsrRegistryEntry<XsrStateDescriptor> entry in snapshot.Entries)
        {
            stateIds[entry.SemanticId] = new XsrStateId(entry.RuntimeId);
        }

        ValidateDependencies(stateIds);
        ResolveDerivedDependencies(stateIds);
        RejectDependencyCycles();

        XsrStateNode[] nodes = new XsrStateNode[snapshot.Count];
        foreach (XsrStateDeclaration declaration in _declarations)
        {
            XsrStateId stateId = stateIds[declaration.SemanticId];
            nodes[stateId.Value.Value - 1] = declaration.CreateNode(stateId.Value);
        }

        _built = true;
        _declarations.Clear();
        _declarationsById.Clear();
        return new XsrStateStore(snapshot, nodes, observer);
    }

    private void Register(XsrStateDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        if (_built)
        {
            throw new InvalidOperationException("The XSR state store builder has already been built.");
        }

        if (_declarationsById.ContainsKey(declaration.SemanticId))
        {
            throw new InvalidOperationException(
                $"The XSR state semantic identifier '{declaration.SemanticId}' is already declared.");
        }

        _declarationsById[declaration.SemanticId] = declaration;
        _declarations.Add(declaration);
    }

    private void ValidateDependencies(Dictionary<XsrSemanticId, XsrStateId> stateIds)
    {
        foreach (XsrStateDeclaration declaration in _declarations)
        {
            if (declaration is not IXsrDerivedDeclarationInfo derived)
            {
                continue;
            }

            foreach (XsrSemanticId dependency in derived.Dependencies)
            {
                if (!stateIds.ContainsKey(dependency))
                {
                    throw new InvalidOperationException(
                        $"Derived state '{declaration.SemanticId}' depends on undeclared state '{dependency}'.");
                }
            }
        }
    }

    private void ResolveDerivedDependencies(Dictionary<XsrSemanticId, XsrStateId> stateIds)
    {
        foreach (XsrStateDeclaration declaration in _declarations)
        {
            if (declaration is not IXsrDerivedDeclarationInfo derived)
            {
                continue;
            }

            derived.ResolvedDependencies = [.. derived.Dependencies.Select(dependency => stateIds[dependency])];
        }
    }

    private void RejectDependencyCycles()
    {
        Dictionary<XsrSemanticId, List<XsrSemanticId>> derivedEdges = [];
        foreach (XsrStateDeclaration declaration in _declarations)
        {
            if (declaration is IXsrDerivedDeclarationInfo derived)
            {
                derivedEdges[declaration.SemanticId] = [.. derived.Dependencies
                    .Where(dependency => _declarationsById[dependency] is IXsrDerivedDeclarationInfo)];
            }
        }

        HashSet<XsrSemanticId> visiting = [];
        HashSet<XsrSemanticId> visited = [];
        foreach (XsrSemanticId semanticId in derivedEdges.Keys)
        {
            Visit(semanticId, derivedEdges, visiting, visited);
        }
    }

    private static void Visit(
        XsrSemanticId semanticId,
        Dictionary<XsrSemanticId, List<XsrSemanticId>> edges,
        HashSet<XsrSemanticId> visiting,
        HashSet<XsrSemanticId> visited)
    {
        if (visited.Contains(semanticId))
        {
            return;
        }

        if (!visiting.Add(semanticId))
        {
            throw new InvalidOperationException(
                $"The XSR derived-state graph contains a cycle at '{semanticId}'.");
        }

        foreach (XsrSemanticId dependency in edges[semanticId])
        {
            Visit(dependency, edges, visiting, visited);
        }

        _ = visiting.Remove(semanticId);
        _ = visited.Add(semanticId);
    }
}
