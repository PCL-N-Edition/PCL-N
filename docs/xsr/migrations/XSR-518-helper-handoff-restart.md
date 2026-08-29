# XSR-518 helper hand-off and restart scheduling

## Outcome

The Update family's final piece: handing a verified update to a replacement process. The
scheduler validates the staged artifacts, prepares the work directory, and launches the
staged executable with the helper's exact argument contract — the helper waits for the
launcher process to exit, applies the install plan (or swaps the single binary), and
optionally restarts. Process launching is a port, so tests record launches instead of
spawning anything.

## Locked contract

- Hand-off payload: `PreparedLauncherUpdate` — the package plan, the current executable
  path, the staged (GPG-verified) executable path, the work directory, the patch/block-map
  flags, and the optional install plan file that switches the helper into tree-update mode.
- Validation before launch: the staged executable must exist; a declared install plan file
  must exist too. Either violation is a `FileNotFoundException` naming the staged path, and
  nothing launches. The work directory is created on schedule.
- Replacement process contract, byte-for-byte legacy: hidden, no-shell, no-window start of
  the staged executable with the working directory of the current executable; then
  `--pcln-apply-tree-update <pid> <current> <plan> <work> <restart>` when an install plan is
  present, otherwise `--pcln-apply-update <pid> <current> <staged> <work> <restart>`. The
  restart flag is `1`/`0`. These argument orders are the helper interface and never change.
- Launch port: `IProcessLauncher` (production `ProcessLauncher` starts the process and
  releases the handle immediately — the replacement outlives the updater by design).
  `ScheduleInstallOnExit` is the same hand-off with the restart flag cleared.
- Staged-path helper: `UpdateStaging.BuildStagedPath` places the hidden update file next to
  the running executable (`.PCL-N-Edition.exe.<version>.update`) with version characters
  sanitized to underscores.

## Deliberate scope

The helper binary itself (waiting on the PID, applying the plan, replacing the executable)
runs from the same staged-install code but is packaged and shipped by the release pipeline;
its launcher-side integration is composition wiring. Process-start failure surfaces as
`InvalidOperationException` from the real launcher port.

## Verification

`tests/PCL.Services.Tests` (123 executable tests, 3 new) covers: the exact argument
contract for both tree-update and plain modes (order, values, PID formatting, restart flag)
with start-info flags and working directory; the scheduler launching through the port,
creating the work directory, refusing a missing staged executable with the staged path
attached to the exception, refusing a declared-but-missing install plan, and launching
exactly once across all of it; and the staged-path builder plus version-character
sanitization. Runs under CoreCLR and NativeAOT in CI.
