# XSR-501 settings capability

## Outcome

Wave 5 opens with the first foundation service: the settings capability in `PCL.Services`.
It establishes the Wave 5 service pattern — a frozen schema, typed local state cells, a
persistence port owned by the service, and stable semantic error codes — against the
data-compatibility requirement of the legacy `key = value` settings files.

## Locked contract

- Schema-first: every setting is declared through `SettingsSchemaBuilder` as a key
  (`XsrSemanticId`), a value type (`Bool`, `I32`, `I64`, `F64`, `Text`), and a default. The
  schema is the data contract; the service never invents keys, and undeclared persisted keys
  are skipped, never imported.
- Typed state, zero plumbing for the renderer: construction builds one `XsrStateStore` cell
  per setting (owner `PCL.Services.Settings`), so renderers and observers read settings as
  local typed state with revisions and availability — the same contract as every other state
  fact.
- Durable-first writes: `SetValue`/`ResetValue`/`ResetAll` encode the value, save the whole
  entry set through the port, and only then publish to the state store. Success means
  persisted; a save failure returns the stable `settings.persist_failed` error and changes
  nothing observable (revision stays untouched).
- Stable errors: `settings.unknown_key` (NotFound), `settings.type_mismatch`
  (ContractMismatch), `settings.invalid_value` (Rejected), `settings.persist_failed`
  (Unavailable). Codes are semantic IDs and never change meaning.
- Port boundary: `ISettingsPort` moves raw string entries only — load and save. The service
  owns when and what is written; the port owns how. `InMemorySettingsPort` and
  `SettingsFilePort` are the first two implementations; file writes go through a temporary
  file and an atomic move, entries are written in ordinal key order.
- Data compatibility: the file format is the legacy `key = value` line format with a
  `# pcl-settings v1` header. Parsing skips comments, blank and malformed lines; values use
  invariant culture with round-trip (`R`) doubles and `true`/`false` literals. Text settings
  cannot carry line breaks, control characters, or the equals sign so one line stays one
  entry — enforced at both set and schema-declaration time.
- Degraded startup: a failed load keeps schema defaults visible but marks every cell
  `Unavailable` and records the stable error in `LoadError`; the next successful write
  restores availability. Malformed or undeclared persisted entries are counted in
  `SkippedEntryCount`, never fatal.

## Deliberate scope

No change notifications beyond the state store's own observer contract, no write batching or
debounce (settings writes are rare and durable-first by contract), and no schema versioning
field yet — forward compatibility is achieved by skipping unknown keys, which is sufficient
while Wave 5 families are still being declared.

## Verification

`tests/PCL.Services.Tests` (14 executable tests) covers schema defaults with availability,
typed round trips, stable rejection of unknown keys/type mismatches/invalid values, durable
persistence across service restart, corrupt and unknown persisted entries skipped, failed
save mutating nothing, failed load degrading to unavailable defaults, reset value/all,
observer and snapshot visibility, and the file port's round trip, malformed-line skipping,
and ordinal ordering. The project is AOT-compatible, wired into the architecture gate, and
runs in CI.
