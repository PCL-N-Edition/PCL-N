# XSR-602 — Minecraft Java, assets, and libraries

## Scope

This unit adds the portable artifact-resolution contracts used by Minecraft launch. Java
selection is driven by manifest/version requirements and an injected runtime locator; no service
starts a process or assumes a platform implementation. Asset indexes resolve inherited and
legacy forms, and object paths are content-addressed under the Minecraft root. Library
coordinates resolve to contained paths with OS/architecture rules and native classifiers;
classpath ordering is a separate deterministic policy.

## Safety and compatibility

- Java candidates are filtered by enabled/available state, version range, and stable tie-breakers
  (lowest suitable major, JDK before JRE, brand, version, path).
- Legacy Java 7/8 boundaries and modern manifest Java component names remain explicit in the
  acquisition decision; malformed Cleanroom metadata is a structured failure.
- Asset source paths, library coordinates, and manifest download paths reject rooted paths,
  separators that escape their content root, and traversal segments.
- Mojang `rules` are evaluated against the requested operating system and architecture. Native
  libraries remain out of the classpath while OptiFine and custom classpath-head ordering stay
  deterministic.

## Verification

The services executable tests cover modern and legacy Java requirements, candidate filtering,
asset object/resource/virtual layouts, hash URLs, OS rules, native classifiers, manifest paths,
coordinate safety, and classpath filtering. Everything stays inside `PCL.Services`; the Runtime
router and UI are not dependencies of this unit.
