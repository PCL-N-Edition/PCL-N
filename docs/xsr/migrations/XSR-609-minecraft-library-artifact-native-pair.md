# XSR-609 — Preserve Minecraft library artifact/native pairs

## Outcome

Mojang library entries may carry the ordinary `downloads.artifact` and a platform native
classifier in the same JSON object. The resolver now treats those artifacts as independent
outputs: the ordinary JAR remains a classpath token and the classifier remains a native
extraction token. A native declaration can no longer discard the dependency that contains the
Java classes.

## Locked contract

- `MinecraftLibraryResolver` resolves `downloads.artifact` independently of `natives` and
  emits it before the selected platform classifier. Ordinary artifacts have `IsNatives = false`
  unless their coordinate explicitly declares a native classifier.
- The selected `natives.<os>` classifier is resolved independently, including manifest path,
  checksum, size, URL, ARM64 compatibility replacement, and unsupported-native filtering.
  Unsupported or unavailable native data suppresses only the native token; it never suppresses
  the ordinary artifact from the same entry.
- A classifier URL built from a library repository base uses the classifier coordinate; when no
  repository base is present, the explicit classifier download URL is used.
- When `UseSystemGlfw` is enabled, the ordinary GLFW artifact is retained but its native
  classifier is omitted before ARM64 replacement; native omission never reclassifies the
  classifier as a classpath artifact. Every token returned by the native-classifier resolver has
  `IsNatives = true`.
- `MinecraftClasspathPlanner` continues to exclude only tokens marked `IsNatives`; therefore a
  standard LWJGL entry contributes its ordinary JAR to `-cp` while its native archive is staged
  separately.

## Regression corpus

The Services tests include a standard LWJGL-shaped entry containing both
`downloads.artifact` and `downloads.classifiers.natives-linux`. The test asserts two tokens,
their paths and checksums, native classification, artifact-first ordering, and a classpath that
contains only the ordinary JAR. The ARM64 acceptance fixture also asserts that the compatibility
artifact survives beside the ARM64 native token and reaches the classpath planner.
The `SystemGlfwKeepsOrdinaryArtifact` and `SystemGlfwDropsNativeClassifier` regressions lock
the system-GLFW contract, with the latter exercising Linux ARM64 before native replacement.

## Verification

`tests/PCL.Services.Tests` passes the complete executable suite (173 tests after this unit) under
both CoreCLR and Foundation NativeAOT, including the standard artifact/native pair and ARM64
classpath regressions plus system-GLFW filtering. The solution Release build is warning-free. The
architecture gate, UI.Next benchmark gate, exact source-format gate, and trimmed Desktop publish
also pass for commit `220a5e74`.
