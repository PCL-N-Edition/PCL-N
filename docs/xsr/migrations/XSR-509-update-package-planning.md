# XSR-509 update package planning

## Outcome

The Update family gains its pure planning core: the model contracts for a package and patch
index, and the planner that turns a build identity plus loaded patch indexes into an
`UpdatePackage` — variant selection, cheapest patch path, patch-versus-full by size, and
asset/URL/blockmap composition. Transport stays out: fetching indexes, HEAD size probes, and
the multi-tag index walk are orchestration concerns that will feed this planner.

## Locked contract

- Models: `UpdateBuildIdentity` (runtime id, runtime variant normalized to SelfContained /
  NoRuntime, configuration normalized Release / Beta / CI, distribution layout, plugin
  sidecar runtime choice preserved through `ResolvePublishedIdentity`), `UpdatePatchStep`
  (one HDiffPatch edge; `hdiffpatch-scatter-v1` marks a scatter bundle), `UpdatePackage`
  (full and patch data plus signatures and block map addresses; `UsesPatch`,
  `SupportsBlockMap`), and the `UpdateChannel` settings-compatible codes (Release=0,
  Beta=1, CI=2).
- Patch index contract: `UpdatePatchIndexDto` / `UpdatePatchStrategyDto` /
  `UpdatePatchVariantDto` / `UpdatePatchDto` with the release pipeline's property names
  (read case-insensitively through the source-generated context). Accepted algorithm
  families are exactly `hdiffpatch` and `hdiffpatch-scatter-v1`; edges missing a version,
  file name, or any hash are ignored.
- Variant selection: runtime id case-insensitive, runtime variant normalized on both sides —
  a legacy-suffixed running build matches the canonical index spelling.
- Patch path: Dijkstra over normalized-version keys across all loaded indexes (multi-hop
  routes are just longer paths), cost = total bytes, planned once for scatter bundles and
  once for legacy patches with the cheaper chain winning and ties going to scatter. A patch
  URL falls back to the release asset URL under the target tag when the index omits one.
- Patch-not-worthwhile: when the known full-package HEAD size exists and the chain is not
  smaller, the plan drops the patch steps; without a HEAD size the variant's
  `targetArchiveSize` decides.
- Full-package fallback: `PlanFromIndex` returns null when no index or no variant matches, so
  the caller composes `PlanFull` — the only plan allowed for single-file layouts and for
  installations before the 1.4.3 block-update baseline.
- Asset composition: `PCL_N_` prefix (planner option), channel to configuration, win-* zips
  versus tar.gz, win single-file Portable executables whose binary signature equals the
  package signature, `.asc` signatures, and block map names by the legacy stem rule (final
  extension only). The v1 block map fallback exists only for Cloudflare distribution and
  tags at or before 1.4.7 (never ci/latest; unknown tag shapes keep it). The 2.0.0 line is
  v2-only by the same rule. `UpdateVersion` (XSR-508) makes XSR tags parse here first-class.

## Deliberate scope

Index fetching and multi-tag walking, HEAD size probes, GitHub channel releases, GPG
signature verification, the vcdiff delta codec, installers, and the service orchestration
around `UpdateEligibility` land as the next update units. Asset naming keeps the legacy
`PCL_N_` prefix as a planner option until the release pipeline defines the 2.0.0 asset
identity.

## Verification

`tests/PCL.Services.Tests` (77 executable tests, 9 new) covers: legacy patch-index JSON
deserialization including strategy and patch fields; identity normalization; full-package
composition across layouts, channels, runtimes, and both distribution origins; direct patch
planning with URL fallback; scatter-vs-legacy byte comparison and the two
patch-not-worthwhile rules; null-for-full-fallback on missing variants; variant matching
through legacy suffix spellings; a two-hop route across chained indexes with byte totals;
and the v1 block map window plus baseline gate with XSR tags. All deterministic, no network.
Runs under CoreCLR and NativeAOT in CI.
