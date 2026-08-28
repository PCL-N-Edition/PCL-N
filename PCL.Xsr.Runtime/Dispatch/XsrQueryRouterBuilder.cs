namespace PCL.Xsr.Runtime;

/// <summary>
/// Collects query routes during startup and builds an immutable router.
/// </summary>
public sealed class XsrQueryRouterBuilder
{
    private readonly XsrRegistry<IXsrQueryRoute> _routes = new();

    public void Register<TQuery, TResponse>(
        XsrSemanticId semanticId,
        XsrQueryHandler<TQuery, TResponse> handler)
        where TQuery : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        _routes.Register(semanticId, new XsrQueryRoute<TQuery, TResponse>(handler));
    }

    public XsrQueryRouter Build(
        IXsrDispatchObserver observer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new XsrQueryRouter(_routes.Seal(), observer, timeProvider ?? TimeProvider.System);
    }
}

internal interface IXsrQueryRoute;

internal sealed class XsrQueryRoute<TQuery, TResponse>(XsrQueryHandler<TQuery, TResponse> handler) : IXsrQueryRoute
    where TQuery : notnull
{
    public XsrQueryHandler<TQuery, TResponse> Handler { get; } = handler;
}
