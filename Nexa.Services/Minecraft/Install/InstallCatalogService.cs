using System.Collections.ObjectModel;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Install;

/// <summary>Pure legacy availability gates. Catalog metadata makes the final support decision.</summary>
public static partial class InstallCompatibility
{
    public static string? UnavailableReason(InstallLoader loader, string game)
    {
        string numeric = game.Split('-', 2)[0];
        string[] parts = numeric.Split('.');
        bool parsed = parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _);
        int major = parsed ? int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) : 0;
        int minor = parsed ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        // Unknown snapshots are resolved by metadata, rather than declared incompatible.
        int drop = major == 1 ? minor * 10 : major >= 25 ? major * 10 + minor : 0;
        bool allowed = loader switch
        {
            InstallLoader.Cleanroom => game.Equals("1.12.2", StringComparison.OrdinalIgnoreCase),
            InstallLoader.Forge => game.StartsWith("1.", StringComparison.Ordinal) || major > 25,
            InstallLoader.LiteLoader => drop < 130,
            InstallLoader.NeoForge => drop == 0 || drop >= 200,
            InstallLoader.Fabric => drop == 0 || drop > 130,
            InstallLoader.LegacyFabric => drop <= 130,
            InstallLoader.Quilt => drop == 0 || drop >= 140,
            InstallLoader.LabyMod => drop == 0 || drop >= 80,
            _ => true,
        };
        if (loader == InstallLoader.Quilt && numeric.StartsWith("1.14.", StringComparison.Ordinal)
            && Version.TryParse(numeric, out Version? quiltGame)) allowed = quiltGame >= new Version(1, 14, 4);
        return allowed ? null : $"{loader} 不支持 Minecraft {game}。";
    }
    public static bool IsAddon(InstallLoader loader) => loader is InstallLoader.FabricApi or InstallLoader.Qsl or InstallLoader.OptiFabric;
    public static bool CanCombine(InstallLoader first, InstallLoader second, string game) => first == second
        || first == InstallLoader.Fabric && second is InstallLoader.OptiFine or InstallLoader.FabricApi or InstallLoader.OptiFabric
        || second == InstallLoader.Fabric && first is InstallLoader.OptiFine or InstallLoader.FabricApi or InstallLoader.OptiFabric
        || first == InstallLoader.OptiFine && second is InstallLoader.OptiFabric or InstallLoader.FabricApi
        || second == InstallLoader.OptiFine && first is InstallLoader.OptiFabric or InstallLoader.FabricApi
        || first == InstallLoader.FabricApi && second == InstallLoader.OptiFabric
        || second == InstallLoader.FabricApi && first == InstallLoader.OptiFabric
        || first == InstallLoader.Quilt && second == InstallLoader.Qsl || second == InstallLoader.Quilt && first == InstallLoader.Qsl
        || first == InstallLoader.OptiFine && CanCombineWithOptiFine(second, game)
        || second == InstallLoader.OptiFine && CanCombineWithOptiFine(first, game);
    public static bool CanCombineWithOptiFine(InstallLoader loader, string game) =>
        loader == InstallLoader.Cleanroom && game == "1.12.2" || loader == InstallLoader.LiteLoader || loader == InstallLoader.Forge
        && (!Version.TryParse(game.Split('-', 2)[0], out Version? version)
            || version < new Version(1, 13) || version > new Version(1, 14, 3));
}

/// <summary>Background per-catalog acquisition; immutable aggregate publications never drop sibling results.</summary>
public sealed partial class InstallCatalogService : IDisposable
{
    private readonly XsrStateStore _store;
    private readonly XsrStateId _state;
    private readonly IInstallCatalogSource _source;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly Dictionary<(string, InstallLoader?), IReadOnlyList<InstallCatalogVersion>> _cache = [];
    private readonly Dictionary<InstallLoader, InstallCatalogSnapshot> _loaders = [];
    private readonly Dictionary<(string, InstallLoader?), Request> _pending = [];
    private InstallCatalogSnapshot _games = new(0, "", null, [], false);
    private string _game = "";
    private long _revision;
    private bool _disposed;
    private sealed class Request(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<XsrResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public InstallCatalogService(XsrStateStore store, IInstallCatalogSource source)
    {
        _store = store; _source = source; _state = store.Resolve(InstallCatalogStateContract.StateKey);
        Publish(_games);
    }
    private void SelectGame(string game)
    {
        if (_game == game) return;
        _game = game; _loaders.Clear();
        foreach (var key in _pending.Keys.Where(key => key.Item2 is not null).ToArray())
        {
            _pending[key].Cancellation.Cancel(); _pending.Remove(key);
        }
    }
    public Task<XsrResult> PrefetchAsync(InstallCatalogPrefetchCommand command, CancellationToken token)
    {
        List<Task<XsrResult>> tasks = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SelectGame(command.GameVersion);
            Publish(_games);
            if (command.GameVersion.Length > 0)
                foreach (InstallLoader loader in Enum.GetValues<InstallLoader>())
                {
                    InstallLoader baseLoader = loader switch { InstallLoader.FabricApi or InstallLoader.OptiFabric => InstallLoader.Fabric, InstallLoader.Qsl => InstallLoader.Quilt, _ => loader };
                    if (InstallCompatibility.UnavailableReason(baseLoader, command.GameVersion) is null)
                        tasks.Add(ReadAsync(new(command.GameVersion, loader), token));
                }
        }
        return CompleteAll(tasks);
    }
    private static async Task<XsrResult> CompleteAll(List<Task<XsrResult>> tasks)
    {
        await Task.WhenAll(tasks).ConfigureAwait(false); return XsrResult.Success();
    }
    public Task<XsrResult> ReadAsync(InstallCatalogReadCommand command, CancellationToken token)
    {
        string game = command.Loader is null ? "" : command.GameVersion.Trim();
        var key = (game, command.Loader);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (command.Loader is not null) SelectGame(game);
            if (!command.Refresh && _pending.TryGetValue(key, out Request? pending)) return pending.Completion.Task;
            if (_pending.Remove(key, out Request? old)) old.Cancellation.Cancel();
            if (!command.Refresh && _cache.TryGetValue(key, out IReadOnlyList<InstallCatalogVersion>? cached))
            {
                Publish(new(++_revision, game, command.Loader, cached, false)); return Task.FromResult(XsrResult.Success());
            }
            if (command.Loader is { } loader && (game.Length == 0 || InstallCompatibility.UnavailableReason(loader, game) is not null))
            {
                Publish(new(++_revision, game, loader, [], false, Unsupported: game.Length == 0 ? "请先选择 Minecraft 版本。" : InstallCompatibility.UnavailableReason(loader, game)));
                return Task.FromResult(XsrResult.Success());
            }
            Request request = new(CancellationTokenSource.CreateLinkedTokenSource(token)); _pending[key] = request;
            Publish(new(++_revision, game, command.Loader, [], true));
            // Queue provider invocation too: even a synchronously completing HTTP/cache/parser cannot occupy the UI thread.
            _ = Task.Run(() => FetchAsync(key, request), CancellationToken.None);
            return request.Completion.Task;
        }
    }
    private async Task FetchAsync((string Game, InstallLoader? Loader) key, Request request)
    {
        CancellationToken token = request.Cancellation.Token;
        bool entered = false;
        try
        {
            await _concurrency.WaitAsync(token).ConfigureAwait(false); entered = true;
            IReadOnlyList<InstallCatalogVersion> versions = key.Loader is { } loader
                ? await _source.GetLoadersAsync(loader, key.Game, token).ConfigureAwait(false)
                : await _source.GetGamesAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            versions = Array.AsReadOnly(versions.Where(v => !string.IsNullOrWhiteSpace(v.Id)).DistinctBy(v => v.Id, StringComparer.Ordinal).ToArray());
            lock (_gate) if (Current())
            {
                if (_cache.Count >= 32) _cache.Clear();
                _cache[key] = versions;
                Publish(new(++_revision, key.Game, key.Loader, versions, false));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            lock (_gate) if (Current()) Publish(new(++_revision, key.Game, key.Loader, [], false,
                Error: error is OperationCanceledException ? "请求已取消。" : error.Message));
        }
        finally
        {
            if (entered) _concurrency.Release();
            lock (_gate)
            {
                if (_pending.TryGetValue(key, out Request? current) && ReferenceEquals(current, request)) _pending.Remove(key);
                request.Cancellation.Dispose();
            }
            request.Completion.TrySetResult(XsrResult.Success());
        }
        bool Current() => !_disposed && _pending.TryGetValue(key, out Request? current) && ReferenceEquals(current, request);
    }
    private void Publish(InstallCatalogSnapshot snapshot)
    {
        if (snapshot.Loader is { } loader) _loaders[loader] = snapshot; else _games = snapshot;
        _store.Publish(_state, new InstallCatalogState(++_revision, _game,
            Array.AsReadOnly(new[] { _games }.Concat(_loaders.OrderBy(pair => pair.Key).Select(pair => pair.Value)).ToArray())));
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Request request in _pending.Values) request.Cancellation.Cancel();
            _pending.Clear(); _cache.Clear();
        }
    }
}
