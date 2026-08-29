# XSR-404 data plane, crash recovery, and reconnect

## Outcome

Wave 4 closes with the data plane and failure semantics: command and query forwarding with correlated results, state deltas publishing into the session mirror, ordered event delivery, bounded pending exchanges, crash and stream-failure handling, and the reconnect contract where a new session replaces the mirror with a fresh coherent snapshot.

## Locked contract

- Request/response: `SendCommandAsync`/`SendQueryAsync` require the Active state, allocate a fresh correlation ID, and await the correlated result. Results complete exactly one pending exchange; a late result after a timeout is dropped silently because the exchange already concluded. Timeouts and cancellation remove the pending exchange and return the stable `xsr.timed_out` / `xsr.cancelled` errors; the pending count is observable.
- Bounded exchanges: the pending table is capped (1024 by default, per construction). A full table rejects new exchanges with the stable `xsr.backpressure` error instead of queueing unboundedly — queues are bounded and expose backpressure as the correctness contract requires.
- State deltas publish into the session mirror's declared cells with the next revision; deltas for undeclared states are dropped. The renderer reads the mirror locally — state reads perform zero IPC.
- Events deliver in order and are never coalesced, matching the service model's event contract. Event-observer failures never change delivery or the session.
- Failure semantics: a sidecar CRASH frame or a transport failure fails the session terminally with the reason observable, and marks every mirrored state cell Unavailable while retaining the last value — a crash cannot destroy the UI module or terminate the host.
- Reconnect: a new session builds a fresh mirror with the same semantic IDs, publishes a fresh snapshot, and only then activates. The old mirror retains its last values marked unavailable and is never touched by the new session, so there is no window where the renderer observes partially replaced state.
- Receiving SHUTDOWN on the data plane closes the session cleanly; unknown data-plane message types fail the session (protocol discipline, not silent skipping).
- All data-plane payloads use the XSR-401 TLV codec with string-encoded values; typed value contracts arrive with the generated codecs.

## Non-goals

Typed state/value serialization (generated codecs), multiplexing several plugins over one connection, permission validation per exchange, and stream-flow control beyond the pending cap are later units. The sidecar-executable side lives in the separate plugin repository and consumes the same frozen protocol surface.

## Verification

`PCL.Xsr.Runtime.Tests` covers command forwarding with argument and success result, failure codes crossing as stable errors, query value return, timeout with pending release, state deltas with revisions landing in the mirror, five in-order events with zero coalescing, crash failing the session while the mirror retains its value as unavailable, stream failure producing the same semantics, the two-session reconnect contract (fresh mirror coherent, old mirror frozen unavailable), and pending-table backpressure rejection. The complete pipeline passes CoreCLR, NativeAOT, architecture, and format gates.
