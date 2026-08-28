using System.Collections.Concurrent;

namespace PCL.Xsr.Runtime;

/// <summary>
/// Collects scope and event declarations during startup and builds an immutable event router.
/// </summary>
public sealed class XsrEventRouterBuilder
{
    private readonly Dictionary<XsrSemanticId, int> _scopes = [];
    private readonly XsrRegistry<IXsrEventRoute> _routes = new();

    /// <summary>
    /// Declares one ordering domain with its bounded buffer capacity.
    /// </summary>
    public void DeclareScope(XsrSemanticId scopeId, int capacity)
    {
        if (!scopeId.IsAssigned)
        {
            throw new ArgumentException("The scope identifier must be assigned.", nameof(scopeId));
        }

        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "A scope capacity must be positive.");
        }

        if (_scopes.ContainsKey(scopeId))
        {
            throw new InvalidOperationException($"The XSR scope '{scopeId}' is already declared.");
        }

        _scopes[scopeId] = capacity;
    }

    /// <summary>
    /// Registers one typed event inside a declared ordering domain.
    /// </summary>
    public void Register<TEvent>(XsrSemanticId semanticId, XsrSemanticId scopeId, XsrEventOrdering ordering)
        where TEvent : notnull
    {
        if (!semanticId.IsAssigned)
        {
            throw new ArgumentException("The event identifier must be assigned.", nameof(semanticId));
        }

        if (!Enum.IsDefined(ordering))
        {
            throw new ArgumentOutOfRangeException(nameof(ordering));
        }

        if (!_scopes.TryGetValue(scopeId, out int capacity))
        {
            throw new InvalidOperationException(
                $"The XSR event '{semanticId}' references undeclared scope '{scopeId}'.");
        }

        _routes.Register(semanticId, new XsrEventRoute<TEvent>(scopeId, ordering, capacity));
    }

    /// <summary>
    /// Seals registration and returns the immutable, concurrently readable router.
    /// </summary>
    public XsrEventRouter Build(IXsrEventObserver? observer = null, TimeProvider? timeProvider = null) =>
        new(_routes.Seal(), observer, timeProvider ?? TimeProvider.System);
}
