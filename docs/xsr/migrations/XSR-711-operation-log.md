# XSR-711 Operation log: observers, manual log points, level policy

## Scope locked before implementation

A macOS-unified-log-style operation log: every subsystem narrates what it does, at a level
users and bug reports can actually read, with verbose tiers available while chasing live bugs.

- One observer layer (`XsrOperationLog` in the composition project) feeds XSR dispatch,
  state, event, scheduler and lifecycle telemetry into `LogService`; services never grow
  observer plumbing. `XsrCompositeStateObserver` fans one store publication out to the
  renderer bridge and the log tap.
- Manual log points in service code: one-shot internals and user-visible operations log
  directly through `LogService`'s ergonomic helpers (`Info/Warn/Error/Debug/Trace`).
  Loop bodies (download segments, retries, instance scans) log at the RealTime tier.
- Level tiers are a product contract: Info = user-visible operations (UI intents, lifecycle
  transitions, profile/setting changes, launch milestones), Debug = one-shot internals
  (dispatch completions, composition facts), RealTime = loop bodies and high-frequency
  state/scheduler flows. Warn/Error = failures, always visible.
- Level policy by channel: alpha/beta/ci builds and console-attached launches run at
  RealTime; only release builds default to the Info gate.
- Sinks are mirrors outside the state ring: console (when a console is attached) and one
  session file, both self-disabling on IO failure so logging never breaks the app.
- The logging/diagnostics/telemetry state domains are quiet: the log's own publications
  never re-enter the log (recursion guard in the state observer).
- A WinExe has a console handle only when launched from a terminal; detached GUI launches
  keep the file sink only.

## Acceptance

- Five observers write at their tiers: dispatch success → Debug with semantic id, duration
  and correlation id; dispatch failure → Warn with error code and fault; state change →
  RealTime with revision (quiet domains skipped); events → Debug; scheduler → RealTime;
  lifecycle transitions → Info.
- `LauncherSession` lifecycle: the composition root narrates NotStarted → Starting →
  Running → Stopping → Stopped at Info, for both the GUI run and `--validate-shell`.
- Manual log points: launcher startup + composition facts, account load/add/remove/import/
  select, settings load failures and writes, launch coordinator milestones, download
  submit/failover/cancel/complete, segment start/complete, temp-file rename retries, and
  per-instance discovery scan lines.
- Sinks: file sink lazily opens one append-mode UTF-8 stream, flushes per entry and
  self-disables after IO errors or disposal; console sink disables when no console exists.
- Channel policy parsed from the informational version (`2.0.0.alpha.N`/`beta`/`ci`),
  mirroring `docs/xsr/versioning.md`.

## Evidence

- `tests/PCL.Desktop.Tests/OperationLogTests.cs`: dispatch Debug/Warn tiers, state RealTime
  + quiet-domain silence, composite fan-out, lifecycle Info (visible under the default gate)
  and scheduler RealTime (only above the gate).
- `tests/PCL.Services.Tests/LogSinkTests.cs`: file sink append/dispose semantics, console
  sink self-disable, and the level-gate policy across tiers (`VerboseEnabled`, Info records
  by default, Trace only when raised).
- Live console session: lifecycle narration, composition facts (Debug), instance-scan lines
  (RealTime), UI navigation/account intents (Info) all present; `--validate-shell` exits 0
  with the full session lifecycle.
- Full local gates: Services 185, Desktop 29, Runtime 89, UI.Next 69, PXML 37, Avalonia 14,
  Sidecar 19, architecture 29 projects, format clean.

## Notes

- `MinecraftInstanceDiscovery`'s primary constructor gained a leading optional `LogService`
  parameter; existing callers pass `versionDiscovery:` by name. Host-bearing composition
  passes `host.Logging`; the host-less overload stays unlogged.
- Lifecycle transitions moved from Debug to Info between review rounds: they are low-volume
  milestones every bug report needs, and the release-default Info gate must keep them.
