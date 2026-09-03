using PCL.Services.Minecraft;
using PCL.Xsr;
using PCL.Xsr.Runtime;

namespace PCL.Desktop.Ui;

/// <summary>
/// Cancellable instance-query boundary for the launch-page projection. Tests can control
/// completion order without replacing Minecraft business services.
/// </summary>
internal interface ILaunchPageInstanceSource
{
    ValueTask<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> ReadAsync(
        CancellationToken cancellationToken);
}

internal sealed class MinecraftRuntimeLaunchPageInstanceSource : ILaunchPageInstanceSource
{
    private readonly XsrQueryRouter _queries;
    private readonly string _minecraftRootDirectory;

    public MinecraftRuntimeLaunchPageInstanceSource(
        XsrQueryRouter queries,
        string minecraftRootDirectory)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        _minecraftRootDirectory = Path.GetFullPath(minecraftRootDirectory);
    }

    public ValueTask<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!_queries.TryResolve(MinecraftRouteIds.InstancesRead, out XsrQueryId queryId))
        {
            return ValueTask.FromResult(
                XsrResult.Failure<IReadOnlyList<MinecraftInstanceDescriptor>>(
                    XsrRuntimeErrors.RouteNotFound()));
        }

        return _queries.QueryAsync<MinecraftInstancesQuery, IReadOnlyList<MinecraftInstanceDescriptor>>(
            queryId,
            new MinecraftInstancesQuery(_minecraftRootDirectory),
            cancellationToken: cancellationToken);
    }
}
