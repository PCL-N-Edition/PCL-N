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
/// One cancellable handle over scheduled work. Disposing the handle releases its timer and
/// cancellation source; a disposed handle cannot be cancelled again.
/// </summary>
public sealed class XsrScheduledWork : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private ITimer? _timer;

    internal XsrScheduledWork(XsrCorrelationId correlationId)
    {
        CorrelationId = correlationId;
    }

    public XsrCorrelationId CorrelationId { get; }

    public bool IsCancelled => _source.IsCancellationRequested;

    internal CancellationToken CancellationToken => _source.Token;

    /// <summary>
    /// Cancels the work before or during execution. Returns false when it was already cancelled.
    /// </summary>
    public bool Cancel()
    {
        if (_source.IsCancellationRequested)
        {
            return false;
        }

        _source.Cancel();
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _source.Dispose();
    }

    internal void AttachTimer(ITimer timer)
    {
        _timer = timer;
    }

    internal ITimer? DetachTimer()
    {
        ITimer? timer = _timer;
        _timer = null;
        return timer;
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
    private bool _disposed;

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
        XsrScheduledWork work = new(correlationId);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(XsrScheduler));

            _pending.Add(work);
        }

        ITimer timer = _timeProvider.CreateTimer(
            _ => Run(work, handler),
            null,
            dueTime,
            Timeout.InfiniteTimeSpan);
        work.AttachTimer(timer);
        return work;
    }

    /// <summary>
    /// Cancels every pending work item and stops accepting new work.
    /// </summary>
    public void Dispose()
    {
        XsrScheduledWork[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (XsrScheduledWork work in pending)
        {
            work.Cancel();
            work.Dispose();
        }
    }

    private void Run(XsrScheduledWork work, XsrScheduledWorkHandler handler)
    {
        lock (_gate)
        {
            _ = _pending.Remove(work);
        }

        work.DetachTimer()?.Dispose();
        long startedAt = _timeProvider.GetTimestamp();

        if (work.CancellationToken.IsCancellationRequested)
        {
            Observe(work, XsrScheduledOutcome.Cancelled, startedAt, null);
            work.Dispose();
            return;
        }

        ValueTask invocation;
        try
        {
            invocation = handler(work.CancellationToken);
        }
        catch (OperationCanceledException) when (work.CancellationToken.IsCancellationRequested)
        {
            Observe(work, XsrScheduledOutcome.Cancelled, startedAt, null);
            work.Dispose();
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            Observe(work, XsrScheduledOutcome.Faulted, startedAt, exception.GetType().FullName);
            work.Dispose();
            return;
        }

        _ = AwaitAndObserve(work, invocation, startedAt);
    }

    private async Task AwaitAndObserve(XsrScheduledWork work, ValueTask invocation, long startedAt)
    {
        try
        {
            await invocation.ConfigureAwait(false);
            Observe(work, XsrScheduledOutcome.Completed, startedAt, null);
        }
        catch (OperationCanceledException) when (work.CancellationToken.IsCancellationRequested)
        {
            Observe(work, XsrScheduledOutcome.Cancelled, startedAt, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            Observe(work, XsrScheduledOutcome.Faulted, startedAt, exception.GetType().FullName);
        }
        finally
        {
            work.Dispose();
        }
    }

    private void Observe(
        XsrScheduledWork work,
        XsrScheduledOutcome outcome,
        long startedAt,
        string? faultType)
    {
        if (_observer is null)
        {
            return;
        }

        XsrScheduledObservation observation = new(
            work.CorrelationId,
            outcome,
            _timeProvider.GetElapsedTime(startedAt),
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
