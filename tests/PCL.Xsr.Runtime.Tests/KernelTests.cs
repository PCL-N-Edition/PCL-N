using System.Threading.Channels;

using PCL.Xsr.Diagnostics;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask SchedulerRunsWorkAndObservesCompletion()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSchedulerObserver observer = new();
        using XsrScheduler scheduler = new(observer: observer);
        XsrCorrelationId correlation = XsrCorrelationId.Create();

        _ = scheduler.Schedule(
            TimeSpan.FromMilliseconds(30),
            _ =>
            {
                gate.TrySetResult();
                return ValueTask.CompletedTask;
            },
            correlation);
        AssertEqual(1, scheduler.PendingCount);

        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        XsrScheduledObservation observation = await observer.TakeAsync().ConfigureAwait(false);

        AssertTrue(observation.IsCompleted);
        AssertEqual(correlation, observation.CorrelationId);
        AssertTrue(observation.Duration >= TimeSpan.Zero);
    }

    private static async ValueTask SchedulerCancellationIsObservedWithoutRunningWork()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSchedulerObserver observer = new();
        using XsrScheduler scheduler = new(observer: observer);

        XsrScheduledWork work = scheduler.Schedule(
            TimeSpan.FromMilliseconds(30),
            async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            });

        // Cancellation before the due time still runs the timer callback, which observes the
        // cancelled outcome instead of running the handler.
        AssertTrue(work.Cancel());
        AssertFalse(work.Cancel());
        XsrScheduledObservation observation = await observer.TakeAsync().ConfigureAwait(false);

        AssertEqual(XsrScheduledOutcome.Cancelled, observation.Outcome);
        AssertTrue(work.IsCancelled);
        AssertFalse(started.Task.IsCompleted);
    }

    private static async ValueTask SchedulerFaultsAreClassifiedAndIsolated()
    {
        RecordingSchedulerObserver observer = new();
        using XsrScheduler scheduler = new(observer: observer);
        TaskCompletionSource secondRan = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = scheduler.Schedule(
            TimeSpan.FromMilliseconds(20),
            _ => throw new InvalidOperationException("private work detail"));
        _ = scheduler.Schedule(
            TimeSpan.FromMilliseconds(40),
            _ =>
            {
                secondRan.TrySetResult();
                return ValueTask.CompletedTask;
            });

        XsrScheduledObservation faulted = await observer.TakeAsync().ConfigureAwait(false);
        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        XsrScheduledObservation completed = await observer.TakeAsync().ConfigureAwait(false);

        AssertEqual(XsrScheduledOutcome.Faulted, faulted.Outcome);
        AssertEqual(typeof(InvalidOperationException).FullName, faulted.FaultType);
        AssertEqual(XsrScheduledOutcome.Completed, completed.Outcome);
    }

    private static void SchedulerDisposeCancelsPendingWork()
    {
        RecordingSchedulerObserver observer = new();
        XsrScheduler scheduler = new(observer: observer);
        XsrScheduledWork work = scheduler.Schedule(TimeSpan.FromMinutes(1), _ => ValueTask.CompletedTask);
        AssertEqual(1, scheduler.PendingCount);

        scheduler.Dispose();
        AssertEqual(0, scheduler.PendingCount);
        AssertTrue(work.IsCancelled);
        AssertThrows<ObjectDisposedException>(
            () => scheduler.Schedule(TimeSpan.Zero, _ => ValueTask.CompletedTask));
        scheduler.Dispose();
    }

    private static void SchedulerWorkDisposeThenSchedulerDisposeIsSafe()
    {
        RecordingSchedulerObserver observer = new();
        XsrScheduler scheduler = new(observer: observer);
        XsrScheduledWork work = scheduler.Schedule(TimeSpan.FromMinutes(1), _ => ValueTask.CompletedTask);
        AssertEqual(1, scheduler.PendingCount);

        // The user handle detaches itself from the scheduler, so the scheduler's own dispose
        // must not touch the already-released cancellation source.
        work.Dispose();
        AssertEqual(0, scheduler.PendingCount);
        AssertFalse(work.Cancel());

        scheduler.Dispose();
        scheduler.Dispose();
        AssertEqual(0, observer.Count);
    }

    private static async ValueTask SchedulerZeroDelayStress()
    {
        const int workCount = 2000;
        RecordingSchedulerObserver observer = new();
        using XsrScheduler scheduler = new(observer: observer);
        int executed = 0;

        for (int index = 0; index < workCount; index++)
        {
            _ = scheduler.Schedule(
                TimeSpan.Zero,
                _ =>
                {
                    Interlocked.Increment(ref executed);
                    return ValueTask.CompletedTask;
                });
        }

        for (int index = 0; index < workCount; index++)
        {
            XsrScheduledObservation observation = await observer.TakeAsync().ConfigureAwait(false);
            AssertEqual(XsrScheduledOutcome.Completed, observation.Outcome);
        }

        AssertEqual(workCount, executed);
        AssertEqual(workCount, observer.Count);
        AssertEqual(0, scheduler.PendingCount);
    }

    private static void ScopeDisposalReleasesAndUnregistersOwnedResources()
    {
        DisposalTracker first = new();
        DisposalTracker second = new();
        DisposalTracker childResource = new();
        XsrScope root = new("root");
        root.Register(first);
        root.Register(second);
        IXsrScope child = root.CreateChild("child");
        child.Register(childResource);

        AssertTrue(root.Unregister(second));
        AssertFalse(root.Unregister(second));

        root.Dispose();
        AssertTrue(first.Disposed);
        AssertFalse(second.Disposed);
        AssertTrue(childResource.Disposed);
        AssertTrue(root.IsDisposed);
        AssertTrue(child.IsDisposed);
        AssertThrows<ObjectDisposedException>(() => root.CreateChild("late"));
        AssertThrows<ObjectDisposedException>(() => root.Register(first));

        // Disposal is idempotent and never double-disposes.
        root.Dispose();
        AssertEqual(1, first.Disposals);
        AssertEqual(1, childResource.Disposals);
    }

    private static void ScopeNestingDisposesDepthFirst()
    {
        DisposalTracker grandchildResource = new();
        DisposalTracker secondChildResource = new();
        XsrScope parent = new("parent");
        IXsrScope firstChild = parent.CreateChild("first");
        IXsrScope secondChild = parent.CreateChild("second");
        IXsrScope grandchild = firstChild.CreateChild("grandchild");
        grandchild.Register(grandchildResource);
        secondChild.Register(secondChildResource);

        firstChild.Dispose();
        AssertTrue(firstChild.IsDisposed);
        AssertEqual(1, grandchildResource.Disposals);
        AssertFalse(parent.IsDisposed);

        // The parent stays usable after a child disposal.
        _ = parent.CreateChild("replacement");

        parent.Dispose();
        AssertTrue(secondChild.IsDisposed);
        AssertEqual(1, secondChildResource.Disposals);
        AssertEqual(1, grandchildResource.Disposals);

        firstChild.Dispose();
        AssertEqual(1, grandchildResource.Disposals);
    }

    private static void ScopeBulkCleanupMatchesPluginUnload()
    {
        // A plugin scope owns its scheduler, scheduled work, and tracked resources; one dispose
        // atomically tears the whole subtree down.
        XsrScope plugin = new("PluginScope");
        RecordingSchedulerObserver observer = new();
        XsrScheduler scheduler = new(observer: observer);
        plugin.Register(scheduler);
        XsrScheduledWork work = scheduler.Schedule(TimeSpan.FromMinutes(1), _ => ValueTask.CompletedTask);

        DisposalTracker[] resources = Enumerable.Range(0, 50)
            .Select(_ => new DisposalTracker())
            .ToArray();
        foreach (DisposalTracker resource in resources)
        {
            plugin.Register(resource);
        }

        // Each feature child owns its own resources; scope disposal must reach all of them
        // exactly once through the depth-first cleanup.
        DisposalTracker[] childResources = Enumerable.Range(0, 10)
            .SelectMany(_ => Enumerable.Range(0, 5).Select(_ => new DisposalTracker()))
            .ToArray();
        for (int index = 0; index < 10; index++)
        {
            IXsrScope child = plugin.CreateChild($"feature-{index}");
            foreach (DisposalTracker resource in childResources.Skip(index * 5).Take(5))
            {
                child.Register(resource);
            }
        }

        plugin.Dispose();

        AssertTrue(plugin.IsDisposed);
        AssertTrue(work.IsCancelled);
        AssertEqual(0, scheduler.PendingCount);
        AssertTrue(resources.All(resource => resource.Disposals == 1));
        AssertTrue(childResources.All(resource => resource.Disposals == 1));
        AssertEqual(0, observer.Count);
    }

    private sealed class DisposalTracker : IDisposable
    {
        private int _disposals;

        public bool Disposed => Volatile.Read(ref _disposals) > 0;

        public int Disposals => Volatile.Read(ref _disposals);

        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    private static void LifecycleAcceptsOnlyForwardTransitions()
    {
        RecordingLifecycleObserver observer = new();
        XsrLifecycle lifecycle = new("DownloadService", observer);

        AssertTrue(lifecycle.Phase == XsrLifecyclePhase.NotStarted);
        lifecycle.Enter(XsrLifecyclePhase.Starting);
        lifecycle.Enter(XsrLifecyclePhase.Running);
        lifecycle.Enter(XsrLifecyclePhase.Stopping);
        lifecycle.Enter(XsrLifecyclePhase.Stopped);

        AssertEqual(XsrLifecyclePhase.Stopped, lifecycle.Phase);
        AssertEqual(4, observer.Transitions.Length);
        AssertEqual("DownloadService", observer.Transitions[0].Component);
        AssertEqual(XsrLifecyclePhase.Stopped, observer.Transitions[3].To);

        // Stopped is terminal; restart means a new instance.
        AssertThrows<InvalidOperationException>(() => lifecycle.Enter(XsrLifecyclePhase.Starting));
        AssertThrows<InvalidOperationException>(() => lifecycle.Enter(XsrLifecyclePhase.Stopping));
    }

    private static void LifecycleRejectsIllegalTransitions()
    {
        XsrLifecycle lifecycle = new("LaunchService");

        AssertFalse(lifecycle.TryEnter(XsrLifecyclePhase.Running));
        AssertFalse(lifecycle.TryEnter(XsrLifecyclePhase.Stopping));
        AssertTrue(lifecycle.TryEnter(XsrLifecyclePhase.Starting));
        AssertFalse(lifecycle.TryEnter(XsrLifecyclePhase.Starting));

        // A half-started component may still stop cleanly.
        AssertTrue(lifecycle.TryEnter(XsrLifecyclePhase.Stopping));
        AssertTrue(lifecycle.TryEnter(XsrLifecyclePhase.Stopped));
        AssertThrows<ArgumentOutOfRangeException>(() => lifecycle.TryEnter((XsrLifecyclePhase)99));
    }

    private static void LifecycleFailureIsTerminalAndObservable()
    {
        RecordingLifecycleObserver observer = new();
        XsrLifecycle lifecycle = new("SidecarSession", observer);
        lifecycle.Enter(XsrLifecyclePhase.Starting);

        AssertTrue(lifecycle.TryEnter(XsrLifecyclePhase.Failed));
        AssertEqual(XsrLifecyclePhase.Failed, lifecycle.Phase);
        AssertFalse(lifecycle.TryEnter(XsrLifecyclePhase.Starting));
        AssertFalse(lifecycle.TryEnter(XsrLifecyclePhase.Stopping));
        AssertEqual(2, observer.Transitions.Length);
        AssertEqual(XsrLifecyclePhase.Failed, observer.Transitions[^1].To);
    }

    private static void LifecycleTransitionsAreSerializedUnderConcurrency()
    {
        XsrLifecycle lifecycle = new("StateStore");
        lifecycle.Enter(XsrLifecyclePhase.Starting);
        lifecycle.Enter(XsrLifecyclePhase.Running);

        int winners = 0;
        Parallel.For(0, 16, _ =>
        {
            if (lifecycle.TryEnter(XsrLifecyclePhase.Stopping))
            {
                Interlocked.Increment(ref winners);
            }
        });

        AssertEqual(1, winners);
        AssertEqual(XsrLifecyclePhase.Stopping, lifecycle.Phase);
    }

    private static void LifecycleErrorsCarryStableCodes()
    {
        XsrError error = XsrRuntimeErrors.Lifecycle();
        AssertEqual(XsrRuntimeErrors.LifecycleCode, error.Code);
        AssertTrue(error.Kind == XsrErrorKind.Lifecycle);
    }

    private static void SessionTraceIsBoundedAndCorrelationAddressable()
    {
        XsrSessionTrace trace = new(XsrSessionId.Create(), capacity: 4);
        AssertTrue(trace.SessionId.IsAssigned);

        XsrCorrelationId first = XsrCorrelationId.Create();
        for (int index = 0; index < 6; index++)
        {
            trace.Record(new XsrTraceEntry(
                XsrTraceKind.Command,
                XsrSemanticId.Parse("test.command"),
                first,
                index,
                $"entry-{index}",
                true));
        }

        // The ring dropped the two oldest entries and kept counting them.
        AssertEqual(4, trace.Count);
        AssertEqual(2, trace.DroppedCount);
        XsrTraceEntry[] snapshot = trace.Snapshot();
        AssertEqual("entry-2", snapshot[0].Detail);
        AssertEqual("entry-5", snapshot[3].Detail);

        XsrTraceEntry[] matches = trace.Find(first);
        AssertEqual(4, matches.Length);
        AssertEqual(0, trace.Find(default).Length);
    }

    private static async ValueTask SessionTraceCorrelatesEndToEndAcrossSubsystems()
    {
        XsrSessionTrace trace = new(XsrSessionId.Create());
        XsrCorrelationId correlation = XsrCorrelationId.Create();
        TaskCompletionSource handlerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        XsrCommandRouterBuilder commands = new();
        commands.Register<TestCommand>("trace.command".AsXsrId(), async (_, _) =>
        {
            handlerDone.TrySetResult();
            return XsrResult.Success();
        });
        XsrCommandRouter commandRouter = commands.Build(new XsrTraceDispatchObserver(trace));

        XsrEventRouterBuilder events = new();
        events.DeclareScope("trace.scope".AsXsrId(), capacity: 8);
        events.Register<CompletedEvent>("trace.event".AsXsrId(), "trace.scope".AsXsrId(), XsrEventOrdering.Global);
        XsrEventRouter eventRouter = events.Build(new XsrTraceEventObserver(trace));

        XsrStateStoreBuilder states = new();
        states.Cell<int>("trace.state".AsXsrId(), "Owner");
        XsrStateStore stateStore = states.Build(new XsrTraceStateObserver(trace));

        AssertTrue(commandRouter.TryResolve("trace.command".AsXsrId(), out XsrCommandId commandId));
        XsrCommandDispatch dispatch = commandRouter.Dispatch(commandId, new TestCommand(1), correlation);
        AssertTrue(dispatch.Acceptance.IsSuccess);

        // The handler's follow-up publications carry the same correlation.
        AssertTrue(eventRouter
            .Publish(eventRouter.Resolve("trace.event".AsXsrId()), new CompletedEvent("traced"), correlation)
            .IsSuccess);
        stateStore.Publish(stateStore.Resolve("trace.state".AsXsrId()), 5);

        await dispatch.Completion.ConfigureAwait(false);
        await handlerDone.Task.ConfigureAwait(false);

        XsrTraceEntry[] correlated = trace.Find(correlation);
        AssertEqual(2, correlated.Length);
        AssertEqual(XsrTraceKind.Command, correlated[0].Kind);
        AssertEqual("trace.command", correlated[0].SemanticId.ToString());
        AssertEqual(XsrTraceKind.Event, correlated[1].Kind);
        AssertEqual("trace.event", correlated[1].SemanticId.ToString());

        XsrTraceEntry[] snapshot = trace.Snapshot();
        AssertTrue(snapshot.Any(entry => entry.Kind == XsrTraceKind.State
            && entry.SemanticId.ToString() == "trace.state"));
        AssertTrue(snapshot.Length >= correlated.Length);
    }

    private sealed class RecordingSchedulerObserver : IXsrSchedulerObserver
    {
        private readonly Channel<XsrScheduledObservation> _observations =
            Channel.CreateUnbounded<XsrScheduledObservation>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public async ValueTask<XsrScheduledObservation> TakeAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            XsrScheduledObservation observation =
                await _observations.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            return observation;
        }

        public void OnExecuted(XsrScheduledObservation observation)
        {
            Interlocked.Increment(ref _count);
            if (!_observations.Writer.TryWrite(observation))
            {
                throw new InvalidOperationException("The observation channel rejected a value.");
            }
        }
    }

    private sealed class RecordingLifecycleObserver : IXsrLifecycleObserver
    {
        private readonly List<XsrLifecycleTransition> _transitions = [];
        private readonly object _gate = new();

        public XsrLifecycleTransition[] Transitions
        {
            get
            {
                lock (_gate)
                {
                    return [.. _transitions];
                }
            }
        }

        public void OnPhaseChanged(XsrLifecycleTransition transition)
        {
            lock (_gate)
            {
                _transitions.Add(transition);
            }
        }
    }
}

file static class XsrKernelSemanticIdExtensions
{
    public static XsrSemanticId AsXsrId(this string value) => XsrSemanticId.Parse(value);
}
