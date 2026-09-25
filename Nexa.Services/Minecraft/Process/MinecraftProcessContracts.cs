using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Crash;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Process;

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
    DateTimeOffset? EndedAt)
{
    public string InstanceDirectory { get; init; } = string.Empty;
    public string GameDirectory { get; init; } = string.Empty;
}

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
    private readonly Queue<string> _evidence = new();
    private readonly Queue<string> _errorEvidence = new();
    private readonly CancellationTokenSource _drainLifetime = new();
    private readonly Task _outputDrain;
    private readonly Task _errorDrain;
    private int _disposed;

    internal MinecraftProcessSession(System.Diagnostics.Process process, string instanceId, Guid sessionId, DateTimeOffset startedAt, string instanceDirectory, string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        _process = process;
        _snapshot = new MinecraftProcessSnapshot(sessionId, instanceId, process.Id, MinecraftProcessState.Created, null, startedAt, null)
        {
            InstanceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory)),
            GameDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)),
        };
        _createdSnapshot = _snapshot;
        _outputDrain = process.StartInfo.RedirectStandardOutput ? Task.Run(() => DrainAsync(process.StandardOutput, _evidence)) : Task.CompletedTask;
        _errorDrain = process.StartInfo.RedirectStandardError ? Task.Run(() => DrainAsync(process.StandardError, _errorEvidence)) : Task.CompletedTask;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
    }

    private async Task DrainAsync(StreamReader reader, Queue<string> evidence)
    {
        char[] buffer = new char[2048];
        StringBuilder line = new();
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), _drainLifetime.Token).ConfigureAwait(false)) > 0)
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c == '\n' || line.Length == 2048)
                    {
                        AddEvidence(evidence, line.ToString());
                        line.Clear();
                    }
                    if (c is not ('\n' or '\r')) line.Append(c);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { if (line.Length > 0) AddEvidence(evidence, line.ToString()); }
    }

    private void AddEvidence(Queue<string> evidence, string line)
    {
        lock (_gate)
        {
            if (evidence.Count == 100) evidence.Dequeue();
            evidence.Enqueue(LogRedactor.Redact(line));
        }
    }

    internal async Task<string[]> ReadEvidenceAsync()
    {
        // Descendants can inherit pipe handles; EOF must never block supervision.
        await Task.WhenAny(Task.WhenAll(_outputDrain, _errorDrain), Task.Delay(2000)).ConfigureAwait(false);
        lock (_gate) return [.. _evidence, .. _errorEvidence];
    }

    internal async Task<(string[] Stdout, string[] Stderr)> ReadSeparatedEvidenceAsync()
    {
        await Task.WhenAny(Task.WhenAll(_outputDrain, _errorDrain), Task.Delay(2000)).ConfigureAwait(false);
        lock (_gate) return (_evidence.ToArray(), _errorEvidence.ToArray());
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
            MinecraftProcessSnapshot before = _snapshot;
            _snapshot = before with { State = MinecraftProcessState.Cancelled, EndedAt = DateTimeOffset.UtcNow };
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch { _snapshot = before; throw; }
            changed = _snapshot;
        }
        if (changed is { } snapshot) Changed?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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

        _drainLifetime.Cancel();
        await Task.WhenAll(_outputDrain, _errorDrain).ConfigureAwait(false);
        _drainLifetime.Dispose();
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
    private readonly string? _jvmHostExecutable;
    private readonly LogService? _log;
    private readonly XsrStateStore? _store;
    private readonly XsrStateId _sessionsId;
    private readonly ConcurrentDictionary<Guid, MinecraftProcessSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _analyses = new();

    public MinecraftProcessService(IMinecraftProcessPort? port = null, XsrStateStore? hostStore = null, LogService? log = null,
        string? jvmHostExecutable = null)
    {
        if (jvmHostExecutable is not null && !Path.IsPathFullyQualified(jvmHostExecutable))
            throw new ArgumentException("JVM host executable must be an absolute path.", nameof(jvmHostExecutable));
        _jvmHostExecutable = jvmHostExecutable;
        _port = port ?? new SystemMinecraftProcessPort();
        _log = log;
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
        using LogOperation? operation = _log?.BeginOperation("Process", "StartProcess", $"instance={instanceId}");
        using MemoryStream? bootstrap = _jvmHostExecutable is null ? null : new MemoryStream();
        MinecraftProcessSession? startedSession = null;
        try
        {
            ProcessStartInfo startInfo = plan.ToStartInfo();
            if (bootstrap is not null)
            {
                await JvmHostBootstrap.WriteAsync(bootstrap, plan, cancellationToken).ConfigureAwait(false);
                bootstrap.Position = 0;
                startInfo.FileName = _jvmHostExecutable!;
                startInfo.ArgumentList.Clear();
                startInfo.ArgumentList.Add("--jvm-host");
                startInfo.RedirectStandardInput = true;
            }
            operation?.Stage("os_start", $"executable={startInfo.FileName} working_directory={startInfo.WorkingDirectory} argument_count={startInfo.ArgumentList.Count}");
            System.Diagnostics.Process process = await _port.StartAsync(startInfo, cancellationToken).ConfigureAwait(false);
            Guid sessionId = Guid.NewGuid();
            MinecraftProcessSession session = new(process, instanceId, sessionId, DateTimeOffset.UtcNow, plan.InstanceDirectory, plan.GameDirectory);
            startedSession = session;
            session.Changed += OnSessionChanged;
            _sessions[sessionId] = session;
            // Publish the Created observation even if the child exited between Process.Start and
            // event subscription; the terminal snapshot follows immediately in that case.
            Publish(session.CreatedSnapshot);
            if (session.Snapshot.State != MinecraftProcessState.Created) OnSessionChanged(session.Snapshot);
            session.StartLifecycle();
            if (bootstrap is not null)
            {
                using CancellationTokenSource transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                transfer.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await bootstrap.CopyToAsync(process.StandardInput.BaseStream, transfer.Token).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(transfer.Token).ConfigureAwait(false);
                }
                finally { process.StandardInput.Close(); }
            }
            PruneSessions();
            operation?.Complete($"session={sessionId} pid={process.Id} state={session.Snapshot.State}");
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            startedSession?.Cancel();
            operation?.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            startedSession?.Cancel();
            operation?.Fail(exception);
            throw;
        }
        finally
        {
            if (bootstrap is not null && bootstrap.TryGetBuffer(out ArraySegment<byte> bytes))
                bytes.AsSpan().Clear();
        }
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
        _log?.Info("Process", $"Process cancellation requested session={sessionId}");
        PruneSessions();
        if (!_sessions.TryGetValue(sessionId, out MinecraftProcessSession? session))
        {
            _log?.Warn("Process", $"Process cancellation rejected session={sessionId} reason=unknown_session");
            return false;
        }
        if (session.Snapshot.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running))
        {
            _log?.Debug("Process", $"Process cancellation ignored session={sessionId} state={session.Snapshot.State}");
            return false;
        }
        session.Cancel();
        return session.Snapshot.State is MinecraftProcessState.Cancelled or MinecraftProcessState.Exited or MinecraftProcessState.Failed;
    }

    private void OnSessionChanged(MinecraftProcessSnapshot snapshot)
    {
        Publish(snapshot);
        if (snapshot.State == MinecraftProcessState.Failed && _sessions.TryGetValue(snapshot.SessionId, out var session))
            _ = _analyses.GetOrAdd(snapshot.SessionId, _ => new Lazy<Task>(() => Task.Run(() => AnalyzeExitAsync(session)))).Value;
        if (snapshot.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running))
            PruneSessions();
    }

    private async Task AnalyzeExitAsync(MinecraftProcessSession session)
    {
        try
        {
            string[] evidence = await session.ReadEvidenceAsync().ConfigureAwait(false);
            var snapshot = session.Snapshot;
            if (snapshot.State != MinecraftProcessState.Failed || _store is null) return;
            // The bounded output has already been redacted by AddEvidence. Preserve it locally
            // before the session is pruned so diagnostics do not depend on an open dialog.
            foreach (string line in evidence)
                _log?.Warn("GameOutput", $"session={snapshot.SessionId} pid={snapshot.ProcessId} {line}");
            var report = MinecraftLaunchFaultAnalyzer.AnalyzeText(
                evidence.Length == 0 ? [$"Minecraft exited with code {snapshot.ExitCode}."] : evidence);
            var id = _store.Resolve(MinecraftProcessStateComposition.FailuresKey);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var current = _store.ReadCollection<MinecraftProcessFailure>(id);
                Guid[] removals = current.Items.Take(Math.Max(0, current.Items.Count - RetainedExitedSessions + 1)).Select(item => item.SessionId).ToArray();
                if (_store.PublishDelta(id, new XsrCollectionDelta<MinecraftProcessFailure, Guid>(current.Revision,
                    [new(snapshot.SessionId, snapshot.InstanceId, report)], removals)).IsApplied) break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        { _log?.Warn("Process", $"Crash analysis failed: {ex.Message}"); }
    }

    private void Publish(MinecraftProcessSnapshot snapshot)
    {
        _log?.Write(snapshot.State == MinecraftProcessState.Failed ? LogLevel.Error : LogLevel.Info,
            "Process", $"Process lifecycle session={snapshot.SessionId} instance={snapshot.InstanceId} pid={snapshot.ProcessId} state={snapshot.State} exit_code={snapshot.ExitCode}");
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
                    _log?.Debug("Process", $"Pruning completed session={snapshot.SessionId} stale={stale} over_retention={overRetention}");
                    removed.Changed -= OnSessionChanged;
                    RemovePublished(snapshot.SessionId);
                    _analyses.TryRemove(snapshot.SessionId, out _);
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

        await Task.WhenAll(_analyses.Values.Where(item => item.IsValueCreated).Select(item => item.Value)).ConfigureAwait(false);
        _analyses.Clear();
        _sessions.Clear();
    }
}
