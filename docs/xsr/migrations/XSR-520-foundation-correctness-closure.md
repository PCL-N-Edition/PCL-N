# XSR-520 Wave 5 foundation correctness closure

## Outcome

This review-closure unit makes the Wave 5 Foundation boundary real rather than merely
test-assembled: schema-encoded setting commands work for every declared type, the production
composition root seals the Foundation command/query routers, and download diagnostics always
reach the host's single logging state collection.

## Locked contract

- Raw settings wire form: `SettingsSetCommand.Value` and `SettingsGetQuery` use the
  schema-encoded raw form. `SettingsService.SetRawValue` resolves the schema definition,
  decodes the raw form to its declared CLR type, persists the canonical encoded snapshot before
  publication, then publishes the typed state cell. `GetRawValue` performs the inverse: typed
  state becomes the schema-encoded raw response. The command layer must never infer `string` as
  the setting value type.
- Stable type coverage: Bool, I32, I64, F64, and Text all share the same raw API. A malformed
  raw value returns `settings.invalid_value`; an undeclared key remains
  `settings.unknown_key`.
- Route ownership: `PCL.Services.Foundation` owns `FoundationRouteIds` and the typed handler
  factories, but still references only XSR abstractions/state. The new
  `PCL.Services.Composition` edge project is the only Foundation layer that references
  `PCL.Xsr.Runtime`; `FoundationRuntimeComposer.Compose` binds all Foundation routes into sealed
  `XsrCommandRouter` and `XsrQueryRouter` instances and returns them with the composed host in
  `FoundationRuntime`.
- Production composition: `PCL.Desktop` creates the Foundation host and immediately creates its
  `FoundationRuntime`. Tests use that same public composition API, not ad-hoc router
  registration, so a service call cannot silently bypass the routed product path.
- Unified logging: `FoundationComposer` creates one `LogService` and injects that exact instance
  into `DownloadService`; callers cannot supply a divergent Foundation logger. Download queued,
  attempt, completion, and failure records therefore publish through the host's
  `logging.entries` collection.

## Verification

`tests/PCL.Services.Tests` has 130 executable tests. The raw-settings parameter matrix covers
Bool/I32/I64/F64/Text through set, typed state, encoded query, persistence, and restart. The
Foundation acceptance tests construct `FoundationRuntime`, resolve all formal routes, dispatch
the raw Text and I32 settings commands/query routes, and prove a composed download writes to the
host logging service.
The architecture graph registers `PCL.Services.Composition` as an AOT-compatible edge project;
the existing NativeAOT Foundation-test and trimmed Desktop composition gates exercise it in CI.
