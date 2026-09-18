using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Tasks;

/// <summary>
/// Tracks user-visible background tasks (installs, downloads, self-update…) and publishes
/// the task list plus a summary into the host store. Callers keep an <see cref="ITaskCenterTask"/>
/// handle; the service owns cancellation so the cancel route works without reaching into the
/// caller. Terminal entries stay visible until dismissed or evicted by the retention cap.
/// </summary>
public sealed class TaskCenterService
{
    private const int MaxStateConflicts = 8;
    private const int RetainedTerminalEntries = 30;

    private readonly XsrStateStore _store;
    private readonly object _stateGate = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly XsrStateId _entriesId;
    private readonly XsrStateId _summaryId;

    internal sealed class Registration(TaskCenterEntry entry)
    {
        public TaskCenterEntry Entry { get; set; } = entry;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Released { get; set; }
    }

    public TaskCenterService(XsrStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _entriesId = store.Resolve(TaskCenterStateContract.EntriesKey);
        _summaryId = store.Resolve(TaskCenterStateContract.SummaryKey);
        PublishSummary([]);
    }

    /// <summary>Registers a task and returns its reporting handle.</summary>
    public ITaskCenterTask Begin(TaskCenterStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Title);

        TaskCenterEntry entry = new(
            start.TaskId,
            start.Title,
            start.StagePlan.Count > 0 ? start.StagePlan[0].Trim() : "准备",
            "正在准备",
            0d,
            0,
            0,
            0,
            TaskCenterEntryState.Running,
            CanCancel: start.CanCancel,
            Steps: TaskStagePlanner.Create(start.StagePlan));
        Registration registration;
        lock (_gate)
        {
            if (_registrations.TryGetValue(start.TaskId, out Registration? previous) && !previous.Released)
            {
                throw new InvalidOperationException($"Task '{start.TaskId}' is already registered.");
            }

            registration = new Registration(entry);
            _registrations[start.TaskId] = registration;
        }

        ApplyEntry(entry);
        return new TaskHandle(this, registration);
    }

    /// <summary>
    /// Cancels a running task by id. CanCancel=false is enforced HERE, at the capability
    /// boundary — the UI hiding the button is presentation, not protection. The three
    /// outcomes stay distinguishable so routes never disguise not-cancelable as missing.
    /// </summary>
    public TaskCenterCancelResult RequestCancel(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        lock (_gate)
        {
            if (!_registrations.TryGetValue(taskId, out Registration? registration)
                || registration.Entry.IsTerminal)
            {
                return TaskCenterCancelResult.NotFound;
            }

            if (!registration.Entry.CanCancel)
            {
                return TaskCenterCancelResult.NotCancelable;
            }

            registration.Cancellation.Cancel();
            return TaskCenterCancelResult.Canceled;
        }
    }

    /// <summary>Removes a terminal entry from the visible list.</summary>
    public bool Dismiss(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        lock (_gate)
        {
            if (!_registrations.TryGetValue(taskId, out Registration? registration)
                || !registration.Entry.IsTerminal)
            {
                return false;
            }

            _registrations.Remove(taskId);
        }

        RemoveEntry(taskId);
        return true;
    }

    /// <summary>Dismisses every terminal entry; returns how many were removed.</summary>
    public int ClearFinished()
    {
        List<string> removed = [];
        lock (_gate)
        {
            foreach (KeyValuePair<string, Registration> pair in _registrations)
            {
                if (pair.Value.Entry.IsTerminal)
                {
                    removed.Add(pair.Key);
                }
            }

            foreach (string taskId in removed)
            {
                _registrations.Remove(taskId);
            }
        }

        foreach (string taskId in removed)
        {
            RemoveEntry(taskId);
        }

        return removed.Count;
    }

    internal void Report(Registration registration, string stage, string detail, double progress,
        int completedFiles, int totalFiles, long speedBytesPerSecond)
    {
        TaskCenterEntry updated;
        lock (_gate)
        {
            if (registration.Released || registration.Entry.IsTerminal)
            {
                return;
            }

            double clamped = Math.Clamp(progress, 0d, 1d);
            updated = registration.Entry with
            {
                Stage = stage,
                Detail = detail,
                Progress = clamped,
                CompletedFiles = Math.Max(0, completedFiles),
                TotalFiles = Math.Max(0, totalFiles),
                SpeedBytesPerSecond = Math.Max(0, speedBytesPerSecond),
                State = TaskCenterEntryState.Running,
                Steps = registration.Entry.Steps is { } steps
                    ? TaskStagePlanner.Advance(steps, stage, detail, clamped)
                    : null,
            };
            registration.Entry = updated;
        }

        ApplyEntry(updated);
    }

    internal void Complete(Registration registration, string detail)
    {
        TaskCenterEntry updated;
        lock (_gate)
        {
            if (registration.Released || registration.Entry.IsTerminal)
            {
                return;
            }

            updated = Terminal(registration.Entry, TaskCenterEntryState.Finished, detail, null);
            registration.Entry = updated;
            registration.Released = true;
        }

        CommitTerminal(registration, updated);
    }

    internal void Fail(Registration registration, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        TaskCenterEntry updated;
        lock (_gate)
        {
            if (registration.Released || registration.Entry.IsTerminal)
            {
                return;
            }

            updated = Terminal(registration.Entry, TaskCenterEntryState.Failed, "失败", message);
            registration.Entry = updated;
            registration.Released = true;
        }

        CommitTerminal(registration, updated);
    }

    internal void MarkCanceled(Registration registration)
    {
        TaskCenterEntry updated;
        lock (_gate)
        {
            if (registration.Released || registration.Entry.IsTerminal)
            {
                return;
            }

            updated = Terminal(registration.Entry, TaskCenterEntryState.Canceled, "已取消", null);
            registration.Entry = updated;
            registration.Released = true;
        }

        CommitTerminal(registration, updated);
    }

    /// <summary>A released handle whose task never reached a terminal state must not read as running forever.</summary>
    internal void Abandon(Registration registration)
    {
        TaskCenterEntry updated;
        lock (_gate)
        {
            if (registration.Released || registration.Entry.IsTerminal)
            {
                return;
            }

            updated = Terminal(registration.Entry, TaskCenterEntryState.Failed, "失败", "任务意外结束。");
            registration.Entry = updated;
            registration.Released = true;
        }

        CommitTerminal(registration, updated);
    }

    private static TaskCenterEntry Terminal(
        TaskCenterEntry entry, TaskCenterEntryState state, string detail, string? error) =>
        entry with
        {
            State = state,
            Detail = detail,
            ErrorMessage = error,
            // Finished reads as fully complete; canceled keeps the progress it reached.
            Progress = state == TaskCenterEntryState.Finished ? 1d : entry.Progress,
            SpeedBytesPerSecond = 0,
            Steps = entry.Steps is { } steps ? TaskStagePlanner.Advance(steps, "完成", detail, 1d) : null,
        };

    /// <summary>
    /// Every terminalization commits through here: dispose the token, publish the terminal
    /// entry, then bound the terminal history. The retention cap is a capability contract
    /// (≤30 terminal entries), not a UI nicety, so Failed/Canceled/Abandoned are bounded too.
    /// </summary>
    private void CommitTerminal(Registration registration, TaskCenterEntry updated)
    {
        registration.Cancellation.Dispose();
        ApplyEntry(updated);
        EvictOldTerminalEntries();
    }

    private void EvictOldTerminalEntries()
    {
        List<string> evicted = [];
        lock (_gate)
        {
            List<string> terminal = [.. _registrations
                .Where(pair => pair.Value.Entry.IsTerminal)
                .Select(pair => pair.Key)];
            if (terminal.Count <= RetainedTerminalEntries)
            {
                return;
            }

            foreach (string taskId in terminal.Take(terminal.Count - RetainedTerminalEntries))
            {
                _registrations.Remove(taskId, out _);
                evicted.Add(taskId);
            }
        }

        foreach (string taskId in evicted)
        {
            RemoveEntry(taskId);
        }
    }

    private void ApplyEntry(TaskCenterEntry entry)
    {
        lock (_stateGate)
        {
            for (int attempt = 0; attempt < MaxStateConflicts; attempt++)
            {
                XsrCollectionSnapshot<TaskCenterEntry> snapshot = _store.ReadCollection<TaskCenterEntry>(_entriesId);
                XsrCollectionApplyResult result = _store.PublishDelta(
                    _entriesId,
                    new XsrCollectionDelta<TaskCenterEntry, string>(snapshot.Revision, [entry], []));
                if (result.IsApplied)
                {
                    // The snapshot predates this delta, so fold the upsert in before summarizing.
                    PublishSummary([.. snapshot.Items.Where(existing => existing.TaskId != entry.TaskId), entry]);
                    return;
                }
            }
        }
    }

    private void RemoveEntry(string taskId)
    {
        lock (_stateGate)
        {
            for (int attempt = 0; attempt < MaxStateConflicts; attempt++)
            {
                XsrCollectionSnapshot<TaskCenterEntry> snapshot = _store.ReadCollection<TaskCenterEntry>(_entriesId);
                XsrCollectionApplyResult result = _store.PublishDelta(
                    _entriesId,
                    new XsrCollectionDelta<TaskCenterEntry, string>(snapshot.Revision, [], [taskId]));
                if (result.IsApplied)
                {
                    PublishSummary([.. snapshot.Items.Where(existing => existing.TaskId != taskId)]);
                    return;
                }
            }
        }
    }

    private void PublishSummary(IReadOnlyList<TaskCenterEntry> entries)
    {
        List<TaskCenterEntry> active = [.. entries.Where(static entry => !entry.IsTerminal)];
        double progress = active.Count == 0
            ? entries.Count == 0 ? 0d : 1d
            : active.Average(static entry => Math.Clamp(entry.Progress, 0d, 1d));
        TaskCenterSummary summary = new(
            active.Count,
            entries.Count,
            progress,
            active.Sum(static entry => Math.Max(0, entry.SpeedBytesPerSecond)),
            active.Sum(static entry => entry.TotalFiles > 0
                ? Math.Max(0, entry.TotalFiles - entry.CompletedFiles)
                : 0));
        _store.Publish(_summaryId, summary);
    }
}

/// <summary>The reporting surface of one registered task.</summary>
public interface ITaskCenterTask : IDisposable
{
    string TaskId { get; }

    /// <summary>Cancelled when the user cancels the task card (or the service shuts down).</summary>
    CancellationToken CancellationToken { get; }

    void Report(string stage, string detail, double progress, int completedFiles, int totalFiles,
        long speedBytesPerSecond);

    /// <summary>Marks the task finished; the entry stays visible until dismissed.</summary>
    void Complete(string detail = "完成");

    /// <summary>Marks the task canceled after the cancellation token fired.</summary>
    void Canceled();

    void Fail(string message);
}

internal sealed class TaskHandle(TaskCenterService service, TaskCenterService.Registration registration) : ITaskCenterTask
{
    public string TaskId => registration.Entry.TaskId;

    public CancellationToken CancellationToken => registration.Cancellation.Token;

    public void Report(string stage, string detail, double progress, int completedFiles, int totalFiles,
        long speedBytesPerSecond) =>
        service.Report(registration, stage, detail, progress, completedFiles, totalFiles, speedBytesPerSecond);

    public void Complete(string detail = "完成") => service.Complete(registration, detail);

    public void Canceled() => service.MarkCanceled(registration);

    public void Fail(string message) => service.Fail(registration, message);

    public void Dispose()
    {
        try
        {
            service.Abandon(registration);
        }
        catch (ObjectDisposedException)
        {
            // The service shut down first; nothing left to publish.
        }
    }
}
