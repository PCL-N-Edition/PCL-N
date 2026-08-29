# XSR-503 launcher settings file compatibility

## Outcome

The settings capability gains the launcher's real data contract: the complete legacy
settings key universe with byte-equal defaults, and a persistence port for the legacy
launcher settings JSON file — including its quarantine-and-recover behavior. An XSR launcher
can now read a legacy settings file, expose every value as typed local state, and write the
same file back without losing anything it does not understand.

## Locked contract

- Key universe: `LauncherDefaults` declares all 103 legacy keys — 44 booleans, 42 integers,
  17 texts — with defaults transcribed byte-equal from the legacy tables (verified by
  scripted diff during migration and locked by tests). `LauncherDefaults.CreateSchema()`
  builds the settings schema; the schema is the single source of key identity and typing.
- File format: the legacy launcher settings JSON — `schemaVersion` (only 1 is supported),
  fixed top-level fields (`automaticallyRepairGameIssues`, `colorMode`, `lightColor`,
  `darkColor`, `downloadSource`), and the `booleanOptions` / `integerOptions` /
  `textOptions` dictionaries. Values keep their legacy encodings: JSON booleans, JSON
  numbers (invariant integers), JSON strings.
- Recovery semantics, migrated: a missing file is an empty view. Invalid per-entry values and
  malformed dictionaries are skipped and count as recovered; valid entries survive. An
  unsupported or unreadable file is a load failure. In both failure shapes the original file
  is quarantined next to itself as `settings.json.invalid` before anything else happens.
  Load failures surface as `IOException` to the service, which reports the stable
  `settings.persist_failed` load error with defaults visible and cells unavailable. The
  legacy immediate rewrite of a repaired file is deliberately dropped: the file heals on the
  next regular save instead.
- Nothing unknown is lost: fixed fields, unknown top-level fields, and option keys outside
  the schema round-trip verbatim on every save — unknown boolean/text values are preserved
  under their original dictionaries. A save never silently rewrites history it does not
  understand.
- Fresh writes look legacy: saving without a prior load emits `schemaVersion: 1` plus the
  legacy fixed-field defaults (`automaticallyRepairGameIssues: true`, `colorMode: System`,
  `lightColor/darkColor: CatBlue`, `downloadSource: PreferOfficialWithMirrorFallback`).
- Durability: saves write a temporary file with write-through and replace the target with
  bounded retries (5 attempts, quadratic backoff), matching the legacy replace loop.
  Per-process path locks serialize concurrent writers on the same file.
- Layering correction (XSR-501 amendment): text-content constraints belong to the port that
  owns the format, not the service. The service treats text values as opaque; the legacy
  line-format port rejects values that cannot fit one line at save time, while the JSON port
  carries full text — required for real JVM arguments containing equals signs and newlines.
  Schema-declared text defaults may now be empty, as the legacy tables require.

## Deliberate scope

The five fixed fields round-trip but are not yet exposed as schema settings — enum-valued
settings need a schema value kind, which lands with the color/theme capability slice that
actually consumes them. Schema key lookup is case-sensitive; the launcher itself writes
canonical keys, and case-insensitive lookup remains a legacy dictionary behavior that is not
carried forward.

## Verification

`tests/PCL.Services.Tests` (34 executable tests, 9 new/updated) covers: schema parity with
the legacy defaults (counts plus exact spot values including the JVM argument string); the
legacy JSON round trip with fixed fields; fresh-save fixed-field defaults; unsupported-schema
quarantine with the service-level load error and unavailable defaults; invalid-item recovery
with quarantine and surviving valid entries; missing-file behavior; preservation of unknown
top-level fields and unknown option keys across service saves; an end-to-end
set/save/restart/read cycle over the JSON port; and the line port rejecting unrepresentable
text at the port boundary. The project runs under CoreCLR and NativeAOT in CI.
