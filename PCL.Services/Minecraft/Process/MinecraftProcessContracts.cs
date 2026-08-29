using System.Collections.Concurrent;
using System.Diagnostics;

using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Minecraft.Process;

public enum MinecraftProcessState
{
    Created,
    Running,
    Exited,
    Failed,
    Cancelled,
}

public sealed record MinecraftProcessSnapshot(
    Guid SessionId,
    string InstanceId,
    int ProcessId,
    MinecraftProcessState State,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public interface IMinecraftProcessPort
{
    ValueTask<System.Diagnostics.Process> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default);
}

public sealed class SystemMinecraftProcessPort : IMinecraftProcessPort
{
    public ValueTask<System.Diagnostics.Process> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        System.Diagnostics.Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Minecraft process could not be started.");
        return ValueTask.FromResult(process);
    }
}

public sealed class MinecraftProcessSession : IAsyncDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly object _gate = new();
    private MinecraftProcessSnapshot _snapshot;

    internal MinecraftProcessSession(System.Diagnostics.Process process, string instanceId, Guid sessionId, DateTimeOffset startedAt)
    {
        _process = process;
        _snapshot = new MinecraftProcessSnapshot(sessionId, instanceId, process.Id, MinecraftProcessState.Created, null, startedAt, null);
        _process.Exited += OnExited;
    }

    /// <summary>Marks the session running once the OS process is confirmed alive.</summary>
    public void MarkRunning()
    {
        lock (_gate)
        {
            if (_snapshot.State != MinecraftProcessState.Created) return;
            _snapshot = _snapshot with { State = MinecraftProcessState.Running };
        }
        Changed?.Invoke(Snapshot);
    }

    public MinecraftProcessSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public System.Diagnostics.Process Process => _process;
    public event Action<MinecraftProcessSnapshot>? Changed;

    public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_snapshot.State == MinecraftProcessState.Running) OnExited(this, EventArgs.Empty);
            return _snapshot.ExitCode ?? _process.ExitCode;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_snapshot.State != MinecraftProcessState.Running) return;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            _snapshot = _snapshot with { State = MinecraftProcessState.Cancelled, EndedAt = DateTimeOffset.UtcNow };
        }
        Changed?.Invoke(Snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        _process.Exited -= OnExited;
        if (!_process.HasExited)
        {
            // Dispose must not block for the lifetime of a running game; wait bounded, then
            // kill the tree so the handle cannot leak.
            Task exited = _process.WaitForExitAsync(System.Threading.CancellationToken.None);
            Task finished = await Task.WhenAny(exited, Task.Delay(3_000)).ConfigureAwait(false);
            if (finished != exited)
            {
                try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }

        _process.Dispose();
    }

    private void OnExited(object? sender, EventArgs args)
    {
        MinecraftProcessSnapshot updated;
        lock (_gate)
        {
            if (_snapshot.State != MinecraftProcessState.Running) return;
            int? exitCode = null;
            try { exitCode = _process.ExitCode; } catch (InvalidOperationException) { }
            updated = _snapshot with { State = exitCode == 0 ? MinecraftProcessState.Exited : MinecraftProcessState.Failed, ExitCode = exitCode, EndedAt = DateTimeOffset.UtcNow };
            _snapshot = updated;
        }
        Changed?.Invoke(updated);
    }
}

public sealed class MinecraftProcessService : IAsyncDisposable
{
    /// <summary>Finished sessions retained before pruning; older exits are removed.</summary>
    public const int RetainedExitedSessions = 32;

    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromHours(12);

    private readonly IMinecraftProcessPort _port;
    private readonly XsrStateStore? _store;
    private readonly XsrStateId _sessionsId;
    private readonly ConcurrentDictionary<Guid, MinecraftProcessSession> _sessions = new();

    public MinecraftProcessService(IMinecraftProcessPort? port = null, XsrStateStore? hostStore = null)
    {
        _port = port ?? new SystemMinecraftProcessPort();
        _store = hostStore;
        if (_store is not null)
        {
            MinecraftProcessStateComposition.DeclareState(new XsrStateStoreBuilder());
            _sessionsId = _store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        }
    }

    public async ValueTask<MinecraftProcessSession> StartAsync(Minecraft.Launch.MinecraftLaunchPlan plan, string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        System.Diagnostics.Process process = await _port.StartAsync(plan.ToStartInfo(), cancellationToken).ConfigureAwait(false);
        Guid sessionId = Guid.NewGuid();
        MinecraftProcessSession session = new(process, instanceId, sessionId, DateTimeOffset.UtcNow);
        session.Changed += snapshot => Publish(snapshot);
        _sessions[sessionId] = session;
        Publish(session.Snapshot);
        session.MarkRunning();
        Publish(session.Snapshot);
        return session;
    }

    public IReadOnlyList<MinecraftProcessSnapshot> ListSessions() => _sessions.Values.Select(static session => session.Snapshot).OrderBy(static snapshot => snapshot.StartedAt).ToArray();

    public bool TryGet(Guid sessionId, out MinecraftProcessSnapshot? snapshot)
    {
        if (_sessions.TryGetValue(sessionId, out MinecraftProcessSession? session)) { snapshot = session.Snapshot; return true; }
        snapshot = null;
        return false;
    }

    /// <summary>Cancels one session by id; returns false when unknown or already ended.</summary>
    public bool TryCancel(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out MinecraftProcessSession? session)) return false;
        session.Cancel();
        PruneStaleSessions();
        return true;
    }

    private void Publish(MinecraftProcessSnapshot snapshot)
    {
        if (_store is null) return;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            XsrCollectionSnapshot<MinecraftProcessSnapshot> current = _store.ReadCollection<MinecraftProcessSnapshot>(_sessionsId);
            Dictionary<Guid, MinecraftProcessSnapshot> merged = current.Items.ToDictionary(static item => item.SessionId);
            merged[snapshot.SessionId] = snapshot;
            int exited = merged.Values.Count(static item => item.State is not MinecraftProcessState.Created and not MinecraftProcessState.Running);
            List<Guid> removals = [];
            foreach (MinecraftProcessSnapshot item in merged.Values.OrderByDescending(static item => item.StartedAt))
            {
                bool isExited = item.State is not MinecraftProcessState.Created and not MinecraftProcessState.Running;
                if (isExited && exited > RetainedExitedSessions)
                {
                    removals.Add(item.SessionId);
                    exited--;
                }
            }

            List<MinecraftProcessSnapshot> upserts = merged.Values
                .Where(item => !removals.Contains(item.SessionId))
                .OrderBy(static item => item.StartedAt)
                .ToList();
            XsrCollectionApplyResult result = _store.PublishDelta(
                _sessionsId,
                new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(current.Revision, upserts, removals));
            if (result.IsApplied) return;
        }
    }

    private void PruneStaleSessions()
    {
        foreach (MinecraftProcessSession session in _sessions.Values)
        {
            if (session.Snapshot.State is MinecraftProcessState.Exited or MinecraftProcessState.Failed or MinecraftProcessState.Cancelled
                && session.Snapshot.EndedAt is { } ended
                && DateTimeOffset.UtcNow - ended > StaleSessionAge)
            {
                if (_sessions.TryRemove(session.Snapshot.SessionId, out MinecraftProcessSession? removed))
                {
                    removed.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (MinecraftProcessSession session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}

