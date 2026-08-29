using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Services.Minecraft;

/// <summary>
/// The user-owned per-instance document. Schema 1 intentionally retains the legacy field
/// names and defaults; secrets and runtime state never belong in this file.
/// </summary>
public sealed record MinecraftInstanceMetadata
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Description { get; init; } = string.Empty;
    public int LaunchCount { get; init; }
    public string ModpackVersion { get; init; } = string.Empty;
    public string ModpackProjectId { get; init; } = string.Empty;
    public bool IsStarred { get; init; }
    public string LogoPath { get; init; } = string.Empty;
    public int CardType { get; init; }
    public bool DisableAssetVerification { get; init; }
    public bool InstanceIsolation { get; init; } = true;
    public string WindowTitle { get; init; } = string.Empty;
    public bool UseGlobalWindowTitle { get; init; } = true;
    public string CustomInfo { get; init; } = string.Empty;
    public int JavaSelectionMode { get; init; }
    public string SelectedJavaPath { get; init; } = string.Empty;
    public int MemorySolution { get; init; } = 2;
    public int CustomMemorySize { get; init; } = 15;
    public int ServerLoginRequirement { get; init; }
    public string AuthServerAddress { get; init; } = string.Empty;
    public string AuthRegisterAddress { get; init; } = string.Empty;
    public string AuthServerDisplayName { get; init; } = string.Empty;
    public bool AuthSettingsLocked { get; init; }
    public string ServerToEnter { get; init; } = string.Empty;
    public int Renderer { get; init; }
    public string JvmArguments { get; init; } = string.Empty;
    public string GameArguments { get; init; } = string.Empty;
    public string ClasspathHead { get; init; } = string.Empty;
    public string WrapperCommand { get; init; } = string.Empty;
    public string PreLaunchCommand { get; init; } = string.Empty;
    public bool WaitForPreLaunchCommand { get; init; } = true;
    public bool IgnoreJavaCompatibility { get; init; }
    public bool UseProxy { get; init; }
    public bool DisableJlw { get; init; }
    public bool DisableRw { get; init; }
    public bool UseDebugLog4j2Config { get; init; }
    public bool DisableLwjglUnsafeAgent { get; init; }
    public bool UseSystemGlfw { get; init; }
    public bool ForceX11OnWayland { get; init; } = true;
}

public sealed class MinecraftInstanceMetadataStore
{
    public const string MetadataDirectoryName = "PCL";
    public const string MetadataFileName = "InstanceMetadata.json";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(GetPathComparer());
    private readonly string _metadataDirectoryName = MetadataDirectoryName;
    private readonly string _metadataFileName = MetadataFileName;

    public string GetMetadataPath(string instanceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);
        return Path.Combine(Path.GetFullPath(instanceDirectory), _metadataDirectoryName, _metadataFileName);
    }

    public async Task<MinecraftInstanceMetadata> LoadAsync(string instanceDirectory, CancellationToken cancellationToken = default)
    {
        string path = GetMetadataPath(instanceDirectory);
        SemaphoreSlim gate = _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(path, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(string instanceDirectory, MinecraftInstanceMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.SchemaVersion != MinecraftInstanceMetadata.CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(metadata), metadata.SchemaVersion, "Only schema 1 can be saved.");
        string path = GetMetadataPath(instanceDirectory);
        SemaphoreSlim gate = _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(path, metadata, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<MinecraftInstanceMetadata> UpdateAsync(string instanceDirectory, Func<MinecraftInstanceMetadata, MinecraftInstanceMetadata> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        string path = GetMetadataPath(instanceDirectory);
        SemaphoreSlim gate = _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MinecraftInstanceMetadata current = await LoadCoreAsync(path, cancellationToken).ConfigureAwait(false);
            MinecraftInstanceMetadata next = update(current) ?? throw new InvalidOperationException("The metadata callback returned null.");
            if (next.SchemaVersion != MinecraftInstanceMetadata.CurrentSchemaVersion)
                throw new ArgumentOutOfRangeException(nameof(update), next.SchemaVersion, "Only schema 1 can be saved.");
            await SaveCoreAsync(path, next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally { gate.Release(); }
    }

    private static async Task<MinecraftInstanceMetadata> LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new MinecraftInstanceMetadata();
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            MinecraftInstanceMetadata? metadata = await JsonSerializer.DeserializeAsync(stream, MinecraftJsonContext.Default.MinecraftInstanceMetadata, cancellationToken).ConfigureAwait(false);
            return metadata is null || metadata.SchemaVersion is <= 0 or > MinecraftInstanceMetadata.CurrentSchemaVersion ? new MinecraftInstanceMetadata() : metadata;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new MinecraftInstanceMetadata();
        }
    }

    private static async Task SaveCoreAsync(string path, MinecraftInstanceMetadata metadata, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Metadata path has no parent.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, MinecraftJsonContext.Default.MinecraftInstanceMetadata, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
            temporary = string.Empty;
        }
        finally
        {
            if (temporary.Length > 0)
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MinecraftInstanceMetadata))]
internal sealed partial class MinecraftJsonContext : JsonSerializerContext;
