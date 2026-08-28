using System.Collections.Concurrent;

namespace PCL.Xsr.Runtime;

/// <summary>
/// Dispatches typed events through compact runtime identifiers into bounded ordering scopes.
/// One declared scope is one ordering domain: every event routed into it shares the same scope
/// instances and one contiguous sequence. Publication never blocks and never drops: a full scope
/// rejects publication with backpressure.
/// </summary>
public sealed class XsrEventRouter
{
    private readonly XsrRegistrySnapshot<IXsrEventRoute> _routes;
    private readonly Dictionary<XsrSemanticId, XsrEventScopeTable> _scopes;
    private readonly IXsrEventObserver? _observer;
    private readonly TimeProvider _timeProvider;

    internal XsrEventRouter(
        XsrRegistrySnapshot<IXsrEventRoute> routes,
        IXsrEventObserver? observer,
        TimeProvider timeProvider)
    {
        _routes = routes;
        _observer = observer;
        _timeProvider = timeProvider;

        _scopes = [];
        foreach (XsrRegistryEntry<IXsrEventRoute> entry in routes.Entries)
        {
            if (!_scopes.ContainsKey(entry.Descriptor.ScopeId))
            {
                _scopes[entry.Descriptor.ScopeId] = new XsrEventScopeTable(entry.Descriptor.Capacity);
            }
        }
    }

    public int Count => _routes.Count;

    public bool TryResolve(XsrSemanticId semanticId, out XsrEventId eventId)
    {
        if (_routes.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId))
        {
            eventId = new XsrEventId(runtimeId);
            return true;
        }

        eventId = default;
        return false;
    }

    /// <summary>
    /// Resolves a declared semantic identifier to its event identifier, rejecting undeclared input.
    /// </summary>
    public XsrEventId Resolve(XsrSemanticId semanticId) =>
        TryResolve(semanticId, out XsrEventId eventId)
            ? eventId
            : throw new InvalidOperationException($"The XSR event '{semanticId}' is not registered.");

    /// <summary>
    /// Publishes one typed event into its ordering scope, assigning the next contiguous sequence.
    /// Returns a stable error instead of throwing when the route is unknown, the contract does not
    /// match, or the scope buffer is full.
    /// </summary>
    public XsrResult Publish<TEvent>(
        XsrEventId eventId,
        TEvent payload,
        XsrCorrelationId correlationId = default,
        string? scopeKey = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        correlationId = EnsureCorrelationId(correlationId);

        if (!_routes.TryGet(eventId.Value, out XsrRegistryEntry<IXsrEventRoute> entry))
        {
            return XsrResult.Failure(XsrRuntimeErrors.RouteNotFound());
        }

        if (entry.Descriptor is not XsrEventRoute<TEvent> route)
        {
            return XsrResult.Failure(XsrRuntimeErrors.ContractMismatch());
        }

        string key = ResolveScopeKey(route, scopeKey);
        XsrEventScopeTable table = _scopes[route.ScopeId];
        XsrEventScopeInstance scope = table.Instances.GetOrAdd(
            key,
            _ => new XsrEventScopeInstance(table.Capacity));
        long timestamp = _timeProvider.GetTimestamp();

        if (!scope.TryEnqueue(
                eventId,
                entry.SemanticId,
                route.ScopeId,
                key,
                correlationId,
                timestamp,
                payload,
                out long sequence))
        {
            return XsrResult.Failure(XsrRuntimeErrors.Backpressure());
        }

        Notify(new XsrEventPublication(
            sequence,
            eventId,
            entry.SemanticId,
            route.ScopeId,
            key,
            correlationId,
            timestamp));
        return XsrResult.Success();
    }

    /// <summary>
    /// Subscribes to one ordering scope. A positive <paramref name="replayFromSequence"/> replays
    /// retained records first; consumers tolerate the resulting duplication.
    /// </summary>
    public XsrEventSubscription<TEvent> Subscribe<TEvent>(
        XsrEventId eventId,
        string? scopeKey = null,
        long replayFromSequence = 0)
        where TEvent : notnull
    {
        if (!_routes.TryGet(eventId.Value, out XsrRegistryEntry<IXsrEventRoute> entry))
        {
            throw new InvalidOperationException($"The XSR event '{eventId}' is not registered.");
        }

        if (entry.Descriptor is not XsrEventRoute<TEvent> route)
        {
            throw new InvalidOperationException(
                $"The XSR event '{entry.SemanticId}' does not carry the contract '{typeof(TEvent).Name}'.");
        }

        string key = ResolveScopeKey(route, scopeKey);
        XsrEventScopeTable table = _scopes[route.ScopeId];
        XsrEventScopeInstance scope = table.Instances.GetOrAdd(
            key,
            _ => new XsrEventScopeInstance(table.Capacity));
        long cursor = scope.AttachCursor(replayFromSequence);
        return new XsrEventSubscription<TEvent>(scope, eventId, cursor, entry.SemanticId, route.ScopeId, key);
    }

    /// <summary>
    /// Reports the number of retained records in one ordering scope.
    /// </summary>
    public bool TryGetQueueDepth(XsrEventId eventId, string? scopeKey, out int depth)
    {
        if (_routes.TryGet(eventId.Value, out XsrRegistryEntry<IXsrEventRoute> entry)
            && _scopes.TryGetValue(entry.Descriptor.ScopeId, out XsrEventScopeTable? table)
            && table.Instances.TryGetValue(ResolveScopeKey(entry.Descriptor, scopeKey), out XsrEventScopeInstance? scope))
        {
            depth = scope.Depth;
            return true;
        }

        depth = 0;
        return false;
    }

    private static string ResolveScopeKey(IXsrEventRoute route, string? scopeKey)
    {
        if (route.Ordering == XsrEventOrdering.Global)
        {
            return string.Empty;
        }

        return string.IsNullOrEmpty(scopeKey)
            ? throw new ArgumentException(
                "A per-key XSR event requires a non-empty scope key.",
                nameof(scopeKey))
            : scopeKey;
    }

    private static XsrCorrelationId EnsureCorrelationId(XsrCorrelationId correlationId) =>
        correlationId.IsAssigned ? correlationId : XsrCorrelationId.Create();

    private void Notify(XsrEventPublication publication)
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            _observer.OnPublished(publication);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Event publication must not be changed by a diagnostics observer failure.
        }
    }
}

internal interface IXsrEventRoute
{
    XsrSemanticId ScopeId { get; }

    XsrEventOrdering Ordering { get; }

    int Capacity { get; }
}

internal sealed class XsrEventRoute<TEvent> : IXsrEventRoute
    where TEvent : notnull
{
    public XsrEventRoute(XsrSemanticId scopeId, XsrEventOrdering ordering, int capacity)
    {
        ScopeId = scopeId;
        Ordering = ordering;
        Capacity = capacity;
    }

    public XsrSemanticId ScopeId { get; }

    public XsrEventOrdering Ordering { get; }

    public int Capacity { get; }
}

/// <summary>
/// Holds the scope instances of one declared ordering domain. Every event routed into the scope
/// resolves its instances here, so the sequence space is shared across event contracts.
/// </summary>
internal sealed class XsrEventScopeTable(int capacity)
{
    public int Capacity { get; } = capacity;

    public ConcurrentDictionary<string, XsrEventScopeInstance> Instances { get; } =
        new(StringComparer.Ordinal);
}
