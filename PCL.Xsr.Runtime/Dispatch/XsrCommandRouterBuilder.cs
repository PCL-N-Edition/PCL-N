namespace PCL.Xsr.Runtime;

/// <summary>
/// Collects command routes during startup and builds an immutable router.
/// </summary>
public sealed class XsrCommandRouterBuilder
{
    private readonly XsrRegistry<IXsrCommandRoute> _routes = new();

    public void Register<TCommand>(
        XsrSemanticId semanticId,
        XsrCommandHandler<TCommand> handler)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        _routes.Register(semanticId, new XsrCommandRoute<TCommand>(handler));
    }

    public XsrCommandRouter Build(
        IXsrDispatchObserver observer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new XsrCommandRouter(_routes.Seal(), observer, timeProvider ?? TimeProvider.System);
    }
}

internal interface IXsrCommandRoute;

internal sealed class XsrCommandRoute<TCommand>(XsrCommandHandler<TCommand> handler) : IXsrCommandRoute
    where TCommand : notnull
{
    public XsrCommandHandler<TCommand> Handler { get; } = handler;
}
