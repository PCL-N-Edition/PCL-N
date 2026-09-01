# XSR-606 Minecraft launch acceptance hardening

## Outcome

The Wave 6 acceptance unit closes the correctness and safety gaps found in review —
Minecraft/Java version-coordinate confusion, incomplete launch token expansion, ARM64 natives
on the classpath, missing client JARs, an unexecuted native-extraction stage, process sessions
outside the host state, permissive Mojang rules, and manifest-controlled path traversal. The
launch route now runs the same prepare → extract → execute pipeline covered by the acceptance
tests.

## Locked contract

- Java requirements: `JavaVersionRange.TryIntersect` implements mathematical intersection
  (minimum = max, maximum = min) and returns false for disjoint ranges; the resolver surfaces
  that as `ConflictingRequirements` instead of widening. `MinecraftGameVersion` is a separate
  domain coordinate, so shorthand and normal Minecraft versions are normalized to their true
  1.x tuple before era gates. Gates: ≥1.20.5 → Java 21, <1.13 → Java 8, legacy Forge <1.12
  → Java 7, OptiFine 1.8–1.12 → Java 8. Manifest exact-major requirements are intersected
  with those gates and cannot silently widen them.
- Launch tokens: the full Mojang vocabulary is covered — auth/version/game/assets tokens
  plus `natives_directory`, `classpath_separator`, `library_directory`, `launcher_version`,
  `clientid`, `auth_xuid`, and `user_properties`. Any remaining `${...}` after replacement
  fails plan creation with the offending argument, so a future Mojang token cannot silently
  produce broken JVM args.
- Client JAR: `MinecraftClientJarResolver` follows `inheritsFrom` and `jar` aliases through
  the installed `versions/<id>/<id>.jar` layout (or an instance-local jar), honors an explicit
  `ClientJarPath` override, requires the selected file to exist, and puts it in the generated
  classpath. The version id and every inherited id are containment-validated like any
  manifest-controlled name.
- ARM64 LWJGL: the Linux-ARM64 LWJGL3 replacement tokens are classified as natives
  (`IsNatives = true`), so the classpath planner keeps them out of `-cp`.
- Natives extraction: `MinecraftLaunchExecutor` validates every native archive, calls
  `MinecraftNativesExtractor.ExtractAsync` before `Process.Start`, and passes the immutable
  plan to `MinecraftProcessService` only after staging succeeds. Extraction skips META-INF,
  refuses traversal entries, and exposes `NativesDirectory` for `${natives_directory}`.
- Mojang rules: rules evaluate in order and the LAST matching rule decides; no matching
  rule means the value is excluded. `os.version` is a regular expression. Absent features
  participate as false. Verified with ordered allow/disallow chains, `^10\.`/`^11\.` regex
  cases, and `has_custom_resolution: false`, through the same evaluator for launch arguments
  and libraries.
- Process state: `minecraft.process.sessions` is an ordered collection in the shared host
  state store (owner `PCL.Services.Minecraft.Process`), declared by
  `MinecraftProcessStateComposition` in the same `FoundationState` builder as every other
  capability. Launch publishes Created → Running and then Exited/Failed; cancellation through
  the `minecraft.process.cancel` command publishes Cancelled. The lifecycle checks `HasExited`
  on both sides of the Running transition, retains at most 32 finished sessions, prunes stale
  CLR/state entries, and bounds disposal waits. `MinecraftRuntimeComposer` wires the service,
  and the Desktop root passes `runtime.Host.StateStore` in.
- Download paths: client `VersionName` and asset-index `id` are validated with the same safe
  reference contract before any `Path.Combine`; final paths are checked for containment below
  the instance or Minecraft root.

## Corpus

`CanonicalCorpusProducesConsistentLaunchSnapshots` uses a de-identified, deterministic fixture
matrix covering legacy `minecraftArguments`, modern JVM/game arguments, 1.7/1.12/1.16/1.20.1/
1.20.5/1.20.6/1.21 boundaries, Forge, Fabric, Quilt, NeoForge, OptiFine, Cleanroom, inherited
loader chains, and Linux ARM64 LWJGL. Each fixture asserts the expected Java lower bound (the
field is no longer discarded), manifest component when present, main class, library/native
split, client-JAR selection, classpath, memory argument, and absence of unresolved tokens.

## Verification

`tests/PCL.Services.Tests` contains 165 executable tests: disjoint Java ranges rejected with
`ConflictingRequirements` (including Cleanroom 25 vs legacy Java 8), overlapping ranges
narrowing, full token resolution including `${natives_directory}`, `${launcher_version}`,
`${classpath_separator}`, and `${library_directory}`, unresolved JVM/game-token plan failure
naming the token, automatic client-JAR selection through inherited versions, ARM64 natives
classified and kept off the classpath, the real launch route staging natives before process
start, META-INF exclusion and traversal refusal, ordered rule semantics with regex
`os.version` and absent-false features, the expanded corpus assertions, process lifecycle
publication/cancellation/retention into shared host state, and the synchronous progress
observer used by the installer AOT test. CoreCLR and NativeAOT both pass.

The full acceptance sequence also passes the Runtime/UI.Next/PXML/Sidecar suites and their
NativeAOT publishes, the UI.Next benchmark gate, the 27-project architecture gate, formatting,
and the trimmed Desktop publish.
