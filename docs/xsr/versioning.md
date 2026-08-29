# XSR product versioning

## Canonical format

The XSR product line begins at `2.0.0`. User-facing versions, build metadata, update manifests, and artifact identity use exactly one of these dotted forms:

```text
Stable: 2.0.0
Alpha:  2.0.0.alpha.1
Beta:   2.0.0.beta.1
CI:     2.0.0.ci.ffffff
```

Grammar:

```text
stable = MAJOR "." MINOR "." PATCH
alpha  = stable ".alpha." positive-integer
beta   = stable ".beta." positive-integer
ci     = stable ".ci." six-lowercase-hex-digits
```

The six CI digits are the lowercase first six hexadecimal characters of the source commit. A CI build with an unknown commit is invalid for publication.

The initial migration build is `2.0.0.alpha.1`. Promotion is monotonic within a stage. Stable `2.0.0` contains no `stable`, `release`, or numeric fourth component.

## .NET and NuGet projection

The dotted product form is intentionally the canonical display and release identity. Some .NET tooling accepts only SemVer or four numeric assembly components, so `eng/xsr/Xsr.Version.props` exposes separate projections:

| Property | Example | Purpose |
|---|---|---|
| `XsrProductVersion` | `2.0.0.alpha.1` | canonical product/display/update/artifact identity |
| `XsrPackageVersion` | `2.0.0-alpha.1` | NuGet-compatible projection only |
| `InformationalVersion` | `2.0.0.alpha.1` | assembly informational identity |
| `AssemblyVersion` | `2.0.0.0` | CLR binding identity |
| `FileVersion` | `2.0.0.0` | numeric file metadata |

The hyphenated package projection must never leak back into product UI or update identity.

## Independent compatibility versions

The product version does not version every XSR contract. These axes remain independent:

- Plugin SDK version;
- Plugin API version;
- private PCL.Plugin runtime version;
- Sidecar Protocol version;
- Manifest Schema version;
- Package Format version;
- Plugin UI IR version;
- PXML Language version;
- individual capability versions.

For example, XSR product `2.0.0.beta.1` may validate Plugin SDK `1.0.0-rc.1`, private PCL.Plugin runtime `1.0.0`, and Sidecar Protocol v1. A product release never implies a bump to any of those independent versions.

## Upgrade path (one-way)

The update flow is one-way, and `UpdateEligibility` in `PCL.Services` is its single decision
point:

- The legacy `1.4.x` line may upgrade to any `2.0.0` build — alpha, beta, or stable. The
  major-version crossing is intentional and is the migration bridge for every existing
  installation.
- A launcher on any `2.0.0` build is never offered a lower version. Downgrades do not exist:
  not to `1.4.x`, not to an older alpha, not to a CI build ranked below the running channel
  (stage order within one numeric version is stable > beta > alpha > ci).
- The candidate equal to the running version is a no-op. Two CI builds of the same numeric
  version differ only by commit, so moving between them is allowed while returning to the
  same commit is a no-op.

The comparison consumes both grammars: the canonical dotted XSR forms above and the legacy
display/tag shapes (`1.4.11`, `v1.4.11-release`, `1.1.8 beta`). Versions outside both
grammars are refused, never guessed.

## Build inputs

New XSR projects import `eng/xsr/Xsr.Version.props`. Builds may set:

- `XsrVersionChannel=stable|alpha|beta|ci`;
- `XsrVersionSequence=N` for alpha/beta;
- `XsrCommitShort=ffffff` for CI, or provide `GITHUB_SHA` from which the first six characters are derived.

The build fails when the selected channel lacks its required sequence/hash or the resulting product version does not match the canonical grammar.
