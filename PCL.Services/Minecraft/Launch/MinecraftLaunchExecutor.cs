using PCL.Services.Logging;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.Process;

namespace PCL.Services.Minecraft.Launch;

/// <summary>
/// Executes a prepared launch in the required order: validate artifacts, stage native archives,
/// then hand the immutable plan to the process boundary.
/// </summary>
public sealed class MinecraftLaunchExecutor
{
    private readonly MinecraftProcessService _processes;
    private readonly LogService? _log;

    public MinecraftLaunchExecutor(MinecraftProcessService processes, LogService? log = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _log = log;
    }

    public async ValueTask<MinecraftProcessSession> ExecuteAsync(
        MinecraftLaunchPlan plan,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        using LogOperation? operation = _log?.BeginOperation("Launch", "ExecuteLaunch", $"instance={instanceId}");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            operation?.Stage("validate_native_archives", $"count={plan.NativeLibraries.Count}");
            IReadOnlyList<MinecraftLibraryToken> natives = plan.NativeLibraries;
            string[] nativePaths = new string[natives.Count];
            for (int index = 0; index < natives.Count; index++)
            {
                string path = natives[index].LocalPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    _log?.Warn("Launch", $"Required native archive is missing instance={instanceId} archive={path}");
                    throw new FileNotFoundException(
                        "A native library archive required by the Minecraft launch is missing.",
                        path);
                }

                nativePaths[index] = path;
            }

            // Even a launch with no native archives gets a deterministic directory for
            // ${natives_directory}; extraction itself is idempotent and cancellation-aware.
            string nativesDirectory = string.IsNullOrWhiteSpace(plan.NativesDirectory)
                ? Path.Combine(plan.WorkingDirectory, "natives")
                : plan.NativesDirectory;
            operation?.Stage("extract_natives", $"directory={nativesDirectory}");
            await MinecraftNativesExtractor.ExtractAsync(nativePaths, nativesDirectory, cancellationToken)
                .ConfigureAwait(false);
            operation?.Stage("start_process");
            MinecraftProcessSession session = await _processes.StartAsync(plan, instanceId, cancellationToken).ConfigureAwait(false);
            operation?.Complete($"session={session.Snapshot.SessionId} pid={session.Snapshot.ProcessId}");
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation?.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            operation?.Fail(exception);
            throw;
        }
    }
}
