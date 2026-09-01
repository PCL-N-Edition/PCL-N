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

    public MinecraftLaunchExecutor(MinecraftProcessService processes)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
    }

    public async ValueTask<MinecraftProcessSession> ExecuteAsync(
        MinecraftLaunchPlan plan,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MinecraftLibraryToken> natives = plan.NativeLibraries;
        string[] nativePaths = new string[natives.Count];
        for (int index = 0; index < natives.Count; index++)
        {
            string path = natives[index].LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
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
        await MinecraftNativesExtractor.ExtractAsync(nativePaths, nativesDirectory, cancellationToken)
            .ConfigureAwait(false);
        return await _processes.StartAsync(plan, instanceId, cancellationToken).ConfigureAwait(false);
    }
}
