# XSR-104 ordered events, scopes, and bounded delivery

## Outcome

Wave 1 adds typed asynchronous event publication and ordered delivery on top of the deterministic registry. Events are declared with an ordering scope; every scope instance is one bounded ring with a contiguous sequence space shared by all events routed into it. Publication never blocks and never silently drops: a full scope rejects publication with the stable backpressure error.

## Locked contract

- Events are declared through closed generic registrations in a declared ordering scope (`Global` or `PerKey`) and sealed before use; runtime IDs are deterministic through the shared registry.
- One declared scope is one ordering domain. `Global` scopes own a single instance; `PerKey` scopes own one instance per non-empty caller key, and a missing key is a caller error. Events of different contracts inside one scope share one sequence space, so cross-event order is total inside the scope.
- Delivery is pull-based: a subscription is one cursor over the scope ring. It delivers only its own event's records in scope order, skipping sibling records without losing sequence continuity. A subscription supports one concurrent reader.
- Bounded delivery is observable: the ring retains a bounded window; when it is full and a live subscriber still needs the oldest retained record, publication is rejected with `xsr.backpressure` and nothing is dropped. Without live cursors the ring evicts freely, and queue depth is observable per scope.
- Consumers tolerate replay and duplication: a subscription may replay from a retained sequence and then continues live. Requesting an expired sequence returns the stable `xsr.event_not_retained` error, directing the consumer back to state for a fresh snapshot.
- Cancellation while waiting for delivery returns the stable `xsr.cancelled` error. Publication accepts cancellation and validates it before enqueuing.
- Every accepted publication is reported to the optional observer with sequence, scope, correlation ID, and timestamp; observer failures never affect publication or delivery. Correlation IDs are created when the caller omits them.
- Scope instance keys are runtime data (like command payloads), not semantic lookups: scope definitions and event routes are resolved numerically at the routing boundary.

## Non-goals

This unit does not introduce transport serialization, cross-process delivery, durability guarantees, event persistence, fan-out trees, or lifecycle shutdown. Sidecar event transport and crash-recovery replay are Wave 4 units; the scope ring, sequence, and backpressure surfaces defined here are the contracts they build on.

## Verification

`PCL.Xsr.Runtime.Tests` covers deterministic event IDs, shared sequence space across event contracts in one scope, independent per-key ordering, required scope keys, backpressure rejection with a live subscriber, free eviction without subscribers, bounded replay with live continuation, expired-replay rejection, cancellation, contract-mismatch and unknown-route rejections, observer coverage and isolation, scope-order preservation under concurrent publishers, and scope declaration validation. The NativeAOT, trim, and architecture gates exercise the event router without new project dependencies.
