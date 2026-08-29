# XSR-519 Wave 5 acceptance integration

## Outcome

The Wave 5 acceptance unit requested by review: it closes the three structural gaps the
per-capability units left open — fragmented foundation state, missing command/query
composition, and unverified AOT evidence — and locks the review fixes (durable-first
`ResetAll`, provider-id equality, symlink containment) into the same release.

## Locked contract

- One host state store: every foundation service now has a two-phase composition —
  `DeclareState(builder)` registers its entries into the shared host builder
  (`FoundationState.CreateBuilder(settingsSchema)`), the store is built once, and the
  service constructor takes the built store and resolves its own keys. Service-local stores
  are gone: settings, logging, downloads, accounts, and telemetry all publish into one
  store, so a settings `XsrStateId` can never collide with an account one. One state, one
  writer, unchanged.
- Foundation command/query handlers: `FoundationCommands` /
  `FoundationQueries` express foundation operations against the XSR handler delegates
  (`settings.set`, `telemetry.consent`, `accounts.upsert-profile`, `settings.get`). The
  follow-up composition edge in XSR-520 registers them through `FoundationRuntimeComposer` into
  the real `XsrCommandRouterBuilder` / `XsrQueryRouterBuilder`; PCL.Services stays free of the
  runtime dependency while the router path is the only command path.
- Desktop composition: `PCL.Desktop/Program.cs` composes the real foundation over
  `AppFolders.ResolveDefault()` (five services, one store) and then its formal runtime routes,
  so the trim gate analyzes the live call graph. The trimmed binary runs and prints the service
  and route counts.
- CI evidence: a NativeAOT publish-and-execute step for `tests/PCL.Services.Tests` now runs
  in the workflow — the analytics were analyzer-contract only before; the whole foundation
  (auth crypto, archives, update payloads) now executes under NativeAOT per commit.
- Review fixes in the same unit: `SettingsService.ResetAll` is durable-first (defaults
  persist before publish; failed save leaves values, revisions, and availability untouched —
  regression-tested); `AccountProviderId` equality/hash/operators are case-insensitive as
  the contract claimed; `SafeFilePort` walks the real filesystem chain and refuses any
  symlink/junction ancestor or link destination, so a planted link cannot redirect a
  root-relative write outside the tree.

## Verification

`tests/PCL.Services.Tests` grows to 130 executable tests. The acceptance tests build the shared
store and `FoundationRuntime` through the formal composition API rather than registering routes
inside the test. They route a `settings.set` command through the real `XsrCommandRouter` into
`SettingsService`, query the raw value through the paired router, and dispatch the schema I32
setting that the former `string` generic path rejected. They observe the publication at the UI
bridge, drain it, load a PXML text bound to `settings.theme`, and assert the rendered scene text
changes `light` → `dark`; `cross capability page has no state id collisions` loads one page
binding state from four capabilities (settings/account/download/telemetry) plus logging, asserts
every binding resolves in the shared store with distinct runtime ids, seeds all capabilities, and
proves a settings publication dirties exactly the settings-bound entity. A separate acceptance
test proves composed downloads log to the host `LogService`. Runs under CoreCLR and NativeAOT in
CI.
