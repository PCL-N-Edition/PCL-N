# XSR-603 — Minecraft ModLoader, launch, process, and crash contracts

## Scope

This unit closes the executable core boundary after discovery and artifact planning. It
identifies Vanilla, OptiFine, Forge, NeoForge, Fabric, Quilt, LiteLoader, Cleanroom, and
LabyMod from manifest signals; creates a deterministic launch plan from a manifest and its
resolved classpath; exposes process start/lifecycle through `IMinecraftProcessPort`; and turns
launch output into structured fault and missing-dependency reports.

## Locked invariants

- Launch arguments are carried as `ProcessStartInfo.ArgumentList` entries. User-controlled paths,
  names, tokens, and server values are never shell-concatenated.
- JVM/game substitutions are resolved from the request and manifest, with an explicit
  `-Xmx` floor and Java 18+ encoding compatibility switch.
- Native libraries are excluded from the classpath, loader-specific main classes remain in the
  manifest contract, and the process working directory is the selected instance directory.
- Process state transitions are `Created -> Running -> Exited|Failed|Cancelled`; a caller can
  observe a snapshot and cancel without reaching around the process port.
- Crash analysis emits stable fault codes/subsystems and a finite repair-action allow-list. It
  truncates evidence and keeps missing-mod dependencies structured for later repair planners.

## Verification

Executable service tests cover loader detection, manifest substitutions, classpath/process-start
argument construction, Java/JVM and graphics fault signatures, and English/Chinese/NeoForge
missing-dependency lines. The implementation has no UI or legacy-worktree dependency.
