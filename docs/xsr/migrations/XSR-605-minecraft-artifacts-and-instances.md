# XSR-605 — Minecraft artifact acquisition and instance discovery

Wave6 now has a complete service-side boundary for installed Minecraft state. The
implementation remains UI-free and keeps all filesystem/network effects behind explicit
request, planner, and process seams.

## Scope

- `MinecraftInstanceDiscovery` enumerates installed version directories and loads schema-1
  instance metadata through the atomic metadata store. The `minecraft.instances.read` query is
  registered by `MinecraftRuntimeComposer` alongside version discovery and crash analysis.
- Java runtime acquisition resolves Mojang's platform catalog, validates manifest paths, ignores
  known directory/noise hashes, downloads to temporary files, verifies SHA-1 and size, preserves
  executable bits, and reuses already verified files. HTTP metadata and the installer are
  replaceable seams for offline tests.
- Asset/client/index download plans preserve the legacy paths and fallback behavior. Source
  ordering supports official, BMCLAPI, third-party Maven, and unlisted-version mirrors.
- Linux ARM64 library planning carries forward the canonical LWJGL 2/3 compatibility mappings,
  verified metadata, unsupported-native exclusions, and safe manifest/coordinate containment.
- Launch planning merges inherited manifests, evaluates OS/architecture/feature rules, builds
  structured JVM/game arguments, handles authlib and quick-play joins, and emits
  `ProcessStartInfo.ArgumentList` entries without shell concatenation.

## Evidence

`MinecraftCoreTests` covers legacy preference parsing, inherited launch arguments, ARM64
compatibility artifacts, source fallback, runtime installation with a fake HTTP transport, and
the composed instance route. The executable suite passes under CoreCLR and NativeAOT; the full
solution also passes architecture, trim, and formatting gates.

