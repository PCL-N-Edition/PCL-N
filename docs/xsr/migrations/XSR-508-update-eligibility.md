# XSR-508 update eligibility

## Outcome

The launcher update flow gains its one-way policy gate: the legacy `1.4.x` line can cross
into the `2.0.0` line — alpha builds included — and no launcher is ever offered a downgrade.
`UpdateEligibility` in `PCL.Services` is the single decision point; discovery and future
orchestration never compare versions themselves.

## Locked contract

- Decisions: `Allowed`, `SameVersion`, `Downgrade`, `Unrecognized`. Only `Allowed` may
  proceed. A version neither grammar recognizes is refused, never guessed — including an
  unrecognized running version.
- One scale for two grammars: `UpdateVersion` normalizes the canonical XSR dotted forms
  (`2.0.0`, `2.0.0.alpha.1`, `2.0.0.beta.1`, `2.0.0.ci.ffffff`) and the legacy display/tag
  shapes (`1.4.11`, `1.4`, `v1.4.11-release`, `1.1.8 beta`, `+build` metadata) into one
  comparable value: numeric triple, then stage (stable > beta > alpha > ci), then
  alpha/beta sequence or CI commit. The numeric core and prerelease may be separated by a
  dash (legacy) or dots (canonical); a four-segment numeric legacy version drops its
  revision. The `release`/`stable`/`final`/`ga` suffixes are stable; `rc` ranks as beta; an
  unrecognized prerelease ranks as beta so it can never beat a stable build.
- The headline rule: `1.4.11 → 2.0.0.alpha.1` is Allowed, and `2.0.0.alpha.1 → 1.4.11` is a
  Downgrade. The major-version crossing is the migration bridge for every existing
  installation.
- Monotonic within `2.0.0`: alpha.N grows, alpha → beta → stable is allowed, the reverse of
  any of these is a Downgrade, and a CI build of the same numeric version ranks below every
  prerelease channel of that version.
- CI hops: two CI builds of the same numeric version differ only by commit, so moving
  between different commits is Allowed and returning to the same commit is a SameVersion
  no-op. The decision record carries both versions and a stable reason string.

The policy is locked in [../../xsr/versioning.md](../../xsr/versioning.md) under
"Upgrade path (one-way)"; changing it requires that document to change first.

## Deliberate scope

The update flow still has no discovery, package, or orchestration logic on this branch —
this unit is only the policy gate and its parser, so the one-way rule is locked before any
code that could violate it exists.

## Verification

`tests/PCL.Services.Tests` (68 executable tests, 6 new) covers: the headline cross-major
rule in both directions across tag/display variants; monotonic alpha/beta/stable ordering
including cross-version cases; same-version no-ops across grammars; CI hop semantics
including the same-commit no-op and the below-prerelease ranking; refusal of null, garbage,
and non-hex CI versions; and parser normalization details (v-prefix, build metadata,
underscore/space display forms, four-segment numeric, round-trip `ToString`). Runs under
CoreCLR and NativeAOT in CI.
