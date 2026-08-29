# XSR-406 transactional snapshot, typed codecs, and content registration

## Outcome

Wave 4 review hardening: the state snapshot is a real transaction — collected and validated in full before any mirror mutation, then committed atomically — the mirror cells are typed by a frozen codec registry instead of string-only, and UiModule/Resource declarations now carry their actual content with verified hashes into a host cache, completing the "open plugin page = zero IPC" goal.

## Locked contract

- Transactional snapshot: STATE_SNAPSHOT_BEGIN/ITEM*/END is collected into a temporary set, never touching the mirror during collection. Per item: an unknown contract ID, a duplicate, or a codec-malformed value fails the session terminally. At END: the received set must exactly cover every declared state — missing states fail too. Only after full validation does the host commit all values (one revision each) and emit READY. A failed snapshot leaves the mirror completely unmuted: cells stay unavailable at revision 0.
- Typed codec registry: IDs frozen for the protocol draft — 0 = UTF-8 string, 1 = Bool, 2 = Int32, 3 = Int64, 4 = Float64, 5 = Bytes, 6 = generated DTO blob. Codecs are pure, fixed-length-validated where applicable, and reflection-free. Mirror cells are created typed by the declared codec, and state deltas travel as raw codec-encoded bytes decoded through the same registry. Codec 6 is the generator target: the host treats the blob as schema-contracted opaque bytes, so Wave 8's generated field codecs extend without re-plumbing the data plane. Unknown codec IDs are rejected at registration.
- Content registration: UiModule and Resource declarations carry their content inline (payload + SHA-256 hash). The host verifies the hash, caches UI modules by semantic ID and resources content-addressed by hash (identical content stored once), and rejects: a module whose hash does not match its payload, and a UI module referencing resources missing from the same registration. Opening a registered page or reading a resource reads the cache — zero wire bytes, probe-verified.

## Non-goals

BLAKE3 (SHA-256 is used until a hash dependency is justified), field-level DTO codegen (Wave 8 generator emits against the frozen codec interface), and real-IPC RTT benchmarks (current gates measure protocol + connection stack over loopback).

## Verification

`PCL.Xsr.Runtime.Tests` adds twelve regressions: duplicate/missing/unknown snapshot states and codec-malformed values rejected without mirror mutation, the full-snapshot atomic commit with READY ordering, Bool/Int32 typed delta round trips, generated-DTO blob preservation, UI module cached at registration, zero-IPC page open (probe), resource hash deduplication, and missing-resource rejection. `PCL.Sidecar.Tests` covers the wire format for all of it. Everything passes under CoreCLR and NativeAOT with formatting clean.
