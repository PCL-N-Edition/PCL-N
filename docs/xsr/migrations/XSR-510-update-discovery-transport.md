# XSR-510 update discovery and transport

## Outcome

The update family gains its discovery and transport layer: fetching patch indexes over the
distribution endpoint, walking the documented multi-tag hop chain backwards, HEAD-probing
the full package size, and wiring the whole thing through the XSR-508 one-way eligibility
gate. The HTTP surface is `HttpClient` with an unmodified, caller-owned client — tests
substitute a stub handler, so every transport scenario is fixture-driven and offline.

## Locked contract

- Eligibility first: `ResolveAsync` evaluates `UpdateEligibility` for the candidate before
  any network request. A `Downgrade`, `SameVersion`, or `Unrecognized` verdict yields no
  package and zero requests — the one-way policy is enforced at the transport boundary, not
  downstream of it.
- Shortcuts before fetches, in the legacy order: a single-file (portable) layout takes only
  the full package (a scatter patch index must never touch it); a running version before the
  1.4.3 block-update baseline takes the full package with every block map address stripped.
  Both skip the index fetch entirely.
- Index fetch: `patch-index.json` is preferred, `index.json` is the legacy alias; 404 moves
  on, other non-success codes and malformed JSON behave as missing. An index is accepted
  only with format version 1–3 and at least one variant. When the distribution origin is
  GitHub (not Cloudflare-only), the same asset names are tried under the GitHub release
  download base as a fallback.
- Multi-tag walk, migrated: after the target index, each index's oldest
  `selectedFromTags` entry is loaded (bounded at 12 hops, never revisiting a tag) and the
  patch path is rebuilt; the walk stops when the newest previous index's target is not newer
  than the running version and still no path exists. The stop comparison uses the
  XSR-508 `UpdateVersion` scale, so legacy and XSR tags order on one line.
- HEAD probe: when a patch path exists, the full package URL is HEAD-probed; the size feeds
  the planner's patch-not-worthwhile rule. Failures (404, network errors) yield null and the
  planner compares against the index archive size instead.
- Result: `UpdateDiscoveryResult(Decision, Package)` — an allowed discovery returns the
  planner's package (patched or full); any other decision returns null.

## Deliberate scope

Channel/release feed discovery, release-notes rendering, the Cloudflare client-certificate
HTTP factory, GPG signature verification, the vcdiff delta codec, and the installers are
their own upcoming units; this one deliberately carries only fetch/walk/probe plus the
eligibility wiring.

## Verification

`tests/PCL.Services.Tests` (84 executable tests, 7 new) covers: fetch preference
(patch-index before index.json, alias accepted alone), refusal of malformed JSON,
out-of-range format versions, and empty variant lists; the GitHub fallback URL tried only
when the origin is not Cloudflare-only; the eligibility gate refusing downgrades, no-ops,
and unrecognized versions with zero requests; baseline and single-file shortcuts skipping
the fetch (block maps stripped versus preserved); a two-hop route across a walked previous
index with the HEAD probe observed and a chain kept when cheaper than the HEAD size; the
walk stopping after a previous index whose target is not newer (and never HEAD-probing);
and HEAD failure falling back to the archive-size comparison. All deterministic through a
stub handler. Runs under CoreCLR and NativeAOT in CI.
