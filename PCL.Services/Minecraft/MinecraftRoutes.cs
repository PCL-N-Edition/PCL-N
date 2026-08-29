using PCL.Services.Minecraft.Crash;
using PCL.Services.Minecraft.Launch;
using PCL.Xsr;

namespace PCL.Services.Minecraft;

public sealed record MinecraftVersionsQuery(string MinecraftRootDirectory);
public sealed record MinecraftLaunchCommand(MinecraftLaunchRequest Request);
public sealed record MinecraftCrashAnalyzeQuery(IReadOnlyList<string> Evidence, string? Stage = null, string? LastClassName = null);

public static class MinecraftRouteIds
{
    public static readonly XsrSemanticId VersionsRead = XsrSemanticId.Parse("minecraft.versions.read");
    public static readonly XsrSemanticId Launch = XsrSemanticId.Parse("minecraft.launch");
    public static readonly XsrSemanticId CrashAnalyze = XsrSemanticId.Parse("minecraft.crash.analyze");
}

public static class MinecraftErrors
{
    public static readonly XsrSemanticId InvalidRequestCode = XsrSemanticId.Parse("minecraft.invalid_request");
    public static readonly XsrSemanticId LaunchFailedCode = XsrSemanticId.Parse("minecraft.launch_failed");

    public static XsrError InvalidRequest(string reason) => new(XsrErrorKind.Rejected, InvalidRequestCode, $"The Minecraft request was rejected: {reason}");
    public static XsrError LaunchFailed(string reason) => new(XsrErrorKind.Unavailable, LaunchFailedCode, $"The Minecraft process could not be started: {reason}");
}

public static class MinecraftCommands
{
    public static XsrCommandHandler<MinecraftLaunchCommand> CreateLaunchHandler(Process.MinecraftProcessService processService) =>
        async (command, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(processService);
            try
            {
                MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(command.Request);
                await processService.StartAsync(plan, command.Request.VersionId, cancellationToken).ConfigureAwait(false);
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
}

