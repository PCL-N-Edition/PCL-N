# XSR-405 execute by ID, snapshot lifecycle, and protocol hardening

## Outcome

Wave 4 review hardening: the sidecar principle "register once, execute by ID" is now literal — the data plane carries session-local numeric contract IDs, never semantic strings — and the reconnect contract gains a real coherent snapshot before READY. This unit also closes the review gaps: registration kinds for UI modules and resources, the registration table as a capability boundary, CANCEL reaching the sidecar, the DEACTIVATE lifecycle fix, Unix IPC same-user security, the protocol's explicit draft status, and deterministic performance gates.

## Locked contract

- Execute by ID: registration items are per-kind ordinals starting at 1 in declaration order; both sides derive the identical session-local contract table from the registration stream. COMMAND_REQUEST / QUERY_REQUEST / STATE_DELTA / EVENT / STATE_SNAPSHOT_ITEM all carry the `uint32` contract ID. Semantic IDs live in registration, diagnostics, and tracing only. Every IPC decode is a table lookup — zero string parsing on the hot path.
- Registration kinds: Command, Query, State, Event, UiModule, Resource. UI-module and resource declarations are the substrate for "open plugin page = zero IPC"; the host caches the module, and render-local opening never dials the sidecar.
- Snapshot lifecycle: REGISTER_END is followed by STATE_SNAPSHOT_BEGIN / ITEM* / STATE_SNAPSHOT_END. The host commits the snapshot into the fresh mirror, then sends READY — READY is now a real wire message meaning "snapshot committed". Only then can the session activate. A reconnected session therefore replaces its mirror atomically: fresh coherent values before activation, old mirror frozen unavailable and untouched.
- Capability boundary: the data plane resolves semantics through the registration table before any wire write. An unregistered command or query is rejected locally with the stable `xsr.route_not_found` error, zero wire bytes — proven by a probe asserting the plugin side sees silence. Registration is an enforced boundary, not metadata; Wave 8 permissions attach to this same table.
- Cancellation: CANCEL carries the correlation ID and reason. Host-side timeout or cancellation removes the pending exchange and best-effort delivers CANCEL, so remote downloads, scans, and analysis abort instead of running to completion behind a closed UI.
- DEACTIVATE returns Active → Ready; Activate → Deactivate → Activate works without re-registration.
- Wire safety: a state snapshot referencing an unknown contract ID fails the session; a delta for an undeclared contract is dropped; state declarations carry a codec ID (0 = UTF-8 string in this draft) so the typed-codec wire schema is fixed before Wave 5 services start crossing types.
- IPC security: on Unix the socket lives in a randomized 0700 directory and the socket file is 0600 explicitly — same-user security is enforced, never inherited from umask. Windows keeps CurrentUserOnly pipes.
- Protocol status: 1.0-draft (pre-freeze). Framing and message numbers are append-only; the freeze happens before the Plugin SDK RC, together with the typed codecs and permission metadata this draft deliberately leaves open. Nothing is a plugin-consumable stable ABI yet.

## Performance gates

Deterministic and machine-independent: frame decode allocates nothing beyond the payload arrays (bounded per frame); the pending table is bounded with backpressure; an unregistered command rejects with zero wire bytes (probe verified); the no-op RTT distribution over loopback is measured and reported (p50/p95/p99 informational, not gated).

## Verification

`PCL.Xsr.Runtime.Tests` (77 passing) covers the full snapshot lifecycle with READY on the wire, contract-ID requests and deltas, local capability rejection with a silence probe, snapshot-vs-delta revision ordering, DEACTIVATE → READY reactivation, crash and stream-failure mirror marking, the two-session reconnect atomicity, bounded pending, and the unregistered-command gates. `PCL.Sidecar.Tests` (19 + gates) covers the wire format, per-type round trips, IPC security defaults, allocation-bounded codec, and RTT distribution. Everything passes under CoreCLR and NativeAOT.
