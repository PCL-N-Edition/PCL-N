# XSR-608 — Minecraft Java policy closure

## Outcome

This unit closes the remaining Minecraft Java compatibility blocker from Wave 6. Minecraft
release coordinates are no longer inferred by treating every `Version` whose major is not `1`
as a `1.x` shorthand. The policy keeps legacy `1.x` releases and calendar releases in distinct
schemes, applies Mojang manifest Java metadata first, and uses the historical matrix only when
that metadata is absent.

## Locked contract

- `MinecraftVersionScheme.Legacy` represents the historical `1.x.y` line. Shorthand values such
  as `20.5` are normalized to `1.20.5` at the compatibility boundary.
- `MinecraftVersionScheme.Calendar` represents the year-based line beginning at `26.1`.
  `MinecraftGameVersion.FromVersion(new Version(26, 1))` retains `26.1` and never produces
  `1.26.1`. The typed `MinecraftJavaRequirementRequest.MinecraftVersion` input is available to
  callers that do not need the legacy `Version` compatibility property.
- If `javaVersion.majorVersion` exists in the effective manifest, it is the exact Java-major
  requirement and is not intersected with an inferred Minecraft fallback. Loader constraints
  (Cleanroom, Forge, OptiFine, LiteLoader, and LabyMod) are applied after that base choice and
  still surface disjoint intersections as `ConflictingRequirements`.
- Without manifest Java metadata, the fallback matrix is:

  | Minecraft coordinate | Java requirement |
  |---|---|
  | `<= 1.16.5` | Java 8 |
  | `1.17.x` | Java 16 |
  | `1.18` through `1.20.4` | Java 17 |
  | `1.20.5` through `1.21.x` | Java 21 |
  | `26.1+` calendar releases | Java 25 |

  The resolver exposes each fallback as the existing exact-major range contract, so the Java
  selector cannot choose an older installed runtime merely because the old fallback was `Any`.
  A missing reliable version may still use release-time inference for the Java 21/25 eras.

## Regression corpus

`tests/PCL.Services.Tests` now asserts the matrix at 1.16.5, 1.17.1, 1.18.2, 1.20.1, 1.20.4,
1.20.5, 1.21.1, and calendar 26.1/26.2. The canonical manifest fixtures correct the previous
`1.7` goldens for 1.16.5, Fabric 1.20.1, Quilt 1.20.1, and ARM64 LWJGL to Java 8/17 as
appropriate, and include vanilla 1.17/1.18/1.20.1 plus calendar 26.1. Dedicated tests cover:

- calendar scheme retention and normalization;
- manifest Java 25 authority for calendar 26.1 and authority over a contradictory fallback;
- 1.16.5 never selecting Java 7;
- 1.20.1 never selecting Java 8.

## Verification

The Services executable suite passes all 170 tests after the policy and corpus updates. The
solution build remains warning-free; the full XSR acceptance sequence must rerun the existing
CoreCLR/NativeAOT, architecture, formatting, benchmark, PXML catalog, and Desktop trim gates.
