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
    private readonly MinecraftProcessSnapshot _createdSnapshot;

    internal MinecraftProcessSession(System.Diagnostics.Process process, string instanceId, Guid sessionId, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        _process = process;
        _snapshot = new MinecraftProcessSnapshot(sessionId, instanceId, process.Id, MinecraftProcessState.Created, null, startedAt, null);
        _createdSnapshot = _snapshot;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
    }

    /// <summary>
    /// Completes the Created phase without a Created-to-Running race. The process is checked both
    /// before and immediately after the Running transition; an already dead JVM is published as
    /// Exited/Failed and never becomes a permanently stale Running session.
    /// </summary>
    public void StartLifecycle()
    {
        bool exited;
        try { exited = _process.HasExited; }
        catch (InvalidOperationException) { exited = true; }
        if (exited)
        {
            CompleteExit();
            return;
        }

        lock (_gate)
        {
            if (_snapshot.State != MinecraftProcessState.Created) return;
            _snapshot = _snapshot with { State = MinecraftProcessState.Running };
        }
        Changed?.Invoke(Snapshot);

        try
        {
            if (_process.HasExited) CompleteExit();
        }
        catch (InvalidOperationException)
        {
            CompleteExit();
        }
    }

    public MinecraftProcessSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    internal MinecraftProcessSnapshot CreatedSnapshot => _createdSnapshot;
    public System.Diagnostics.Process Process => _process;
    public event Action<MinecraftProcessSnapshot>? Changed;

    public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        CompleteExit();
        MinecraftProcessSnapshot snapshot = Snapshot;
        return snapshot.ExitCode ?? 0;
    }

    public void Cancel()
    {
        bool hasExited;
        try { hasExited = _process.HasExited; }
        catch (InvalidOperationException) { hasExited = true; }
        if (hasExited)
        {
            CompleteExit();
            return;
        }

        MinecraftProcessSnapshot? changed = null;
        lock (_gate)
        {
            if (_snapshot.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running)) return;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            _snapshot = _snapshot with { State = MinecraftProcessState.Cancelled, EndedAt = DateTimeOffset.UtcNow };
            changed = _snapshot;
        }
        if (changed is { } snapshot) Changed?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        _process.Exited -= OnExited;
        bool hasExited = false;
        try { hasExited = _process.HasExited; } catch (InvalidOperationException) { }
        if (!hasExited)
        {
            // Dispose is a lifecycle boundary, not a request to wait for a user's game forever.
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Task exited = _process.WaitForExitAsync(System.Threading.CancellationToken.None);
            _ = await Task.WhenAny(exited, Task.Delay(3_000)).ConfigureAwait(false);
        }

        _process.Dispose();
    }

    private void OnExited(object? sender, EventArgs args)
    {
        CompleteExit();
    }

    private void CompleteExit()
    {
        MinecraftProcessSnapshot? updated = null;
        lock (_gate)
        {
            if (_snapshot.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running)) return;
            int? exitCode = null;
            try { exitCode = _process.ExitCode; } catch (InvalidOperationException) { }
            updated = _snapshot with { State = exitCode == 0 ? MinecraftProcessState.Exited : MinecraftProcessState.Failed, ExitCode = exitCode, EndedAt = DateTimeOffset.UtcNow };
            _snapshot = updated;
        }
        if (updated is { } snapshot) Changed?.Invoke(snapshot);
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
            _sessionsId = _store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        }
    }

    /// <summary>The host state store receiving process lifecycle snapshots, when composed.</summary>
    public XsrStateStore? StateStore => _store;

    public async ValueTask<MinecraftProcessSession> StartAsync(Minecraft.Launch.MinecraftLaunchPlan plan, string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        System.Diagnostics.Process process = await _port.StartAsync(plan.ToStartInfo(), cancellationToken).ConfigureAwait(false);
        Guid sessionId = Guid.NewGuid();
        MinecraftProcessSession session = new(process, instanceId, sessionId, DateTimeOffset.UtcNow);
        session.Changed += OnSessionChanged;
        _sessions[sessionId] = session;
        // Publish the Created observation even if the child exited between Process.Start and
        // event subscription; the terminal snapshot follows immediately in that case.
        Publish(session.CreatedSnapshot);
        if (session.Snapshot.State != MinecraftProcessState.Created) Publish(session.Snapshot);
        session.StartLifecycle();
        PruneSessions();
        return session;
    }

    public IReadOnlyList<MinecraftProcessSnapshot> ListSessions()
    {
        PruneSessions();
        return _sessions.Values.Select(static session => session.Snapshot).OrderBy(static snapshot => snapshot.StartedAt).ToArray();
    }

    public bool TryGet(Guid sessionId, out MinecraftProcessSnapshot? snapshot)
    {
        PruneSessions();
        if (_sessions.TryGetValue(sessionId, out MinecraftProcessSession? session)) { snapshot = session.Snapshot; return true; }
        snapshot = null;
        return false;
    }

    /// <summary>Cancels one session by id; returns false when unknown or already ended.</summary>
    public bool TryCancel(Guid sessionId)
    {
        PruneSessions();
        if (!_sessions.TryGetValue(sessionId, out MinecraftProcessSession? session)) return false;
        if (session.Snapshot.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running)) return false;
        session.Cancel();
        return session.Snapshot.State is MinecraftProcessState.Cancelled or MinecraftProcessState.Exited or MinecraftProcessState.Failed;
    }

    private void OnSessionChanged(MinecraftProcessSnapshot snapshot)
    {
        Publish(snapshot);
        if (snapshot.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running))
            PruneSessions();
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

    private void PruneSessions()
    {
        MinecraftProcessSession[] completed = _sessions.Values
            .Where(static session => session.Snapshot.State is MinecraftProcessState.Exited or MinecraftProcessState.Failed or MinecraftProcessState.Cancelled)
            .OrderBy(static session => session.Snapshot.EndedAt ?? session.Snapshot.StartedAt)
            .ToArray();
        DateTimeOffset staleBefore = DateTimeOffset.UtcNow - StaleSessionAge;
        int keep = Math.Min(RetainedExitedSessions, completed.Length);
        foreach (MinecraftProcessSession session in completed)
        {
            MinecraftProcessSnapshot snapshot = session.Snapshot;
            bool overRetention = Array.IndexOf(completed, session) < completed.Length - keep;
            bool stale = snapshot.EndedAt is { } ended && ended < staleBefore;
            if (overRetention || stale)
            {
                if (_sessions.TryRemove(snapshot.SessionId, out MinecraftProcessSession? removed))
                {
                    removed.Changed -= OnSessionChanged;
                    RemovePublished(snapshot.SessionId);
                    removed.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
    }

    private void RemovePublished(Guid sessionId)
    {
        if (_store is null) return;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            XsrCollectionSnapshot<MinecraftProcessSnapshot> current = _store.ReadCollection<MinecraftProcessSnapshot>(_sessionsId);
            if (!current.Items.Any(item => item.SessionId == sessionId)) return;
            XsrCollectionApplyResult result = _store.PublishDelta(
                _sessionsId,
                new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(current.Revision, [], [sessionId]));
            if (result.IsApplied) return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (MinecraftProcessSession session in _sessions.Values)
        {
            session.Changed -= OnSessionChanged;
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}
