using PCL.Services.Minecraft.Crash;
using PCL.Services.Minecraft.Launch;
using PCL.Xsr;

namespace PCL.Services.Minecraft;

public sealed record MinecraftVersionsQuery(string MinecraftRootDirectory);
public sealed record MinecraftInstancesQuery(string MinecraftRootDirectory);
public sealed record MinecraftStartCommand(string InstanceId, int AccountIndex);
public sealed record MinecraftLaunchCommand(MinecraftLaunchRequest Request);
public sealed record MinecraftCancelProcessCommand(Guid SessionId);
public sealed record MinecraftCancelLaunchCommand;
public sealed record MinecraftCrashAnalyzeQuery(IReadOnlyList<string> Evidence, string? Stage = null, string? LastClassName = null);

public static class MinecraftRouteIds
{
    public static readonly XsrSemanticId VersionsRead = XsrSemanticId.Parse("minecraft.versions.read");
    public static readonly XsrSemanticId InstancesRead = XsrSemanticId.Parse("minecraft.instances.read");
    public static readonly XsrSemanticId Start = XsrSemanticId.Parse("minecraft.start");
    public static readonly XsrSemanticId Launch = XsrSemanticId.Parse("minecraft.launch");
    public static readonly XsrSemanticId LaunchCancel = XsrSemanticId.Parse("minecraft.launch.cancel");
    public static readonly XsrSemanticId ProcessCancel = XsrSemanticId.Parse("minecraft.process.cancel");
    public static readonly XsrSemanticId CrashAnalyze = XsrSemanticId.Parse("minecraft.crash.analyze");
}

public static class MinecraftErrors
{
    public static readonly XsrSemanticId InvalidRequestCode = XsrSemanticId.Parse("minecraft.invalid_request");
    public static readonly XsrSemanticId LaunchFailedCode = XsrSemanticId.Parse("minecraft.launch_failed");
    public static readonly XsrSemanticId LaunchPreparationFailedCode = XsrSemanticId.Parse("minecraft.launch_preparation_failed");
    public static readonly XsrSemanticId InstanceNotFoundCode = XsrSemanticId.Parse("minecraft.instance_not_found");
    public static readonly XsrSemanticId JavaUnavailableCode = XsrSemanticId.Parse("minecraft.java_unavailable");
    public static readonly XsrSemanticId UnsupportedAccountCode = XsrSemanticId.Parse("minecraft.unsupported_account");
    public static readonly XsrSemanticId ProcessNotFoundCode = XsrSemanticId.Parse("minecraft.process_not_found");

    public static XsrError InvalidRequest(string reason) => new(XsrErrorKind.Rejected, InvalidRequestCode, $"The Minecraft request was rejected: {reason}");
    public static XsrError LaunchFailed(string reason) => new(XsrErrorKind.Unavailable, LaunchFailedCode, $"The Minecraft process could not be started: {reason}");
    public static XsrError LaunchPreparationFailed(string reason) => new(XsrErrorKind.Rejected, LaunchPreparationFailedCode, $"The Minecraft launch could not be prepared: {reason}");
    public static XsrError InstanceNotFound(string instanceId) => new(XsrErrorKind.NotFound, InstanceNotFoundCode, $"The Minecraft instance '{instanceId}' is not installed.");
    public static XsrError JavaUnavailable(string reason) => new(XsrErrorKind.Unavailable, JavaUnavailableCode, $"A compatible Java runtime is unavailable: {reason}");
    public static XsrError UnsupportedAccount(string reason) => new(XsrErrorKind.Rejected, UnsupportedAccountCode, $"The launch account cannot be used: {reason}");
    public static XsrError ProcessNotFound(Guid sessionId) => new(XsrErrorKind.NotFound, ProcessNotFoundCode, $"The Minecraft process session '{sessionId}' was not found or has already ended.");
}

public static class MinecraftCommands
{
    public static XsrCommandHandler<MinecraftStartCommand> CreateStartHandler(
        MinecraftLaunchCoordinator coordinator) =>
        async (command, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(coordinator);
            return await coordinator.StartAsync(
                command.InstanceId,
                command.AccountIndex,
                cancellationToken).ConfigureAwait(false);
        };

    public static XsrCommandHandler<MinecraftLaunchCommand> CreateLaunchHandler(Process.MinecraftProcessService processService) =>
        CreateLaunchHandler(new Launch.MinecraftLaunchExecutor(processService));

    public static XsrCommandHandler<MinecraftLaunchCommand> CreateLaunchHandler(Launch.MinecraftLaunchExecutor executor) =>
        async (command, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(executor);
            try
            {
                MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(command.Request);
                await executor.ExecuteAsync(plan, command.Request.VersionId, cancellationToken: cancellationToken).ConfigureAwait(false);
                return XsrResult.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return XsrResult.Failure(MinecraftErrors.LaunchFailed(exception.Message));
            }
        };

    public static XsrCommandHandler<MinecraftCancelProcessCommand> CreateCancelProcessHandler(Process.MinecraftProcessService processService) =>
        (command, _) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(processService);
            return ValueTask.FromResult(processService.TryCancel(command.SessionId)
                ? XsrResult.Success()
                : XsrResult.Failure(MinecraftErrors.ProcessNotFound(command.SessionId)));
        };

    public static XsrCommandHandler<MinecraftCancelLaunchCommand> CreateCancelLaunchHandler(
        Launch.MinecraftLaunchCoordinator coordinator) =>
        (command, _) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(coordinator);
            return ValueTask.FromResult(coordinator.CancelActiveLaunch()
                ? XsrResult.Success()
                : XsrResult.Failure(MinecraftErrors.InvalidRequest("no launch pipeline is running.")));
        };
}

public static class MinecraftQueries
{
    public static XsrQueryHandler<MinecraftVersionsQuery, IReadOnlyList<MinecraftVersionDescriptor>> CreateVersionsHandler(MinecraftVersionDiscovery discovery) =>
        (query, _) =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(discovery);
            try { return ValueTask.FromResult(XsrResult.Success<IReadOnlyList<MinecraftVersionDescriptor>>(discovery.Discover(query.MinecraftRootDirectory))); }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            { return ValueTask.FromResult(XsrResult.Failure<IReadOnlyList<MinecraftVersionDescriptor>>(MinecraftErrors.InvalidRequest(exception.Message))); }
        };

    public static XsrQueryHandler<MinecraftCrashAnalyzeQuery, MinecraftLaunchFaultReport> CreateCrashHandler() =>
        (query, _) =>
        {
            ArgumentNullException.ThrowIfNull(query);
            MinecraftLaunchFaultReport report = MinecraftLaunchFaultAnalyzer.AnalyzeText(query.Evidence, query.Stage, query.LastClassName);
            return ValueTask.FromResult(XsrResult.Success(report));
        };

    public static XsrQueryHandler<MinecraftInstancesQuery, IReadOnlyList<MinecraftInstanceDescriptor>> CreateInstancesHandler(MinecraftInstanceDiscovery discovery) =>
        async (query, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(discovery);
            try { return XsrResult.Success<IReadOnlyList<MinecraftInstanceDescriptor>>(await discovery.DiscoverAsync(query.MinecraftRootDirectory, cancellationToken).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            { return XsrResult.Failure<IReadOnlyList<MinecraftInstanceDescriptor>>(MinecraftErrors.InvalidRequest(exception.Message)); }
        };
}
