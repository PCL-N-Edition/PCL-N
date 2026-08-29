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
  composition root registers them into the real `XsrCommandRouterBuilder` — PCL.Services
  stays free of the runtime dependency while the router path is the only command path.
- Desktop composition: `PCL.Desktop/Program.cs` now composes the real foundation over
  `AppFolders.ResolveDefault()` (five services, one store) instead of an empty shell, so the
  trim gate analyzes the live call graph. The trimmed binary runs and prints the composed
  service count.
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

`tests/PCL.Services.Tests` grows to 128 executable tests. The two acceptance tests are the
headline: `foundation composition end to end` builds the shared store through the
composition, routes a `settings.set` command through the real `XsrCommandRouter` into
`SettingsService`, observes the publication at the UI bridge, drains it, loads a PXML text
bound to `settings.theme`, and asserts the rendered scene text changes `light` → `dark`;
`cross capability page has no state id collisions` loads one page binding state from four
capabilities (settings/account/download/telemetry) plus logging, asserts every binding
resolves in the shared store with distinct runtime ids, seeds all capabilities, and proves a
settings publication dirties exactly the settings-bound entity. Runs under CoreCLR and
NativeAOT in CI.
