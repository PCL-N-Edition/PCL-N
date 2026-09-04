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
    MinecraftInstanceMetadataStore? metadataStore = null)
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
        IReadOnlyList<MinecraftVersionDescriptor> versions = _versionDiscovery.Discover(minecraftRootDirectory);
        _log?.Write(LogLevel.RealTime, LogModuleName,
            $"扫描根目录 {minecraftRootDirectory}：发现 {versions.Count} 个版本目录。");
        List<MinecraftInstanceDescriptor> result = new(versions.Count);
        foreach (MinecraftVersionDescriptor version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = Path.GetFileName(version.DirectoryPath);
            if (!MinecraftVersionPaths.IsSafeReference(id)) continue;
            MinecraftInstanceMetadata metadata = await _metadataStore.LoadAsync(version.DirectoryPath, cancellationToken).ConfigureAwait(false);
            _log?.Write(LogLevel.RealTime, LogModuleName,
                $"实例 {id}（版本 {version.Id}，描述 {metadata.Description}）。");
            result.Add(new MinecraftInstanceDescriptor(id, version.DirectoryPath, version.Id, version, metadata));
        }
        return result;
    }
}
