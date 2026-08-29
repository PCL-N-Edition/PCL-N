# XSR-606 Minecraft launch acceptance hardening

## Outcome

The Wave 6 acceptance unit: it closes the correctness and safety gaps found in review —
broken Java range intersection, incomplete launch token expansion, ARM64 natives on the
classpath, missing client JAR, no native extraction, process sessions outside the host
state, permissive Mojang rules, and manifest-controlled path traversal — and locks the
whole launch pipeline behind the canonical corpus snapshots.

## Locked contract

- Java requirements: `JavaVersionRange.TryIntersect` implements mathematical intersection
  (minimum = max, maximum = min) and returns false for disjoint ranges; the resolver
  surfaces that as `ConflictingRequirements` instead of widening. Vanilla versions are
  normalized to their true 1.x tuple ("1.8" → 1.8.0, not 8.0) before era gates, and the
  1.20.5 gate compares against the normalized form. Gates: ≥1.20.5 → Java 21, <1.13 →
  Java 8, legacy Forge <1.12 → Java 7, OptiFine 1.8–1.12 → Java 8.
- Launch tokens: the full Mojang vocabulary is covered — auth/version/game/assets tokens
  plus `natives_directory`, `classpath_separator`, `library_directory`, `launcher_version`,
  and `user_properties`. Any remaining `${...}` after replacement fails plan creation with
  the offending argument, so a future Mojang token cannot silently produce broken JVM args.
- Client JAR: the planner derives `<instance>/<versionId>.jar` (or honors an explicit
  `ClientJarPath`), requires it to exist, and places it at the classpath head. The version
  id is containment-validated like any manifest-controlled name.
- ARM64 LWJGL: the Linux-ARM64 LWJGL3 replacement tokens are classified as natives
  (`IsNatives = true`), so the classpath planner keeps them out of `-cp`.
- Natives extraction: `MinecraftNativesExtractor.ExtractAsync` unpacks every native JAR
  into the natives directory, skipping META-INF, refusing traversal entries, and
  overwriting unconditionally so a stale native set cannot survive. The launch plan exposes
  `NativesDirectory` for the `${natives_directory}` token.
- Mojang rules: rules evaluate in order and the LAST matching rule decides; no matching
  rule means the value is excluded. `os.version` is a regular expression. Absent features
  participate as false. Verified with ordered allow/disallow chains, `^10\.`/`^11\.` regex
  cases, and `has_custom_resolution: false`.
- Process state: `minecraft.process.sessions` is an ordered collection in the shared host
  state store (owner `PCL.Services.Minecraft.Process`), declared by
  `MinecraftProcessStateComposition` in the same `FoundationState` builder as every other
  capability. Launch publishes Created → Running; cancellation publishes Cancelled;
  retention keeps at most 32 finished sessions and prunes stale ones. The service accepts
  the host store; `MinecraftRuntimeComposer` wires it, and the Desktop root passes
  `runtime.Host.StateStore` in.

## Deliberate scope

The helper binary that actually waits on the PID and applies the plan is shipped by the
release pipeline; the canonical corpus fixtures here are representative de-typified
manifests covering the shape matrix, with real vendor manifests to be added as corpus
growth (1.12.2 Forge, NeoForge, OptiFine-tuned chains).

## Verification

`tests/PCL.Services.Tests` grows to 159 executable tests: disjoint Java ranges rejected
with `ConflictingRequirements` (Cleanroom 21 vs vanilla 8 + legacy Forge), overlapping
ranges narrowing, full token resolution including `-Djava.library.path=${natives_directory}`
and `${classpath_separator}`, unresolved-token plan failure naming the token, client JAR at
the classpath head for vanilla and inherited launches, ARM64 natives classified and kept
off the classpath, natives extraction with META-INF exclusion and traversal refusal,
ordered rule semantics with regex `os.version` and absent-false features, the canonical
corpus snapshots (Java requirement, main class, classpath head, no unresolved tokens across
five manifest shapes), and process session publication/cancellation/retention into the
shared host state. Runs under CoreCLR and NativeAOT in CI.
