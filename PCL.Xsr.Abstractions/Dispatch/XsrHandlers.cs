namespace PCL.Xsr;

/// <summary>
/// Handles one typed command asynchronously.
/// </summary>
public delegate ValueTask<XsrResult> XsrCommandHandler<TCommand>(
    TCommand command,
    CancellationToken cancellationToken)
    where TCommand : notnull;

/// <summary>
/// Handles one typed query asynchronously.
/// </summary>
public delegate ValueTask<XsrResult<TResponse>> XsrQueryHandler<TQuery, TResponse>(
    TQuery query,
    CancellationToken cancellationToken)
    where TQuery : notnull;
