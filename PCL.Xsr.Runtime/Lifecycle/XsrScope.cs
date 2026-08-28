namespace PCL.Xsr.Runtime;

/// <summary>
/// One runtime lifetime scope: a named node in a disposal tree that owns resources, child
/// scopes, and cancellation. Disposing a scope atomically releases everything registered on it
/// and every scope below it — plugin unload, window teardown, and Sidecar session retirement are
/// single-scope operations. This is a lifetime abstraction, distinct from the event ordering
/// scopes of the event router.
/// </summary>
public interface IXsrScope : IDisposable
{
    XsrScopeId Id { get; }

    string Name { get; }

    IXsrScope? Parent { get; }

    bool IsDisposed { get; }

    /// <summary>
    /// Creates one child scope that is disposed with (and before) this scope.
    /// </summary>
    IXsrScope CreateChild(string name);

    /// <summary>
    /// Registers one resource released in reverse registration order when this scope disposes.
    /// </summary>
    void Register(IDisposable resource);

    /// <summary>
    /// Registers one cleanup action released in reverse registration order when this scope
    /// disposes.
    /// </summary>
    void Register(Action dispose);

    /// <summary>
    /// Unregisters one previously registered resource without disposing it.
    /// </summary>
    bool Unregister(IDisposable resource);
}

/// <summary>
/// The default lifetime scope implementation. Disposal is idempotent, safe under concurrency,
/// and runs child scopes first, then registered resources in reverse order; individual cleanup
/// failures never stop the remaining cleanup.
/// </summary>
public sealed class XsrScope : IXsrScope
{
    private readonly object _gate = new();
    private readonly IXsrScope? _parent;
    private readonly List<IXsrScope> _children = [];
    private readonly List<object> _resources = [];
    private bool _disposed;

    public XsrScope(string name)
        : this(name, null)
    {
    }

    private XsrScope(string name, XsrScope? parent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Id = XsrScopeId.Create();
        _parent = parent;
    }

    public XsrScopeId Id { get; }

    public string Name { get; }

    public IXsrScope? Parent => _parent;

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    public IXsrScope CreateChild(string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(XsrScope));
            XsrScope child = new(name, this);
            _children.Add(child);
            return child;
        }
    }

    public void Register(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Register(resource.Dispose);
    }

    public void Register(Action dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(XsrScope));
            _resources.Add(dispose);
        }
    }

    public bool Unregister(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        lock (_gate)
        {
            return _resources.Remove((Action)resource.Dispose);
        }
    }

    public void Dispose()
    {
        IXsrScope[] children;
        object[] resources;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            children = [.. _children];
            _children.Clear();
            resources = [.. _resources];
            _resources.Clear();
        }

        // Detach from the parent first so a concurrent parent disposal cannot reach us again.
        if (_parent is XsrScope parent)
        {
            parent.DetachChild(this);
        }

        foreach (IXsrScope child in children)
        {
            child.Dispose();
        }

        for (int index = resources.Length - 1; index >= 0; index--)
        {
            try
            {
                ((Action)resources[index])();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                // One failing resource never stops the remaining cleanup.
            }
        }
    }

    private void DetachChild(XsrScope child)
    {
        lock (_gate)
        {
            _ = _children.Remove(child);
        }
    }
}
