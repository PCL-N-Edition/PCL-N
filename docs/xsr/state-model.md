# XSR state model

## Principle

State is the observable truth of the running system. A ViewModel property, renderer-local business field, service-private mirror, or remote Sidecar object is not an alternative source of truth.

## Primitives

```text
StateStore
|- StateCell<T>        one typed value
|- StateCollection<T>  revisioned collection snapshot/delta
|- DerivedState<T>     value computed from declared dependencies
|- StateSnapshot       immutable read view at a revision
`- StateDelta          ordered change from a known revision
```

Every public state has a semantic development ID, a compact runtime ID, a value contract, an owner, a revision, and an availability status. Runtime IDs may change between Sidecar sessions; semantic IDs and protocol contracts do not.

## Ownership

- One service or runtime component is the writer for a state cell.
- Multiple readers consume immutable snapshots.
- Derived state declares dependencies and is recomputed only after an input revision changes.
- Renderers never mutate business state directly. They emit commands or intents.
- Sidecar state is mirrored into Host state before the renderer can observe it.

## Update semantics

State publication is asynchronous. A producer publishes a new value or delta; the store assigns the next revision and invalidates only dependent nodes. Readers see a coherent snapshot, never a partially applied delta.

Coalescing is permitted for replaceable state such as progress or throughput. The latest value must win, coalescing must be measurable, and durable events must not be dropped under the same policy.

Collections need an explicit identity and ordering contract. A collection delta that cannot be applied to the reader's base revision triggers a snapshot refresh rather than best-effort mutation.

## Availability

Remote state carries availability separately from its last value:

```text
Available | Stale | Unavailable
```

A Sidecar crash marks the mirror stale or unavailable without destroying its UI module. Reconnect creates a new session, performs full registration, and publishes a fresh snapshot before activation.

## Renderer contract

Render work reads an immutable local snapshot and registers state dependencies. It performs no query, IPC, blocking wait, or service lookup. State changes mark only dependent semantic entities dirty.

## Verification

State implementations require tests for revision ordering, snapshot consistency, derived dependency propagation, cancellation, coalescing, bounded queues, stale transitions, reconnect snapshots, and deterministic replay where applicable.
