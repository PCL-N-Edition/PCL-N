using System.Collections.Concurrent;
using System.Diagnostics;

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
        _snapshot = new MinecraftProcessSnapshot(sessionId, instanceId, process.Id, MinecraftProcessState.Running, null, startedAt, null);
        _process.Exited += OnExited;
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
        await _process.WaitForExitAsync().ConfigureAwait(false);
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

public sealed class MinecraftProcessService(IMinecraftProcessPort? port = null)
{
    private readonly IMinecraftProcessPort _port = port ?? new SystemMinecraftProcessPort();
    private readonly ConcurrentDictionary<Guid, MinecraftProcessSession> _sessions = new();

    public async ValueTask<MinecraftProcessSession> StartAsync(Minecraft.Launch.MinecraftLaunchPlan plan, string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        System.Diagnostics.Process process = await _port.StartAsync(plan.ToStartInfo(), cancellationToken).ConfigureAwait(false);
        Guid sessionId = Guid.NewGuid();
        MinecraftProcessSession session = new(process, instanceId, sessionId, DateTimeOffset.UtcNow);
        _sessions[sessionId] = session;
        return session;
    }

    public IReadOnlyList<MinecraftProcessSnapshot> ListSessions() => _sessions.Values.Select(static session => session.Snapshot).OrderBy(static snapshot => snapshot.StartedAt).ToArray();

    public bool TryGet(Guid sessionId, out MinecraftProcessSnapshot? snapshot)
    {
        if (_sessions.TryGetValue(sessionId, out MinecraftProcessSession? session)) { snapshot = session.Snapshot; return true; }
        snapshot = null;
        return false;
    }
}

