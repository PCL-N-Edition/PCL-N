namespace PCL.Xsr.Runtime;

/// <summary>
/// Handles one scheduled work item asynchronously.
/// </summary>
public delegate ValueTask XsrScheduledWorkHandler(CancellationToken cancellationToken);

/// <summary>
/// Classifies how one scheduled work item ended.
/// </summary>
public enum XsrScheduledOutcome
{
    Completed = 1,
    Cancelled = 2,
    Faulted = 3,
}

/// <summary>
/// Describes one finished scheduled work item without exposing its exception.
/// </summary>
public readonly record struct XsrScheduledObservation(
    XsrCorrelationId CorrelationId,
    XsrScheduledOutcome Outcome,
    TimeSpan Duration,
    string? FaultType)
{
    public bool IsCompleted => Outcome == XsrScheduledOutcome.Completed;
}

/// <summary>
/// Receives every finished scheduled work item, including cancelled and faulted runs. The
/// scheduler never lets an observer failure affect scheduling.
/// </summary>
public interface IXsrSchedulerObserver
{
    void OnExecuted(XsrScheduledObservation observation);
}

/// <summary>
/// The lifetime of one scheduled work item. Every transition happens under the owning
/// scheduler's gate: <see cref="Pending"/> until the due time fires, then <see cref="Running"/>,
/// ending in exactly one terminal state.
/// </summary>
internal enum XsrScheduledWorkState
{
    Pending = 1,
    Running = 2,
    Completed = 3,
    Cancelled = 4,
    Disposed = 5,
}

/// <summary>
/// One cancellable handle over scheduled work. The scheduler owns the timer; the handle only
/// cancels and releases. Disposing the handle is safe from any thread at any point — before the
/// due time, while running, after completion, or after the scheduler itself was disposed.
/// </summary>
public sealed class XsrScheduledWork : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly XsrScheduler _owner;
    private XsrScheduledWorkState _state = XsrScheduledWorkState.Pending;
    private bool _cancelled;
    private bool _observed;

    internal XsrScheduledWork(XsrScheduler owner, XsrCorrelationId correlationId)
    {
        _owner = owner;
        CorrelationId = correlationId;
    }

    public XsrCorrelationId CorrelationId { get; }

    public bool IsCancelled => _cancelled;

    internal XsrScheduledWorkState State => _state;

    internal CancellationToken CancellationToken => _source.Token;

    /// <summary>
    /// Cancels the work before or during execution. Returns false when it already reached a
    /// terminal state.
    /// </summary>
    public bool Cancel()
    {
        lock (_owner.Gate)
        {
            switch (_state)
            {
                case XsrScheduledWorkState.Pending:
                    // The timer stays alive: firing later delivers the cancelled observation.
                    _state = XsrScheduledWorkState.Cancelled;
                    _cancelled = true;
                    _ = _owner.RemovePendingLocked(this);
                    break;
                case XsrScheduledWorkState.Running:
                    _cancelled = true;
                    break;
                default:
                    return false;
            }
        }

        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The work finished and released its source concurrently with this cancellation.
        }

        return true;
    }

    /// <summary>
    /// Releases the handle's timer and cancellation source and detaches it from the scheduler.
    /// </summary>
    public void Dispose()
    {
        ITimer? timer;
        lock (_owner.Gate)
        {
            if (_state == XsrScheduledWorkState.Disposed)
            {
                return;
            }

            _state = XsrScheduledWorkState.Disposed;
            _ = _owner.RemovePendingLocked(this);
            timer = _owner.RemoveTimerLocked(this);
        }

        timer?.Dispose();
        _source.Dispose();
    }

    internal void BeginRunLocked()
    {
        _state = XsrScheduledWorkState.Running;
    }

    internal void MarkDisposedBySchedulerLocked()
    {
        _state = XsrScheduledWorkState.Disposed;
        _cancelled = true;
    }

    internal void CompleteLocked(XsrScheduledOutcome outcome)
    {
        // A user disposal wins over a late completion classification.
        if (_state == XsrScheduledWorkState.Running)
        {
            _state = outcome == XsrScheduledOutcome.Cancelled
                ? XsrScheduledWorkState.Cancelled
                : XsrScheduledWorkState.Completed;
        }
    }

    internal bool TryMarkObserved()
    {
        lock (_owner.Gate)
        {
            if (_observed)
            {
                return false;
            }

            _observed = true;
            return true;
        }
    }

    internal void ReleaseSource()
    {
        _source.Dispose();
    }
}

/// <summary>
/// Schedules cancellable one-shot work on a <see cref="TimeProvider"/>. Work completion is always
/// observed, so detached work never produces unobserved exceptions, and faulted work is recorded
/// by classification instead of leaking its exception.
/// </summary>
public sealed class XsrScheduler : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly IXsrSchedulerObserver? _observer;
    private readonly object _gate = new();
    private readonly List<XsrScheduledWork> _pending = [];
    private readonly Dictionary<XsrScheduledWork, ITimer> _timers = [];
    private bool _disposed;

    internal object Gate => _gate;

    public XsrScheduler(TimeProvider? timeProvider = null, IXsrSchedulerObserver? observer = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observer = observer;
    }

    /// <summary>
    /// Gets the number of work items whose due time has not fired yet.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Schedules one cancellable work item at a future due time.
    /// </summary>
    public XsrScheduledWork Schedule(
        TimeSpan dueTime,
        XsrScheduledWorkHandler handler,
        XsrCorrelationId correlationId = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (dueTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime), "A due time cannot be negative.");
        }

        correlationId = correlationId.IsAssigned ? correlationId : XsrCorrelationId.Create();
        XsrScheduledWork work;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(XsrScheduler));
            work = new XsrScheduledWork(this, correlationId);
            _pending.Add(work);
        }

        ITimer? timer = _timeProvider.CreateTimer(
            _ => Run(work, handler),
            null,
            dueTime,
            Timeout.InfiniteTimeSpan);

        lock (_gate)
        {
            // The callback may race ahead of this attachment when the due time is zero. The
            // scheduler owns timer disposal in both orderings.
            if (!_disposed && work.State == XsrScheduledWorkState.Pending)
            {
                _timers.Add(work, timer);
                timer = null;
            }
        }

        timer?.Dispose();
        return work;
    }

    /// <summary>
    /// Cancels every pending work item, releases its timers, and stops accepting new work. Work
    /// handles disposed earlier are skipped.
    /// </summary>
    public void Dispose()
    {
        ITimer[] timers;
        XsrScheduledWork[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (XsrScheduledWork work in _pending)
            {
                work.MarkDisposedBySchedulerLocked();
            }

            pending = [.. _pending];
            _pending.Clear();
            timers = [.. _timers.Values];
            _timers.Clear();
        }

        foreach (ITimer timer in timers)
        {
            timer.Dispose();
        }

        foreach (XsrScheduledWork work in pending)
        {
            work.ReleaseSource();
        }
    }

    internal bool RemovePendingLocked(XsrScheduledWork work) => _pending.Remove(work);

    internal ITimer? RemoveTimerLocked(XsrScheduledWork work)
    {
        _ = _timers.Remove(work, out ITimer? timer);
        return timer;
    }

    private void Run(XsrScheduledWork work, XsrScheduledWorkHandler handler)
    {
        ITimer? timer;
        bool skipRun;
        lock (_gate)
        {
            timer = RemoveTimerLocked(work);
            skipRun = work.State != XsrScheduledWorkState.Pending;
            if (!skipRun)
            {
                work.BeginRunLocked();
                _ = RemovePendingLocked(work);
            }
        }

        timer?.Dispose();

        if (skipRun)
        {
            // A work cancelled before its due time still gets its one cancelled observation;
            // a work disposed before its due time is abandoned without observation.
            if (work.State == XsrScheduledWorkState.Cancelled)
            {
                Observe(work, XsrScheduledOutcome.Cancelled, 0, null);
                work.ReleaseSource();
            }

            return;
        }

        long startedAt = _timeProvider.GetTimestamp();
        ValueTask invocation;
        try
        {
            invocation = handler(work.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            Finish(work, XsrScheduledOutcome.Cancelled, startedAt, null);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            Finish(work, XsrScheduledOutcome.Faulted, startedAt, exception.GetType().FullName);
            return;
        }

        _ = AwaitAndFinish(work, invocation, startedAt);
    }

    private async Task AwaitAndFinish(XsrScheduledWork work, ValueTask invocation, long startedAt)
    {
        try
        {
            await invocation.ConfigureAwait(false);
            Finish(work, XsrScheduledOutcome.Completed, startedAt, null);
        }
        catch (OperationCanceledException)
        {
            Finish(work, XsrScheduledOutcome.Cancelled, startedAt, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            Finish(work, XsrScheduledOutcome.Faulted, startedAt, exception.GetType().FullName);
        }
    }

    private void Finish(
        XsrScheduledWork work,
        XsrScheduledOutcome outcome,
        long startedAt,
        string? faultType)
    {
        lock (_gate)
        {
            work.CompleteLocked(outcome);
        }

        Observe(work, outcome, startedAt, faultType);
        work.ReleaseSource();
    }

    private void Observe(
        XsrScheduledWork work,
        XsrScheduledOutcome outcome,
        long startedAt,
        string? faultType)
    {
        if (_observer is null || !work.TryMarkObserved())
        {
            return;
        }

        XsrScheduledObservation observation = new(
            work.CorrelationId,
            outcome,
            startedAt == 0 ? TimeSpan.Zero : _timeProvider.GetElapsedTime(startedAt),
            faultType);

        try
        {
            _observer.OnExecuted(observation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Scheduling must not be changed by a diagnostics observer failure.
        }
    }
}
