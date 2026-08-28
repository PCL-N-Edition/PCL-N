# XSR-103 revisioned state

## Outcome

Wave 1 adds the revisioned state store behind the locked state model: typed cells, ordered collections with identity and ordering contracts, whole-store snapshots, collection deltas, and a derived-state dependency graph. State entries are declared through the deterministic sealed registry, so runtime IDs stay contiguous, nonzero, and identical across routing and state registration.

## Locked contract

- Every entry has a semantic ID, a compact runtime ID, a value contract, an owner, a monotonic per-entry revision, and an availability status. A fresh entry is unavailable until its first publication.
- Writers publish through typed cell publication or collection deltas. The store assigns the next revision per entry; readers receive value, revision, and availability together and never observe a partially applied delta.
- Replaceable values may publish with latest-wins coalescing: deferred publication applies at the next read, snapshot capture, or immediate publication, replaced intermediates never allocate revisions, and the replaced count is observable through `CoalescedCount`. Durable state must not use this policy.
- Collection deltas apply only against a matching base revision. A stale base revision is rejected without mutation; the caller refreshes a snapshot. Item identity uses the key contract's default equality, and ordering uses the declared comparer.
- Derived entries declare dependencies and recompute only after an input changes. Invalidation uses the store-global change stamp assigned by every applied mutation, not per-entry revisions, because local counters cannot be compared across entries. A compute result is committed only when no dependency mutation happened between the watermark capture and the compute return — including the first computation; under sustained input movement the read returns the last applied value and retries on the next read. Derived entries do not advance their revision when the recomputed value is unchanged, and reject undeclared dependencies and cycles at build time.
- Availability changes independently of the value: marking an entry stale or unavailable retains the last value, advances the revision so dependents invalidate, and a fresh publication restores availability. Derived entries derive availability and reject explicit marking.
- Cancellation aborts a read or publication before it starts and is handed to derived computations.
- Observers receive every applied change, including coalesced application and derived recomputation. Observer failures never affect publication or readers.

## Registry placement

The deterministic sealed registry (`XsrRegistry`, `XsrRegistrySnapshot`, `XsrRegistryEntry`) moved from `PCL.Xsr.Runtime` to `PCL.Xsr.Abstractions` with namespace `PCL.Xsr`. Registration identity is shared kernel: routing, state, events, transport, and generated code must resolve the identical deterministic mapping, and `PCL.Xsr.State` cannot depend on `PCL.Xsr.Runtime`. The sealing rule is unchanged.

## Non-goals

This unit does not introduce remote mirroring, reconnect snapshots, transport serialization, event delivery, scopes, scheduling, or lifecycle. Reconnect snapshot refresh and deterministic replay over transport are exercised by the later Sidecar mirror unit; the store already provides the revision and availability surfaces they require.

## Verification

`PCL.Xsr.Runtime.Tests` covers deterministic identifiers and ownership, unavailable-until-published transitions, revision monotonicity, snapshot coherence and immutability, coalescing with replace counting, delta application and stale-base rejection, derived recompute-on-revision-change, chain propagation, cycle and undeclared-dependency rejection, availability transitions, observer ordering and isolation, contract-mismatch rejection, builder reuse and duplicate rejection, and concurrent publication. `PCL.Xsr.State` is AOT-compatible and exercised by the NativeAOT runtime-test gate, the trimmed Desktop publish, and the architecture gate.
