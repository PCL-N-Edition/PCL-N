using PCL.Xsr.State;

namespace PCL.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static void StateAssignsDeterministicIdentifiersAndOwnership()
    {
        XsrStateStoreBuilder first = new();
        first.Cell<int>("download.progress".AsXsrId(), "Download");
        first.Cell<int>("launch.status".AsXsrId(), "Launch");

        XsrStateStoreBuilder second = new();
        second.Cell<int>("launch.status".AsXsrId(), "Launch");
        second.Cell<int>("download.progress".AsXsrId(), "Download");

        XsrStateStore firstStore = first.Build();
        XsrStateStore secondStore = second.Build();

        XsrStateId firstProgress = firstStore.Resolve("download.progress".AsXsrId());
        XsrStateId secondProgress = secondStore.Resolve("download.progress".AsXsrId());
        XsrStateId firstStatus = firstStore.Resolve("launch.status".AsXsrId());

        AssertEqual(firstProgress, secondProgress);
        AssertEqual(1u, firstProgress.Value.Value);
        AssertEqual(2u, firstStatus.Value.Value);
        AssertEqual("Download", firstStore.Describe(firstProgress).Owner);
        AssertTrue(firstStore.Describe(firstProgress).Kind == XsrStateKind.Cell);
        AssertFalse(firstStore.TryResolve("unknown.state".AsXsrId(), out _));
    }

    private static void StateCellsStartUnavailableUntilPublished()
    {
        XsrStateStore store = BuildSimpleStore(out XsrStateId progress);

        XsrStateValue<int> initial = store.Read<int>(progress);
        AssertFalse(initial.HasValue);
        AssertTrue(initial.Availability == XsrStateAvailability.Unavailable);
        AssertEqual(0L, initial.Revision);

        long revision = store.Publish(progress, 42);
        XsrStateValue<int> published = store.Read<int>(progress);

        AssertEqual(1L, revision);
        AssertEqual(1L, published.Revision);
        AssertTrue(published.IsAvailable);
        AssertEqual(42, published.Value);
    }

    private static void StateRevisionsAreMonotonicPerEntry()
    {
        XsrStateStore store = BuildSimpleStore(out XsrStateId progress);

        long first = store.Publish(progress, 1);
        long second = store.Publish(progress, 2);
        long third = store.Publish(progress, 3);

        AssertTrue(first < second);
        AssertTrue(second < third);
        AssertEqual(3L, store.Read<int>(progress).Revision);
        AssertEqual(3, store.Read<int>(progress).Value);
    }

    private static void StateReadsSeeCoherentSnapshots()
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("cell.alpha".AsXsrId(), "Owner");
        builder.Cell<string>("cell.beta".AsXsrId(), "Owner");
        XsrStateStore store = builder.Build();

        XsrStateId alpha = store.Resolve("cell.alpha".AsXsrId());
        XsrStateId beta = store.Resolve("cell.beta".AsXsrId());
        _ = store.Publish(alpha, 1);
        _ = store.Publish(beta, "one");

        XsrStateSnapshot snapshot = store.CaptureSnapshot();

        AssertEqual(2, snapshot.Entries.Count);
        AssertTrue(snapshot.Entries[0].Revision == 1 && snapshot.Entries[1].Revision == 1);
        AssertTrue(snapshot.Entries.All(entry => entry.Availability == XsrStateAvailability.Available));

        // Mutations after capture never leak into the immutable snapshot.
        _ = store.Publish(alpha, 2);
        XsrStateSnapshot second = store.CaptureSnapshot();
        AssertTrue(second.Entries[0].Revision == 2);
        AssertTrue(snapshot.Entries[0].Revision == 1);
    }

    private static void StateCoalescingAppliesLatestAndCountsReplaced()
    {
        XsrStateStore store = BuildSimpleStore(out XsrStateId progress);

        store.PublishCoalesced(progress, 1);
        store.PublishCoalesced(progress, 2);
        store.PublishCoalesced(progress, 3);

        XsrStateValue<int> value = store.Read<int>(progress);
        AssertEqual(3, value.Value);
        AssertEqual(1L, value.Revision);
        AssertEqual(2L, store.CoalescedCount(progress));

        // An immediate publication applies the deferred value first, in publication order.
        long revision = store.Publish(progress, 4);
        AssertEqual(2L, revision);
        AssertEqual(2L, store.CoalescedCount(progress));
        AssertEqual(4, store.Read<int>(progress).Value);
    }

    private static void StateCollectionDeltasApplyAgainstMatchingBase()
    {
        XsrStateStoreBuilder builder = new();
        builder.Collection<NamedItem, string>(
            "instances.list".AsXsrId(),
            "Instances",
            static item => item.Name,
            StringComparer.Ordinal);
        XsrStateStore store = builder.Build();
        XsrStateId list = store.Resolve("instances.list".AsXsrId());

        XsrCollectionDelta<NamedItem, string> initial = new(
            0,
            [new NamedItem("beta", 1), new NamedItem("alpha", 1)],
            []);
        XsrCollectionApplyResult applied = store.PublishDelta(list, initial);
        AssertTrue(applied.IsApplied);
        AssertEqual(1L, applied.Revision);

        XsrCollectionSnapshot<NamedItem> afterInitial = store.ReadCollection<NamedItem>(list);
        AssertEqual(2, afterInitial.Count);
        AssertEqual("alpha", afterInitial.Items[0].Name);
        AssertEqual("beta", afterInitial.Items[1].Name);

        XsrCollectionDelta<NamedItem, string> update = new(
            1,
            [new NamedItem("gamma", 2)],
            ["alpha"]);
        XsrCollectionApplyResult updated = store.PublishDelta(list, update);
        AssertTrue(updated.IsApplied);

        XsrCollectionSnapshot<NamedItem> afterUpdate = store.ReadCollection<NamedItem>(list);
        AssertEqual(2, afterUpdate.Count);
        AssertEqual("beta", afterUpdate.Items[0].Name);
        AssertEqual("gamma", afterUpdate.Items[1].Name);

        // A delta whose base revision no longer matches is rejected without mutation;
        // the caller refreshes a snapshot instead of mutating best-effort.
        XsrCollectionDelta<NamedItem, string> stale = new(0, [new NamedItem("delta", 1)], []);
        XsrCollectionApplyResult rejected = store.PublishDelta(list, stale);
        AssertFalse(rejected.IsApplied);
        XsrCollectionSnapshot<NamedItem> unchanged = store.ReadCollection<NamedItem>(list);
        AssertEqual(2, unchanged.Count);
        AssertEqual(2L, unchanged.Revision);
    }

    private static void StateDerivedRecomputesOnlyWhenInputsChange()
    {
        XsrStateStore store = BuildProgressStore(
            out XsrStateId received,
            out XsrStateId total,
            out XsrStateId percent,
            out ComputeCounter counter);

        _ = store.Publish(received, 0);
        _ = store.Publish(total, 200);

        XsrStateValue<int> first = store.Read<int>(percent);
        AssertEqual(0, first.Value);
        AssertEqual(1, counter.Count);

        // A repeated read without input changes does not recompute.
        _ = store.Read<int>(percent);
        AssertEqual(1, counter.Count);

        _ = store.Publish(received, 100);
        XsrStateValue<int> second = store.Read<int>(percent);
        AssertEqual(50, second.Value);
        AssertEqual(2, counter.Count);

        // Publishing the same value still advances the input revision, so the derived entry
        // recomputes; the unchanged result does not advance the derived revision itself.
        _ = store.Publish(received, 100);
        XsrStateValue<int> repeated = store.Read<int>(percent);
        AssertEqual(3, counter.Count);
        AssertEqual(50, repeated.Value);
        AssertEqual(second.Revision, repeated.Revision);
    }

    private static void StateDerivedChainsPropagateInOrder()
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("chain.input".AsXsrId(), "Owner");
        builder.Derived<int>(
            "chain.doubled".AsXsrId(),
            "Derived",
            ["chain.input".AsXsrId()],
            static (reader, cancellationToken) => reader.Read<int>(
                reader.Resolve("chain.input".AsXsrId()),
                cancellationToken).Value * 2);
        builder.Derived<string>(
            "chain.labeled".AsXsrId(),
            "Derived",
            ["chain.doubled".AsXsrId()],
            static (reader, cancellationToken) =>
                $"value={reader.Read<int>(reader.Resolve("chain.doubled".AsXsrId()), cancellationToken).Value}");
        XsrStateStore store = builder.Build();

        XsrStateId input = store.Resolve("chain.input".AsXsrId());
        XsrStateId labeled = store.Resolve("chain.labeled".AsXsrId());

        _ = store.Publish(input, 21);
        AssertEqual("value=42", store.Read<string>(labeled).Value);

        _ = store.Publish(input, -1);
        AssertEqual("value=-2", store.Read<string>(labeled).Value);
    }

    private static void StateDerivedUnevenDependencyRevisionsInvalidate()
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("uneven.a".AsXsrId(), "Owner");
        builder.Cell<int>("uneven.b".AsXsrId(), "Owner");
        ComputeCounter counter = new();
        builder.Derived<int>(
            "uneven.sum".AsXsrId(),
            "Derived",
            ["uneven.a".AsXsrId(), "uneven.b".AsXsrId()],
            (reader, cancellationToken) =>
            {
                counter.Increment();
                return reader.Read<int>(reader.Resolve("uneven.a".AsXsrId()), cancellationToken).Value
                    + reader.Read<int>(reader.Resolve("uneven.b".AsXsrId()), cancellationToken).Value;
            });
        XsrStateStore store = builder.Build();
        XsrStateId a = store.Resolve("uneven.a".AsXsrId());
        XsrStateId b = store.Resolve("uneven.b".AsXsrId());
        XsrStateId sum = store.Resolve("uneven.sum".AsXsrId());

        // A's per-entry revision races far ahead of B's local counter.
        for (int index = 1; index <= 10; index++)
        {
            _ = store.Publish(a, index);
        }

        _ = store.Publish(b, 100);
        AssertEqual(110, store.Read<int>(sum).Value);
        AssertEqual(1, counter.Count);

        // B changes again: despite its low local revision, the derived entry must recompute.
        _ = store.Publish(b, 200);
        AssertEqual(210, store.Read<int>(sum).Value);
        AssertEqual(2, counter.Count);
    }

    private static async ValueTask StateDerivedMutationDuringFirstComputeDoesNotCommitStaleValue()
    {
        TaskCompletionSource computeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCompute = new(TaskCreationOptions.RunContinuationsAsynchronously);
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("race.input".AsXsrId(), "Owner");
        ComputeCounter counter = new();
        builder.Derived<int>(
            "race.doubled".AsXsrId(),
            "Derived",
            ["race.input".AsXsrId()],
            (reader, cancellationToken) =>
            {
                counter.Increment();
                // The input is captured before the gate so the first computation observes the
                // pre-mutation value while the mutation lands underneath it.
                int captured = reader.Read<int>(reader.Resolve("race.input".AsXsrId()), cancellationToken).Value;
                computeStarted.TrySetResult();
                releaseCompute.Task.Wait(cancellationToken);
                return captured * 2;
            });
        XsrStateStore store = builder.Build();
        XsrStateId input = store.Resolve("race.input".AsXsrId());
        XsrStateId doubled = store.Resolve("race.doubled".AsXsrId());

        Task<XsrStateValue<int>> readTask = Task.Run(() => store.Read<int>(doubled));
        await computeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _ = store.Publish(input, 21);
        releaseCompute.TrySetResult();

        // The stale first computation must not be committed as current: the read retries and
        // returns the value computed against the new input.
        XsrStateValue<int> result = await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEqual(42, result.Value);
        AssertTrue(result.IsAvailable);
        AssertEqual(2, counter.Count);

        // The committed value is cached: a repeated read does not recompute.
        _ = store.Read<int>(doubled);
        AssertEqual(2, counter.Count);
    }

    private static void StateDerivedGraphRejectsCycles()
    {
        XsrStateStoreBuilder builder = new();
        builder.Derived<int>(
            "cycle.a".AsXsrId(),
            "Derived",
            ["cycle.b".AsXsrId()],
            static (_, _) => 0);
        builder.Derived<int>(
            "cycle.b".AsXsrId(),
            "Derived",
            ["cycle.a".AsXsrId()],
            static (_, _) => 0);

        AssertThrows<InvalidOperationException>(() => builder.Build());
    }

    private static void StateDerivedRejectsUndeclaredDependency()
    {
        XsrStateStoreBuilder builder = new();
        builder.Derived<int>(
            "derived.orphan".AsXsrId(),
            "Derived",
            ["missing.state".AsXsrId()],
            static (_, _) => 0);

        AssertThrows<InvalidOperationException>(() => builder.Build());
    }

    private static void StateAvailabilityIsSeparateFromValue()
    {
        XsrStateStore store = BuildSimpleStore(out XsrStateId progress);
        _ = store.Publish(progress, 42);

        bool staleChanged = store.MarkAvailability(progress, XsrStateAvailability.Stale);
        XsrStateValue<int> stale = store.Read<int>(progress);

        AssertTrue(staleChanged);
        AssertTrue(stale.Availability == XsrStateAvailability.Stale);
        AssertTrue(stale.HasValue);
        AssertEqual(42, stale.Value);
        AssertEqual(2L, stale.Revision);

        // Marking the same availability again is a no-op.
        AssertFalse(store.MarkAvailability(progress, XsrStateAvailability.Stale));

        bool unavailableChanged = store.MarkAvailability(progress, XsrStateAvailability.Unavailable);
        XsrStateValue<int> unavailable = store.Read<int>(progress);
        AssertTrue(unavailableChanged);
        AssertTrue(unavailable.Availability == XsrStateAvailability.Unavailable);
        AssertTrue(unavailable.HasValue);
        AssertEqual(42, unavailable.Value);

        // A fresh publication restores availability.
        _ = store.Publish(progress, 43);
        XsrStateValue<int> restored = store.Read<int>(progress);
        AssertTrue(restored.IsAvailable);
        AssertEqual(43, restored.Value);
    }

    private static void StateObserversSeeOrderedChangesAndCannotBreakPublication()
    {
        RecordingStateObserver observer = new();
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("observed.cell".AsXsrId(), "Owner");
        XsrStateStore store = builder.Build(observer);
        XsrStateId cell = store.Resolve("observed.cell".AsXsrId());

        _ = store.Publish(cell, 1);
        store.PublishCoalesced(cell, 2);
        store.PublishCoalesced(cell, 3);
        _ = store.Read<int>(cell);
        _ = store.MarkAvailability(cell, XsrStateAvailability.Stale);

        AssertEqual(3, observer.Changes.Length);
        AssertEqual(XsrStateChangeReason.ValuePublished, observer.Changes[0].Reason);
        AssertEqual(XsrStateChangeReason.CoalescedApplied, observer.Changes[1].Reason);
        AssertEqual(XsrStateChangeReason.AvailabilityChanged, observer.Changes[2].Reason);
        AssertEqual(1L, observer.Changes[0].Revision);
        AssertEqual(2L, observer.Changes[1].Revision);
        AssertEqual(3L, observer.Changes[2].Revision);
        AssertTrue(observer.Changes[0].Id.Equals(cell));

        // A throwing observer never blocks publication.
        ThrowingStateObserver throwing = new();
        XsrStateStoreBuilder throwingBuilder = new();
        throwingBuilder.Cell<int>("throwing.cell".AsXsrId(), "Owner");
        XsrStateStore throwingStore = throwingBuilder.Build(throwing);
        XsrStateId throwingCell = throwingStore.Resolve("throwing.cell".AsXsrId());
        long revision = throwingStore.Publish(throwingCell, 7);
        AssertEqual(1L, revision);
        AssertEqual(7, throwingStore.Read<int>(throwingCell).Value);
    }

    private static void StateContractMismatchesAreRejected()
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("typed.cell".AsXsrId(), "Owner");
        builder.Collection<NamedItem, string>(
            "typed.collection".AsXsrId(),
            "Owner",
            static item => item.Name,
            StringComparer.Ordinal);
        XsrStateStore store = builder.Build();
        XsrStateId cell = store.Resolve("typed.cell".AsXsrId());
        XsrStateId collection = store.Resolve("typed.collection".AsXsrId());

        AssertThrows<InvalidOperationException>(() => store.Read<string>(cell));
        AssertThrows<InvalidOperationException>(() => store.Publish(cell, "text"));
        AssertThrows<InvalidOperationException>(() => store.ReadCollection<string>(collection));
        AssertThrows<InvalidOperationException>(() => store.ReadCollection<NamedItem>(cell));
        AssertThrows<ArgumentException>(() => store.Read<int>(new XsrStateId(new XsrRuntimeId(999))));
    }

    private static void StateBuilderRejectsReuseAndDuplicates()
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("build.once".AsXsrId(), "Owner");
        _ = builder.Build();

        AssertThrows<InvalidOperationException>(() => builder.Build());
        AssertThrows<InvalidOperationException>(() => builder.Cell<int>("build.twice".AsXsrId(), "Owner"));

        XsrStateStoreBuilder duplicated = new();
        duplicated.Cell<int>("duplicate.cell".AsXsrId(), "Owner");
        AssertThrows<InvalidOperationException>(
            () => duplicated.Cell<string>("duplicate.cell".AsXsrId(), "Owner"));
    }

    private static void StateSupportsConcurrentReadersAndPublishers()
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("concurrent.cell".AsXsrId(), "Owner");
        RecordingStateObserver observer = new();
        XsrStateStore store = builder.Build(observer);
        XsrStateId cell = store.Resolve("concurrent.cell".AsXsrId());

        Parallel.For(0, 8, iteration =>
        {
            for (int index = 0; index < 500; index++)
            {
                _ = store.Publish(cell, iteration * 1000 + index);
                XsrStateValue<int> value = store.Read<int>(cell);
                if (!value.IsAvailable || value.Revision < 1)
                {
                    throw new InvalidOperationException("A concurrent state read observed an invalid value.");
                }
            }
        });

        AssertEqual(4_000, store.Read<int>(cell).Revision);
        AssertEqual(4_000, observer.Changes.Length);
    }

    private static XsrStateStore BuildSimpleStore(out XsrStateId progress)
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("download.progress".AsXsrId(), "Download");
        XsrStateStore store = builder.Build();
        progress = store.Resolve("download.progress".AsXsrId());
        return store;
    }

    private static XsrStateStore BuildProgressStore(
        out XsrStateId received,
        out XsrStateId total,
        out XsrStateId percent,
        out ComputeCounter counter)
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<int>("download.received".AsXsrId(), "Download");
        builder.Cell<int>("download.total".AsXsrId(), "Download");
        ComputeCounter computeCounter = new();
        builder.Derived<int>(
            "download.percent".AsXsrId(),
            "Derived",
            ["download.received".AsXsrId(), "download.total".AsXsrId()],
            (reader, cancellationToken) =>
            {
                computeCounter.Increment();
                XsrStateValue<int> receivedValue = reader.Read<int>(
                    reader.Resolve("download.received".AsXsrId()),
                    cancellationToken);
                XsrStateValue<int> totalValue = reader.Read<int>(
                    reader.Resolve("download.total".AsXsrId()),
                    cancellationToken);
                return totalValue.Value == 0 ? 0 : receivedValue.Value * 100 / totalValue.Value;
            });
        XsrStateStore store = builder.Build();
        received = store.Resolve("download.received".AsXsrId());
        total = store.Resolve("download.total".AsXsrId());
        percent = store.Resolve("download.percent".AsXsrId());
        counter = computeCounter;
        return store;
    }

    private sealed class ComputeCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private readonly record struct NamedItem(string Name, int Value);

    private sealed class RecordingStateObserver : IXsrStateObserver
    {
        private readonly List<XsrStateChange> _changes = [];
        private readonly object _gate = new();

        public XsrStateChange[] Changes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _changes];
                }
            }
        }

        public void OnChanged(XsrStateChange change)
        {
            lock (_gate)
            {
                _changes.Add(change);
            }
        }
    }

    private sealed class ThrowingStateObserver : IXsrStateObserver
    {
        public void OnChanged(XsrStateChange change) => throw new InvalidOperationException("observer failure");
    }
}

file static class XsrSemanticIdExtensions
{
    public static XsrSemanticId AsXsrId(this string value) => XsrSemanticId.Parse(value);
}
