namespace PCL.Xsr.Runtime;

/// <summary>
/// Collects XSR registrations during startup and seals them into a deterministic snapshot.
/// </summary>
/// <remarks>
/// Registration and sealing are thread-safe. The returned snapshot is immutable; registered
/// descriptor objects are expected to be immutable as well.
/// </remarks>
public sealed class XsrRegistry<TDescriptor>
    where TDescriptor : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<XsrSemanticId, TDescriptor> _registrations = [];
    private XsrRegistrySnapshot<TDescriptor>? _snapshot;

    /// <summary>
    /// Gets the number of registered descriptors.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _snapshot?.Count ?? _registrations.Count;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether registration has ended.
    /// </summary>
    public bool IsSealed
    {
        get
        {
            lock (_gate)
            {
                return _snapshot is not null;
            }
        }
    }

    /// <summary>
    /// Adds a descriptor during the registration phase.
    /// </summary>
    public void Register(XsrSemanticId semanticId, TDescriptor descriptor)
    {
        if (!semanticId.IsAssigned)
        {
            throw new ArgumentException("The semantic identifier must be assigned.", nameof(semanticId));
        }

        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_gate)
        {
            if (_snapshot is not null)
            {
                throw new InvalidOperationException("The XSR registry is sealed and cannot accept registrations.");
            }

            if (!_registrations.TryAdd(semanticId, descriptor))
            {
                throw new InvalidOperationException(
                    $"The XSR semantic identifier '{semanticId}' is already registered.");
            }
        }
    }

    /// <summary>
    /// Ends registration and returns the immutable runtime lookup snapshot.
    /// </summary>
    public XsrRegistrySnapshot<TDescriptor> Seal()
    {
        lock (_gate)
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            XsrRegistryEntry<TDescriptor>[] entries = _registrations
                .OrderBy(registration => registration.Key.Value, StringComparer.Ordinal)
                .Select((registration, index) => new XsrRegistryEntry<TDescriptor>(
                    registration.Key,
                    new XsrRuntimeId(checked((uint)index + 1)),
                    registration.Value))
                .ToArray();

            _snapshot = new XsrRegistrySnapshot<TDescriptor>(entries);
            _registrations.Clear();
            return _snapshot;
        }
    }
}
