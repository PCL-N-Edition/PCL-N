using PCL.Xsr.Runtime;

namespace PCL.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static void EventsAssignDeterministicTypedIdentifiers()
    {
        XsrEventRouterBuilder first = BuildTestRouter();
        XsrEventRouterBuilder second = BuildTestRouter(reversed: true);

        XsrEventRouter firstRouter = first.Build();
        XsrEventRouter secondRouter = second.Build();

        AssertTrue(firstRouter.TryResolve("event.step".AsXsrId(), out XsrEventId firstStep));
        AssertTrue(secondRouter.TryResolve("event.step".AsXsrId(), out XsrEventId secondStep));
        AssertTrue(firstRouter.TryResolve("event.typed".AsXsrId(), out XsrEventId firstTyped));
        AssertTrue(secondRouter.TryResolve("event.typed".AsXsrId(), out XsrEventId secondTyped));
        AssertEqual(firstStep, secondStep);
        AssertEqual(firstTyped, secondTyped);
        AssertTrue(firstStep.IsAssigned);
        AssertTrue(firstTyped.IsAssigned);
        AssertFalse(firstRouter.TryResolve("event.unknown".AsXsrId(), out _));
    }

    private static void EventsShareSequenceSpaceInsideDeclaredScope()
    {
        XsrEventRouter router = BuildTestRouter().Build();
        XsrEventId step = router.Resolve("event.step".AsXsrId());
        XsrEventId completed = router.Resolve("event.completed".AsXsrId());

        AssertTrue(router.Publish(step, new StepEvent(1)).IsSuccess);
        AssertTrue(router.Publish(completed, new CompletedEvent("done")).IsSuccess);
        AssertTrue(router.Publish(step, new StepEvent(2)).IsSuccess);

        XsrEventSubscription<StepEvent> steps = router.Subscribe<StepEvent>(step, replayFromSequence: 1);
        XsrEventSubscription<CompletedEvent> completions =
            router.Subscribe<CompletedEvent>(completed, replayFromSequence: 1);

        // One scope, one sequence space; each subscription delivers only its own event.
        XsrResult<XsrEventDelivery<StepEvent>> firstStep = RequireResult(steps.ReadAsync().AsTask());
        XsrResult<XsrEventDelivery<CompletedEvent>> onlyCompletion =
            RequireResult(completions.ReadAsync().AsTask());
        XsrResult<XsrEventDelivery<StepEvent>> secondStep = RequireResult(steps.ReadAsync().AsTask());

        AssertEqual(1L, RequireValue(firstStep).Record.Sequence);
        AssertEqual(2L, RequireValue(onlyCompletion).Record.Sequence);
        AssertEqual(3L, RequireValue(secondStep).Record.Sequence);
        AssertEqual(1, RequireValue(firstStep).Payload.Index);
        AssertEqual("done", RequireValue(onlyCompletion).Payload.Reason);
        AssertEqual(step, RequireValue(firstStep).Record.EventId);
        AssertEqual("scope.download", RequireValue(onlyCompletion).Record.ScopeId.ToString());
    }

    private static void EventsOrderPerScopeKeyIndependently()
    {
        XsrEventRouter router = BuildTestRouter().Build();
        XsrEventId typed = router.Resolve("event.typed".AsXsrId());

        AssertTrue(router.Publish(typed, new TypedEvent("a1"), scopeKey: "alpha").IsSuccess);
        AssertTrue(router.Publish(typed, new TypedEvent("b1"), scopeKey: "beta").IsSuccess);
        AssertTrue(router.Publish(typed, new TypedEvent("a2"), scopeKey: "alpha").IsSuccess);

        XsrEventSubscription<TypedEvent> alpha = router.Subscribe<TypedEvent>(typed, scopeKey: "alpha", replayFromSequence: 1);
        XsrEventSubscription<TypedEvent> beta = router.Subscribe<TypedEvent>(typed, scopeKey: "beta", replayFromSequence: 1);

        AssertEqual(("a1", 1L), ReadPayload(alpha));
        AssertEqual(("a2", 2L), ReadPayload(alpha));
        AssertEqual(("b1", 1L), ReadPayload(beta));
        AssertThrows<ArgumentException>(() => router.Publish(typed, new TypedEvent("x")));
        AssertThrows<ArgumentException>(() => router.Subscribe<TypedEvent>(typed));
    }

    private static void EventsRejectWithBackpressureInsteadOfDropping()
    {
        XsrEventRouter router = BuildIsolatedRouter(capacity: 2);
        XsrEventId isolated = router.Resolve("event.isolated".AsXsrId());

        // The live subscriber pins the oldest record, so a full scope must reject publication
        // instead of dropping a record the subscriber still needs.
        using XsrEventSubscription<StepEvent> subscription = router.Subscribe<StepEvent>(isolated);

        AssertTrue(router.Publish(isolated, new StepEvent(1)).IsSuccess);
        AssertTrue(router.Publish(isolated, new StepEvent(2)).IsSuccess);
        XsrResult rejected = router.Publish(isolated, new StepEvent(3));
        AssertFalse(rejected.IsSuccess);
        AssertEqual(XsrRuntimeErrors.BackpressureCode, RequiredError(rejected.Error).Code);
        AssertTrue(router.TryGetQueueDepth(isolated, null, out int depth));
        AssertEqual(2, depth);

        // Reading one record frees the consumed slot; publication resumes without loss.
        AssertEqual(1L, RequireValue(RequireResult(subscription.ReadAsync().AsTask())).Record.Sequence);
        AssertTrue(router.Publish(isolated, new StepEvent(3)).IsSuccess);
        AssertEqual(2L, RequireValue(RequireResult(subscription.ReadAsync().AsTask())).Record.Sequence);
        AssertEqual(3L, RequireValue(RequireResult(subscription.ReadAsync().AsTask())).Record.Sequence);
    }

    private static void EventsEvictFreelyWithoutSubscribers()
    {
        XsrEventRouter router = BuildIsolatedRouter(capacity: 2);
        XsrEventId isolated = router.Resolve("event.isolated".AsXsrId());

        for (int index = 1; index <= 5; index++)
        {
            AssertTrue(router.Publish(isolated, new StepEvent(index)).IsSuccess);
        }

        AssertTrue(router.TryGetQueueDepth(isolated, null, out int depth));
        AssertEqual(2, depth);

        // A new subscription can only replay the retained window.
        XsrEventSubscription<StepEvent> subscription =
            router.Subscribe<StepEvent>(isolated, replayFromSequence: 4);
        AssertEqual(4L, RequireValue(RequireResult(subscription.ReadAsync().AsTask())).Record.Sequence);
        AssertEqual(5L, RequireValue(RequireResult(subscription.ReadAsync().AsTask())).Record.Sequence);

        XsrEventSubscription<StepEvent> expired =
            router.Subscribe<StepEvent>(isolated, replayFromSequence: 1);
        XsrResult<XsrEventDelivery<StepEvent>> expiredRead = RequireResult(expired.ReadAsync().AsTask());
        AssertEqual(XsrRuntimeErrors.NotRetainedCode, RequiredError(expiredRead.Error).Code);
    }

    private static void EventsReplayRetainedRecordsThenContinueLive()
    {
        XsrEventRouter router = BuildTestRouter().Build();
        XsrEventId step = router.Resolve("event.step".AsXsrId());

        for (int index = 1; index <= 3; index++)
        {
            AssertTrue(router.Publish(step, new StepEvent(index)).IsSuccess);
        }

        XsrEventSubscription<StepEvent> replay = router.Subscribe<StepEvent>(step, replayFromSequence: 2);

        AssertEqual(2L, RequireValue(RequireResult(replay.ReadAsync().AsTask())).Record.Sequence);
        AssertEqual(3L, RequireValue(RequireResult(replay.ReadAsync().AsTask())).Record.Sequence);

        // The replayed subscription continues live once it caught up.
        AssertTrue(router.Publish(step, new StepEvent(4)).IsSuccess);
        AssertEqual(4L, RequireValue(RequireResult(replay.ReadAsync().AsTask())).Record.Sequence);
    }

    private static async ValueTask EventsCancellationReturnsStableError()
    {
        XsrEventRouter router = BuildTestRouter().Build();
        XsrEventSubscription<StepEvent> subscription =
            router.Subscribe<StepEvent>(router.Resolve("event.step".AsXsrId()));

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        XsrResult<XsrEventDelivery<StepEvent>> result =
            await subscription.ReadAsync(cancellation.Token).ConfigureAwait(false);

        AssertFalse(result.IsSuccess);
        AssertEqual(XsrRuntimeErrors.CancelledCode, RequiredError(result.Error).Code);
    }

    private static void EventsRejectContractMismatchesAndUnknownRoutes()
    {
        XsrEventRouter router = BuildTestRouter().Build();
        XsrEventId step = router.Resolve("event.step".AsXsrId());

        XsrResult mismatch = router.Publish(step, new CompletedEvent("other"));
        AssertEqual(XsrRuntimeErrors.ContractMismatchCode, RequiredError(mismatch.Error).Code);

        XsrResult unknown = router.Publish(new XsrEventId(new XsrRuntimeId(999)), new StepEvent(1));
        AssertEqual(XsrRuntimeErrors.RouteNotFoundCode, RequiredError(unknown.Error).Code);

        AssertThrows<InvalidOperationException>(() => router.Subscribe<CompletedEvent>(step));
    }

    private static void EventsObserveEveryPublicationWithoutBlockingIt()
    {
        RecordingEventObserver observer = new();
        XsrEventRouter router = BuildTestRouter().Build(observer);
        XsrEventId step = router.Resolve("event.step".AsXsrId());

        AssertTrue(router.Publish(step, new StepEvent(1)).IsSuccess);
        AssertTrue(router.Publish(step, new StepEvent(2)).IsSuccess);

        AssertEqual(2, observer.Publications.Length);
        AssertEqual(1L, observer.Publications[0].Sequence);
        AssertEqual(2L, observer.Publications[1].Sequence);
        AssertEqual(step, observer.Publications[0].EventId);

        ThrowingEventObserver throwing = new();
        XsrEventRouter throwingRouter = BuildTestRouter().Build(throwing);
        AssertTrue(throwingRouter
            .Publish(throwingRouter.Resolve("event.step".AsXsrId()), new StepEvent(9))
            .IsSuccess);
    }

    private static async ValueTask EventsPreserveScopeOrderUnderConcurrentPublishers()
    {
        XsrEventRouterBuilder builder = new();
        builder.DeclareScope("scope.concurrent".AsXsrId(), capacity: 512);
        builder.Register<StepEvent>(
            "event.concurrent".AsXsrId(),
            "scope.concurrent".AsXsrId(),
            XsrEventOrdering.Global);
        XsrEventRouter router = builder.Build();
        XsrEventId step = router.Resolve("event.concurrent".AsXsrId());

        // The subscriber attaches before publication, so no record can be evicted and no
        // publication can be rejected: bounded delivery is observable instead.
        using XsrEventSubscription<StepEvent> subscription = router.Subscribe<StepEvent>(step);

        await Parallel.ForAsync(0, 8, async (iteration, cancellationToken) =>
        {
            for (int index = 0; index < 50; index++)
            {
                XsrResult result = router.Publish(
                    step,
                    new StepEvent(iteration * 100 + index),
                    cancellationToken: cancellationToken);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException("A concurrent publication was rejected.");
                }

                await Task.Yield();
            }
        });

        long previous = 0;
        for (int index = 0; index < 400; index++)
        {
            XsrResult<XsrEventDelivery<StepEvent>> delivery =
                await subscription.ReadAsync().ConfigureAwait(false);
            AssertEqual(previous + 1, RequireValue(delivery).Record.Sequence);
            previous = RequireValue(delivery).Record.Sequence;
        }

        AssertEqual(400L, previous);
    }

    private static void EventsRejectUndeclaredScopeAndDuplicateScope()
    {
        XsrEventRouterBuilder missingScope = new();
        AssertThrows<InvalidOperationException>(
            () => missingScope.Register<StepEvent>(
                "event.orphan".AsXsrId(),
                "scope.missing".AsXsrId(),
                XsrEventOrdering.Global));

        XsrEventRouterBuilder duplicatedScope = new();
        duplicatedScope.DeclareScope("scope.download".AsXsrId(), capacity: 8);
        AssertThrows<InvalidOperationException>(
            () => duplicatedScope.DeclareScope("scope.download".AsXsrId(), capacity: 8));

        XsrEventRouterBuilder invalidCapacity = new();
        AssertThrows<ArgumentOutOfRangeException>(
            () => invalidCapacity.DeclareScope("scope.bad".AsXsrId(), capacity: 0));
    }

    private static (string, long) ReadPayload(XsrEventSubscription<TypedEvent> subscription)
    {
        XsrEventDelivery<TypedEvent> delivery =
            RequireValue(RequireResult(subscription.ReadAsync().AsTask()));
        return (delivery.Payload.Key, delivery.Record.Sequence);
    }

    private static XsrEventRouterBuilder BuildTestRouter(bool reversed = false)
    {
        XsrEventRouterBuilder builder = new();
        builder.DeclareScope("scope.download".AsXsrId(), capacity: 64);
        builder.DeclareScope("scope.typed".AsXsrId(), capacity: 8);

        if (reversed)
        {
            builder.Register<TypedEvent>("event.typed".AsXsrId(), "scope.typed".AsXsrId(), XsrEventOrdering.PerKey);
            builder.Register<CompletedEvent>(
                "event.completed".AsXsrId(),
                "scope.download".AsXsrId(),
                XsrEventOrdering.Global);
            builder.Register<StepEvent>("event.step".AsXsrId(), "scope.download".AsXsrId(), XsrEventOrdering.Global);
        }
        else
        {
            builder.Register<StepEvent>("event.step".AsXsrId(), "scope.download".AsXsrId(), XsrEventOrdering.Global);
            builder.Register<CompletedEvent>(
                "event.completed".AsXsrId(),
                "scope.download".AsXsrId(),
                XsrEventOrdering.Global);
            builder.Register<TypedEvent>("event.typed".AsXsrId(), "scope.typed".AsXsrId(), XsrEventOrdering.PerKey);
        }

        return builder;
    }

    private static XsrEventRouter BuildIsolatedRouter(int capacity)
    {
        XsrEventRouterBuilder builder = new();
        builder.DeclareScope("scope.isolated".AsXsrId(), capacity: capacity);
        builder.Register<StepEvent>("event.isolated".AsXsrId(), "scope.isolated".AsXsrId(), XsrEventOrdering.Global);
        return builder.Build();
    }

    private static XsrResult<T> RequireResult<T>(Task<XsrResult<T>> task)
    {
        task.Wait();
        return task.Result;
    }

    private static XsrEventDelivery<TEvent> RequireValue<TEvent>(XsrResult<XsrEventDelivery<TEvent>> result)
        where TEvent : notnull
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Expected a delivered event but received '{result.Error?.Code}'.");
        }

        return result.Value;
    }

    private sealed class RecordingEventObserver : IXsrEventObserver
    {
        private readonly List<XsrEventPublication> _publications = [];
        private readonly object _gate = new();

        public XsrEventPublication[] Publications
        {
            get
            {
                lock (_gate)
                {
                    return [.. _publications];
                }
            }
        }

        public void OnPublished(XsrEventPublication publication)
        {
            lock (_gate)
            {
                _publications.Add(publication);
            }
        }
    }

    private sealed class ThrowingEventObserver : IXsrEventObserver
    {
        public void OnPublished(XsrEventPublication publication) =>
            throw new InvalidOperationException("observer failure");
    }

    private sealed record StepEvent(int Index);

    private sealed record CompletedEvent(string Reason);

    private sealed record TypedEvent(string Key);
}

file static class XsrEventSemanticIdExtensions
{
    public static XsrSemanticId AsXsrId(this string value) => XsrSemanticId.Parse(value);
}
