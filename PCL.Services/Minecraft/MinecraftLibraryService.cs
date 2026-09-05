using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Services.Settings;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Minecraft;

public interface IMinecraftInstanceSource
{
    ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default);
}

public sealed record MinecraftLibraryDirectory(string Path, string SelectedInstanceId = "")
{
    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
}

public sealed record MinecraftLibrarySnapshot(
    long Revision,
    IReadOnlyList<MinecraftLibraryDirectory> Directories,
    string RootDirectory,
    IReadOnlyList<MinecraftInstanceDescriptor> Instances,
    string SelectedInstanceId,
    bool IsLoading,
    XsrError? Error = null)
{
    public MinecraftInstanceDescriptor? SelectedInstance => Instances.FirstOrDefault(instance => instance.Id == SelectedInstanceId);
}

internal sealed record MinecraftLibraryDocument(int SchemaVersion, string ActiveDirectory, MinecraftLibraryDirectory[] Directories);

[JsonSerializable(typeof(MinecraftLibraryDocument))]
internal sealed partial class MinecraftLibraryJsonContext : JsonSerializerContext;

/// <summary>Owns durable directory-qualified selection and cancellable installed-version discovery.</summary>
public sealed class MinecraftLibraryService : IDisposable
{
    public const string SettingKey = "MinecraftLibrary";
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("minecraft.library");
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly object _gate = new();
    private readonly SettingsService _settings;
    private readonly IMinecraftInstanceSource _source;
    private readonly XsrStateId _state;
    private MinecraftLibraryDocument _document;
    private MinecraftLibrarySnapshot _snapshot;
    private CancellationTokenSource? _scanCancellation;
    private long _generation;
    private bool _disposed;

    public MinecraftLibraryService(SettingsService settings, string defaultDirectory, IMinecraftInstanceSource source)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _state = settings.StateStore.Resolve(StateKey);
        string root = NormalizeDirectory(defaultDirectory);
        _document = Load(root);
        _snapshot = new(0, Array.AsReadOnly(_document.Directories), _document.ActiveDirectory, [], "", false);
        Publish(_snapshot);
    }

    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<MinecraftLibrarySnapshot>(StateKey, "PCL.Services.Minecraft.Library");

    public static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute Minecraft directory is required.", nameof(path));
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    public Task<XsrResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            long generation = ++_generation;
            string root = _document.ActiveDirectory;
            Publish(_snapshot with { IsLoading = true, Error = null });
            return ScanAsync(root, generation, _scanCancellation.Token);
        }
    }

    public async Task<XsrResult> ChangeDirectoryAsync(string path, bool add, CancellationToken cancellationToken = default)
    {
        string root;
        try { root = NormalizeDirectory(path); }
        catch (ArgumentException exception) { return XsrResult.Failure(MinecraftErrors.InvalidRequest(exception.Message)); }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            MinecraftLibraryDirectory? existing = _document.Directories.FirstOrDefault(item => PathComparer.Equals(item.Path, root));
            if (existing is null && !add) return XsrResult.Failure(MinecraftErrors.InvalidRequest("the directory is not registered."));
            if (existing is null && !Directory.Exists(root)) return XsrResult.Failure(MinecraftErrors.InvalidRequest("the directory does not exist or cannot be accessed."));
            MinecraftLibraryDirectory[] directories = existing is null ? [.. _document.Directories, new(root)] : _document.Directories;
            root = existing?.Path ?? root;
            XsrResult saved = Save(_document with { ActiveDirectory = root, Directories = directories });
            if (!saved.IsSuccess) return saved;
            InvalidateScan();
            Publish(new(_snapshot.Revision, Array.AsReadOnly(directories), root, [], "", true));
        }
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<XsrResult> ForgetDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        bool refresh;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            MinecraftLibraryDirectory[] remaining = [.. _document.Directories.Where(item => !PathComparer.Equals(item.Path, path))];
            if (remaining.Length == _document.Directories.Length || remaining.Length == 0)
                return XsrResult.Failure(MinecraftErrors.InvalidRequest("keep at least one registered directory."));
            refresh = PathComparer.Equals(_document.ActiveDirectory, path);
            string root = refresh ? remaining[0].Path : _document.ActiveDirectory;
            XsrResult saved = Save(_document with { Directories = remaining, ActiveDirectory = root });
            if (!saved.IsSuccess) return saved;
            if (refresh) InvalidateScan();
            Publish(_snapshot with
            {
                Directories = Array.AsReadOnly(remaining),
                RootDirectory = root,
                Instances = refresh ? [] : _snapshot.Instances,
                SelectedInstanceId = refresh ? "" : _snapshot.SelectedInstanceId,
                IsLoading = refresh
            });
        }
        return refresh ? await RefreshAsync(cancellationToken).ConfigureAwait(false) : XsrResult.Success();
    }

    public XsrResult SelectInstance(string root, string instanceId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PathComparer.Equals(root, _document.ActiveDirectory) || !_snapshot.Instances.Any(instance => instance.Id == instanceId))
                return XsrResult.Failure(MinecraftErrors.InstanceNotFound(instanceId));
            XsrResult saved = Remember(instanceId);
            if (saved.IsSuccess) Publish(_snapshot with { SelectedInstanceId = instanceId, Directories = Array.AsReadOnly(_document.Directories), Error = null });
            return saved;
        }
    }

    private async Task<XsrResult> ScanAsync(string root, long generation, CancellationToken cancellationToken)
    {
        IReadOnlyList<MinecraftInstanceDescriptor> instances = [];
        XsrError? error = null;
        try
        {
            if (!Directory.Exists(root)) throw new IOException("The Minecraft directory is unavailable.");
            instances = await _source.DiscoverAsync(root, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (!_disposed && generation == _generation) Publish(_snapshot with { IsLoading = false });
            }
            return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            error = MinecraftErrors.InvalidRequest("the directory could not be scanned.");
        }
        lock (_gate)
        {
            if (_disposed || generation != _generation || cancellationToken.IsCancellationRequested)
                return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
            int rememberedIndex = Array.FindIndex(_document.Directories, item => PathComparer.Equals(item.Path, root));
            string remembered = _document.Directories[rememberedIndex].SelectedInstanceId;
            string selected = remembered.Length > 0 && instances.Any(instance => instance.Id == remembered) ? remembered : instances.Count > 0 ? instances[0].Id : "";
            if (error is null && selected.Length > 0 && selected != remembered)
            {
                XsrResult saved = Remember(selected);
                if (!saved.IsSuccess) { error = saved.Error; selected = ""; }
            }
            Publish(new(_snapshot.Revision, Array.AsReadOnly(_document.Directories), root,
                Array.AsReadOnly(instances.ToArray()), selected, false, error));
            return error is null ? XsrResult.Success() : XsrResult.Failure(error);
        }
    }

    private XsrResult Remember(string id) => Save(_document with
    {
        Directories = [.. _document.Directories.Select(item => PathComparer.Equals(item.Path, _document.ActiveDirectory) ? item with { SelectedInstanceId = id } : item)],
    });

    private XsrResult Save(MinecraftLibraryDocument document)
    {
        XsrResult result = _settings.SetValue(SettingKey, JsonSerializer.Serialize(document, MinecraftLibraryJsonContext.Default.MinecraftLibraryDocument));
        if (result.IsSuccess) _document = document;
        return result;
    }

    private MinecraftLibraryDocument Load(string fallback)
    {
        XsrResult<string> setting = _settings.GetValue<string>(SettingKey);
        if (setting.IsSuccess && !string.IsNullOrWhiteSpace(setting.Value))
        {
            try
            {
                MinecraftLibraryDocument? document = JsonSerializer.Deserialize(setting.Value, MinecraftLibraryJsonContext.Default.MinecraftLibraryDocument);
                if (document is { SchemaVersion: 1, Directories.Length: > 0 })
                {
                    MinecraftLibraryDirectory[] directories = [.. document.Directories
                        .Select(item => new MinecraftLibraryDirectory(NormalizeDirectory(item.Path), item.SelectedInstanceId ?? ""))
                        .DistinctBy(item => item.Path, PathComparer)];
                    string active = directories.FirstOrDefault(item => PathComparer.Equals(item.Path, document.ActiveDirectory))?.Path ?? directories[0].Path;
                    return new(1, active, directories);
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException) { }
        }
        return new(1, fallback, [new(fallback)]);
    }

    private void Publish(MinecraftLibrarySnapshot snapshot)
    {
        _snapshot = snapshot with { Revision = _snapshot.Revision + 1 };
        _settings.StateStore.Publish(_state, _snapshot);
    }

    private void InvalidateScan() { ++_generation; _scanCancellation?.Cancel(); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            InvalidateScan();
            _scanCancellation?.Dispose();
        }
    }
}

public sealed record MinecraftLibraryRefreshCommand;
public sealed record MinecraftLibraryDirectoryCommand(string Path, bool Add = false);
public sealed record MinecraftLibraryForgetCommand(string Path);
public sealed record MinecraftLibrarySelectCommand(string RootDirectory, string InstanceId);

public static class MinecraftLibraryRoutes
{
    public static readonly XsrSemanticId Refresh = XsrSemanticId.Parse("minecraft.library.refresh");
    public static readonly XsrSemanticId Directory = XsrSemanticId.Parse("minecraft.library.directory");
    public static readonly XsrSemanticId Forget = XsrSemanticId.Parse("minecraft.library.forget");
    public static readonly XsrSemanticId Select = XsrSemanticId.Parse("minecraft.library.select");
}
