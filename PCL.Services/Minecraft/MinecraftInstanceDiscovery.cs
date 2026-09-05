using PCL.Services.Logging;

namespace PCL.Services.Minecraft;

/// <summary>A discovered, user-owned Minecraft instance rooted below a Minecraft installation.</summary>
public sealed record MinecraftInstanceDescriptor(
    string Id,
    string DirectoryPath,
    string VersionId,
    MinecraftVersionDescriptor Version,
    MinecraftInstanceMetadata Metadata);

/// <summary>
/// Discovers installed version/instance directories without loading UI state. Metadata is read
/// through the same atomic store used by instance commands, so discovery never observes a
/// partially written document.
/// </summary>
public sealed class MinecraftInstanceDiscovery(
    LogService? log = null,
    MinecraftVersionDiscovery? versionDiscovery = null,
    MinecraftInstanceMetadataStore? metadataStore = null) : IMinecraftInstanceSource
{
    private const string LogModuleName = "InstanceScan";

    private readonly LogService? _log = log;
    private readonly MinecraftVersionDiscovery _versionDiscovery = versionDiscovery ?? new MinecraftVersionDiscovery();
    private readonly MinecraftInstanceMetadataStore _metadataStore = metadataStore ?? new MinecraftInstanceMetadataStore();

    public async ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(
        string minecraftRootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        using LogOperation? operation = _log?.BeginOperation(LogModuleName, "DiscoverInstances", $"root={minecraftRootDirectory}");
        string? currentInstance = null;
        try
        {
            operation?.Stage("discover_versions");
            IReadOnlyList<MinecraftVersionDescriptor> versions = await Task.Run(
                () => _versionDiscovery.Discover(minecraftRootDirectory), cancellationToken).ConfigureAwait(false);
            _log?.Write(LogLevel.RealTime, LogModuleName,
                $"Version directories discovered root={minecraftRootDirectory} count={versions.Count}");
            operation?.Stage("read_instance_metadata", $"count={versions.Count}");
            List<MinecraftInstanceDescriptor> result = new(versions.Count);
            foreach (MinecraftVersionDescriptor version in versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = Path.GetFileName(version.DirectoryPath);
                if (!MinecraftVersionPaths.IsSafeReference(id)) continue;
                currentInstance = id;
                MinecraftInstanceMetadata metadata = await _metadataStore.LoadAsync(version.DirectoryPath, cancellationToken).ConfigureAwait(false);
                _log?.Write(LogLevel.RealTime, LogModuleName,
                    $"Instance metadata loaded instance={id} version={version.Id}");
                result.Add(new MinecraftInstanceDescriptor(id, version.DirectoryPath, version.Id, version, metadata));
            }
            operation?.Complete($"count={result.Count}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation?.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            _log?.Warn(LogModuleName, $"Instance discovery failed current_instance={currentInstance}");
            operation?.Fail(exception);
            throw;
        }
    }
}
