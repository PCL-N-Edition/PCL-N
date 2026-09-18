using System.Text.Json;
using System.Text.Json.Serialization;
using Nexa.Services.Settings;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft;

public interface IMinecraftInstanceSource
{
    ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default);
}

public sealed record MinecraftLibraryDirectory(string Path, string SelectedInstanceId = "", string Name = "")
{
    [JsonIgnore]
    public bool IsOfficial => MinecraftLibraryService.PathComparer.Equals(Path, MinecraftLibraryService.OfficialDirectory);
    [JsonIgnore]
    public string DisplayName => IsOfficial
        ? "官方文件夹"
        : string.IsNullOrWhiteSpace(Name)
            ? LeafName()
            : Name;

    private string LeafName()
    {
        string trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string leaf = System.IO.Path.GetFileName(trimmed);
        return leaf.Length == 0 ? Path : leaf;
    }
    [JsonIgnore]
    public bool HasName => IsOfficial || !string.IsNullOrWhiteSpace(Name);
}

public sealed record MinecraftLibrarySnapshot(long Revision, IReadOnlyList<MinecraftLibraryDirectory> Directories,
    string RootDirectory, IReadOnlyList<MinecraftInstanceDescriptor> Instances, string SelectedInstanceId,
    bool IsLoading, XsrError? Error = null)
{
    public MinecraftInstanceDescriptor? SelectedInstance => Instances.FirstOrDefault(instance => instance.Id == SelectedInstanceId);
}

internal sealed record MinecraftLibraryDocument(int SchemaVersion, string ActiveDirectory, MinecraftLibraryDirectory[] Directories);
[JsonSerializable(typeof(MinecraftLibraryDocument))]
internal sealed partial class MinecraftLibraryJsonContext : JsonSerializerContext;

/// <summary>Durable directory-qualified selection and generation-safe discovery over existing capabilities.</summary>
public sealed class MinecraftLibraryService : IDisposable
{
    public const string SettingKey = "MinecraftLibrary";
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("minecraft.library");
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string OfficialDirectory { get; } = NormalizeDirectory(new Files.DefaultMinecraftRootProvider().ResolveRoot());
    private readonly object _gate = new();
    private readonly SettingsService _settings;
    private readonly IMinecraftInstanceSource _source;
    private readonly XsrStateId _state;
    private MinecraftLibraryDocument _document;
    private MinecraftLibrarySnapshot _snapshot;
    private CancellationTokenSource? _scanCancellation;
    private long _generation;
    private bool _disposed;
    private XsrError? _configurationError;

    public MinecraftLibraryService(SettingsService settings, string defaultDirectory, IMinecraftInstanceSource source)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _state = settings.StateStore.Resolve(StateKey);
        _document = Load(NormalizeDirectory(defaultDirectory));
        _snapshot = new(0, Array.AsReadOnly(_document.Directories), _document.ActiveDirectory, [], "", false, _configurationError);
        Publish(_snapshot);
    }

    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<MinecraftLibrarySnapshot>(StateKey, "Nexa.Services.Minecraft.Library");
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
            return BeginScanLocked(_document.ActiveDirectory, cancellationToken);
        }
    }

    public async Task<XsrResult> ChangeDirectoryAsync(string path, bool add, CancellationToken cancellationToken = default)
    {
        string root;
        try { root = NormalizeDirectory(path); }
        catch (ArgumentException exception) { return XsrResult.Failure(MinecraftErrors.InvalidRequest(exception.Message)); }
        Task<XsrResult> scan;
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
            // Commit and scan replacement share this critical section: a scan completing for
            // the old root must see the bumped generation BEFORE it can publish again, or it
            // would resurrect the old root and persist its selection into the new one.
            scan = BeginScanLocked(root, cancellationToken);
        }
        return await scan.ConfigureAwait(false);
    }

    public async Task<XsrResult> ForgetDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        // The X command contract accepts any path spelling; registration always stores
        // normalized forms, so the forget must normalize before comparing.
        string normalized;
        try { normalized = NormalizeDirectory(path); }
        catch (ArgumentException exception) { return XsrResult.Failure(MinecraftErrors.InvalidRequest(exception.Message)); }
        bool refresh;
        Task<XsrResult>? scanTask = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            MinecraftLibraryDirectory[] remaining = [.. _document.Directories.Where(item => !PathComparer.Equals(item.Path, normalized))];
            if (remaining.Length == _document.Directories.Length || remaining.Length == 0)
                return XsrResult.Failure(MinecraftErrors.InvalidRequest("keep at least one registered directory."));
            refresh = PathComparer.Equals(_document.ActiveDirectory, normalized);
            string root = refresh ? remaining[0].Path : _document.ActiveDirectory;
            XsrResult saved = Save(_document with { Directories = remaining, ActiveDirectory = root });
            if (!saved.IsSuccess) return saved;
            if (refresh)
            {
                scanTask = BeginScanLocked(root, cancellationToken);
            }
            else
            {
                Publish(_snapshot with
                {
                    Directories = Array.AsReadOnly(remaining),
                    RootDirectory = root,
                    Instances = _snapshot.Instances,
                    SelectedInstanceId = _snapshot.SelectedInstanceId,
                    IsLoading = false
                });
            }
        }

        if (scanTask is not null)
        {
            return await scanTask.ConfigureAwait(false);
        }

        return XsrResult.Success();
    }

    public async Task<XsrResult> DeleteInstanceAsync(string root, string instanceId, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            Task<XsrResult> refresh;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (!PathComparer.Equals(root, _document.ActiveDirectory) || !_snapshot.Instances.Any(item => item.Id == instanceId)
                    || string.IsNullOrWhiteSpace(instanceId) || instanceId is "." or ".." || instanceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || instanceId.Contains('/') || instanceId.Contains('\\'))
                    return XsrResult.Failure(MinecraftErrors.InvalidRequest("版本路径无效。"));
                if (_settings.StateStore.TryResolve(Process.MinecraftProcessStateComposition.SessionsKey, out var sessions)
                    && _settings.StateStore.ReadCollection<Process.MinecraftProcessSnapshot>(sessions).Items.Any(item => item.InstanceId == instanceId
                        && item.State is Process.MinecraftProcessState.Created or Process.MinecraftProcessState.Running))
                    return XsrResult.Failure(MinecraftErrors.InvalidRequest("请先结束此版本的游戏进程。"));
                try
                {
                    string versions = Path.Combine(root, "versions");
                    string source = Path.Combine(versions, instanceId);
                    string recycle = Path.Combine(root, ".recycle");
                    foreach (string path in new[] { root, versions, source, recycle })
                        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            return XsrResult.Failure(MinecraftErrors.InvalidRequest("不能移除链接目录中的版本。"));
                    Directory.CreateDirectory(recycle);
                    Directory.Move(source, Path.Combine(recycle, instanceId + "-" + Guid.NewGuid().ToString("N")));
                    refresh = BeginScanLocked(root, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { return XsrResult.Failure(MinecraftErrors.InvalidRequest(ex.Message)); }
            }
            return await refresh.ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public XsrResult SelectInstance(string root, string instanceId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!PathComparer.Equals(root, _document.ActiveDirectory) || !_snapshot.Instances.Any(instance => instance.Id == instanceId))
                return XsrResult.Failure(MinecraftErrors.InstanceNotFound(instanceId));
            XsrResult saved = Remember(_document.ActiveDirectory, instanceId);
            if (saved.IsSuccess) Publish(_snapshot with { SelectedInstanceId = instanceId, Directories = Array.AsReadOnly(_document.Directories), Error = null });
            return saved;
        }
    }

    public XsrResult RenameDirectory(string path, string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MinecraftLibraryDirectory? directory = _document.Directories.FirstOrDefault(item => PathComparer.Equals(item.Path, path));
            if (directory is null || directory.IsOfficial)
                return XsrResult.Failure(MinecraftErrors.InvalidRequest("the directory name cannot be changed."));
            name = name.Trim();
            if (name.Length > 80 || name.Any(char.IsControl))
                return XsrResult.Failure(MinecraftErrors.InvalidRequest("the directory name must be at most 80 characters without control characters."));
            XsrResult saved = Save(_document with { Directories = [.. _document.Directories.Select(item => item == directory ? item with { Name = name } : item)] });
            if (saved.IsSuccess) Publish(_snapshot with { Directories = Array.AsReadOnly(_document.Directories) });
            return saved;
        }
    }

    /// <summary>
    /// Replaces the in-flight scan with one for <paramref name="root"/>. MUST be called while
    /// holding <see cref="_gate"/>: cancelling the old scan, bumping the generation, publishing
    /// the loading truth, and creating the new scan are one atomic step, so a completing old
    /// scan can never slip between the commit and the invalidation.
    /// </summary>
    private Task<XsrResult> BeginScanLocked(string root, CancellationToken cancellationToken)
    {
        _scanCancellation?.Cancel(); _scanCancellation?.Dispose();
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long generation = ++_generation;
        // A re-scan of the SAME root keeps the live selection visible while loading; a switch
        // to a new root clears it (the old root's selection does not apply there).
        string remembered = PathComparer.Equals(root, _snapshot.RootDirectory)
            ? _snapshot.SelectedInstanceId
            : "";
        Publish(new(
            _snapshot.Revision,
            Array.AsReadOnly(_document.Directories),
            root,
            [],
            remembered,
            true));
        return ScanAsync(root, generation, _scanCancellation.Token);
    }

    private async Task<XsrResult> ScanAsync(string root, long generation, CancellationToken cancellationToken)
    {
        IReadOnlyList<MinecraftInstanceDescriptor> instances = [];
        XsrError? error = _configurationError;
        try
        {
            if (!Directory.Exists(root)) throw new IOException("The Minecraft directory is unavailable.");
            instances = await _source.DiscoverAsync(root, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate) { if (!_disposed && generation == _generation) Publish(_snapshot with { IsLoading = false }); }
            return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { error = MinecraftErrors.InvalidRequest("the directory could not be scanned."); }
        lock (_gate)
        {
            if (_disposed || generation != _generation)
                return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
            if (cancellationToken.IsCancellationRequested)
            {
                Publish(_snapshot with { IsLoading = false });
                return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
            }
            // Keyed to the SCANNED root: the active directory may have switched while this
            // scan ran, and persisting against ActiveDirectory would cross-pollute.
            string remembered = _document.Directories.First(item => PathComparer.Equals(item.Path, root)).SelectedInstanceId;
            string selected = instances.Any(instance => instance.Id == remembered) ? remembered : instances.Count > 0 ? instances[0].Id : "";
            if (error is null && selected.Length > 0 && selected != remembered)
            {
                XsrResult saved = Remember(root, selected);
                if (!saved.IsSuccess) { error = saved.Error; selected = ""; }
            }
            if (error is not null) selected = "";
            Publish(new(_snapshot.Revision, Array.AsReadOnly(_document.Directories), root, Array.AsReadOnly(instances.ToArray()), selected, false, error));
            return error is null ? XsrResult.Success() : XsrResult.Failure(error);
        }
    }

    private XsrResult Remember(string root, string id) => Save(_document with
    { Directories = [.. _document.Directories.Select(item => PathComparer.Equals(item.Path, root) ? item with { SelectedInstanceId = id } : item)] });
    private XsrResult Save(MinecraftLibraryDocument document)
    {
        if (_configurationError is not null) return XsrResult.Failure(_configurationError);
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
                if (document is not { SchemaVersion: 1, Directories.Length: > 0 } || document.Directories.Any(item => item is null)) throw new JsonException();
                MinecraftLibraryDirectory[] directories = [.. document.Directories.Select(item =>
                    new MinecraftLibraryDirectory(NormalizeDirectory(item.Path), item.SelectedInstanceId ?? "", item.Name?.Trim() ?? "")).DistinctBy(item => item.Path, PathComparer)];
                // A hand-edited or pre-normalization document may carry a non-canonical
                // active path; normalize before matching so it still resolves.
                string normalizedActive;
                try { normalizedActive = NormalizeDirectory(document.ActiveDirectory); }
                catch (ArgumentException) { normalizedActive = directories[0].Path; }
                string active = directories.FirstOrDefault(item => PathComparer.Equals(item.Path, normalizedActive))?.Path ?? directories[0].Path;
                return new(1, active, directories);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
            { _configurationError = SettingsErrors.InvalidValue(SettingKey, "the saved library document is invalid or unsupported."); }
        }
        return new(1, fallback, [new(fallback)]);
    }
    private void Publish(MinecraftLibrarySnapshot snapshot)
    { _snapshot = snapshot with { Revision = _snapshot.Revision + 1 }; _settings.StateStore.Publish(_state, _snapshot); }
    private void InvalidateScan() { ++_generation; _scanCancellation?.Cancel(); }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; InvalidateScan(); _scanCancellation?.Dispose(); }
    }
}

public sealed record MinecraftLibraryRefreshCommand;
public sealed record MinecraftLibraryDirectoryCommand(string Path, bool Add = false);
public sealed record MinecraftLibraryForgetCommand(string Path);
public sealed record MinecraftLibraryDeleteCommand(string RootDirectory, string InstanceId);
public sealed record MinecraftLibrarySelectCommand(string RootDirectory, string InstanceId);
public sealed record MinecraftLibraryRenameCommand(string Path, string Name);
public static class MinecraftLibraryRoutes
{
    public static readonly XsrSemanticId Refresh = XsrSemanticId.Parse("minecraft.library.refresh");
    public static readonly XsrSemanticId Directory = XsrSemanticId.Parse("minecraft.library.directory");
    public static readonly XsrSemanticId Forget = XsrSemanticId.Parse("minecraft.library.forget");
    public static readonly XsrSemanticId Delete = XsrSemanticId.Parse("minecraft.library.delete");
    public static readonly XsrSemanticId Select = XsrSemanticId.Parse("minecraft.library.select");
    public static readonly XsrSemanticId Rename = XsrSemanticId.Parse("minecraft.library.rename");
}
