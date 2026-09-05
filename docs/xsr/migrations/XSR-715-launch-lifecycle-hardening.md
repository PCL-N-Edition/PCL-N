# XSR-715 Launch lifecycle and composition hardening

## Outcome

Wave 7 treats a Minecraft launch as one owned, single-flight operation whose service truth can
be published from any thread while every UI.Next inspection and mutation remains on the render
thread. The operation identifies the exact process session it created, resets its progress when
that session ends, and never infers completion from unrelated games in the process roster.

This unit also closes the small compatibility/composition gaps found while exercising that
boundary: vanilla offline UUID byte order, launch-account capability, platform Minecraft roots,
observer lifetime, bounded feedback, and one launcher build identity.

## Render-thread boundary

`XsrUiTree`, `XsrUiNavigator`, and `XsrUiRenderer` are render-thread owned. A state observer or an
async command continuation may only capture immutable service facts, set an atomic pending flag,
or enqueue a Desktop UI action. It requests a frame by publishing a Desktop-owned wake revision.
`LaunchPageController.OnFramePreparing()` is the sole consumer and performs navigation, focus,
component inspection, and component mutation.

In particular, neither of these paths may close the launching page directly:

```text
OS Process.Exited -> process state publication -> Desktop observer
command Completion after ConfigureAwait(false) -> Desktop continuation
```

Both paths request a close; the next frame drains it. Disposing a controller unsubscribes its
observer before destroying controller-owned state, so the store cannot retain or call a dead
controller.

## Single-flight launch and coherent truth

`minecraft.start` is a runtime command contract and is single-flight independently of button
debouncing. Admission uses one lock/CAS boundary. A second overlapping start returns the stable
`minecraft.launch_already_active` error. Cancellation captures the admitted operation under the
same boundary, and `finally` may clear only the exact cancellation source it admitted.

Launch progress is one typed cell:

```text
MinecraftLaunchProgressSnapshot
  Active
  Stage
  Progress
  Method
  DownloadSpeed
  IsLaunched
  SessionId
```

The former scalar keys remain derived compatibility projections of that cell; they are never
published independently. One report therefore causes one authoritative state transition. The
successful end report includes the executor-created session ID. Desktop follows only that ID,
and the progress publisher resets the active/stage/progress/launched facts when that exact
session becomes terminal. An older game exiting cannot reset a newer launch.

## Compatibility and composition

- Offline UUID v3 bytes use RFC/Java order. Exact UUIDs previously persisted by the XSR alpha
  through `.NET Guid(byte[])` byte swapping are detected by username, migrated durable-first,
  and corrected at launch even if the migration write cannot complete.
- Account presentation exposes whether the current profile can be launched by the composed
  identity pipeline. Providers awaiting Authlib Injector preparation are not presented as ready.
  Provider refresh/preparation belongs behind an account launch-identity resolver, not in the
  Minecraft coordinator.
- The default Minecraft root is supplied by a platform provider: `%APPDATA%/.minecraft` on
  Windows, `~/.minecraft` on Linux, and `~/Library/Application Support/minecraft` on macOS. A
  persisted explicit setting may override it.
- The composition root resolves one `LauncherBuildInfo` from assembly informational version and
  passes it to logging, shell presentation, and `${launcher_version}` request construction.
- The feedback service has a finite retained count. Repeated identical permanent errors aggregate
  rather than allocating an unbounded PXML subtree per occurrence.

## Acceptance

- A launch failure, cancellation, or process-exit callback cannot change navigation/tree/focus
  until `FramePreparing` drains the pending action.
- Concurrent start commands admit exactly one pipeline and the loser returns
  `minecraft.launch_already_active`; cancellation always targets the admitted operation.
- Readers observe either the complete old or complete new launch progress snapshot, never a mix
  of stage/progress/method values.
- The launching page closes for its own terminal session even while another game remains running;
  an unrelated terminal session does not close it.
- Terminal process state resets launch progress and an old session cannot reset a newer launch.
- `Player`, `Steve`, and `Alex` match vanilla offline UUID golden values, including persisted
  alpha-profile repair.
- Unsupported account providers expose a reason and cannot enable the launch action.
- Windows, Linux, and macOS root fixtures resolve their canonical default paths.
- Disposed observers receive no later publications; feedback remains within its retained bound.
- UI display version, diagnostic version, and Minecraft launcher token come from one build fact.
- Managed, architecture, formatting, Desktop NativeAOT `--validate-shell`, and trimmed Desktop
  `--validate-shell` gates pass.
