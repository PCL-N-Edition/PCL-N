# XSR-502 logging capability

## Outcome

The second foundation service: the logging capability in `PCL.Services`, migrated from the
legacy portable logging bridge (`PortableLog` + `PortableLauncherLogSource`). The legacy
behavior contract — level gating, bounded FIFO ring, credential redaction, and "a diagnostic
sink must never break the operation being diagnosed" — is preserved, while the delivery
mechanism becomes XSR state: the ring is an ordered state collection that any surface reads
locally.

## Locked contract

- Levels: `Error`/`Warn`/`Info`/`Debug`/`RealTime`, ordered most-to-least severe so the
  maximum-level gate is one integer comparison. The default gate is Info; `RealTime` remains
  the high-volume trace level for compatibility with old trace call sites. An invalid enum
  value in `IsEnabled` or a `MaximumLevel` setter falls back to Info, mirroring legacy.
- Entry normalization: a blank module becomes `General`, modules are trimmed, timestamps come
  from an injectable `TimeProvider` (system clock by default), and messages plus exception
  text are redacted before storage — raw secrets are never in state or snapshots.
- Redaction: the five legacy pattern classes are the security behavior contract —
  authorization headers, bearer tokens, secret assignments (`password=`, `token:` …), secret
  space-separated arguments, and sensitive query parameters (`code=`, `access_token=`, `sig=`
  …). Patterns are culture-invariant and non-backtracking; replacements preserve the matched
  prefix and `<redacted>` the value.
- Bounded ring: capacity default 2,000 (minimum 1); appends evict the oldest entries so the
  state collection itself stays bounded. `Sequence` is monotonic per service and keys the
  ordered collection, so ordering is total and survives concurrent writers.
- State, not a shared mutable list: entries live in one ordered collection state
  (`logging.entries`, owner `PCL.Services.Logging`) with availability and revisions like any
  other state fact. `GetSnapshot` and the store's `ReadCollection` return the same coherent
  items. `Clear` empties the collection through a delta, not a side channel.
- No static global: the legacy `PortableLog.Written` static event is replaced by receiving
  `LogService` through the composition root. Observer failures cannot break publication (the
  store guarantees this; proven by a hostile-observer test).
- Display parity: `ToDisplayText` keeps the legacy `[HH:mm:ss.fff] [Level] [Module] message`
  format with local time, appending the exception text on a second line.

## Deliberate scope

No persistence yet — legacy logs were session-visible and file dumps were launcher-level
concerns; durable log export returns as a port when the File capability (XSR-503+) lands.
Log sources stay programmatic for now; the Sidecar event plane forwards plugin logs through
the same service later in the wave.

## Verification

`tests/PCL.Services.Tests` (25 executable tests, 11 new) covers the level gate and stable
fallback, module normalization, message and exception redaction before storage, ring
eviction, state-collection parity with snapshots, clear semantics, the full legacy redaction
pattern set, observer visibility plus hostile-observer isolation, injected clock, exact
display format, and concurrent writers preserving unique ascending sequences. The project
runs under CoreCLR and NativeAOT in CI.
