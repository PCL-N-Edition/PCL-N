# XSR-712 Launch progress page (1:1 with the legacy launching overlay)

## Scope locked before implementation

1:1 replication of the legacy launching card (`PageLaunchHomeExperimental`'s centered card:
width 420, min height 360, padding 32/36/32/28, corner radius 20) — presented as its own
navigation page, not an overlay: dispatching the launch pushes `LaunchingPage` onto the
navigation stage (title-bar subpage title `正在启动`), and failure, cancellation, or the
game process ending pops back to the launch page. Real-device review rejected the
full-page-scrim overlay: the card lost its visual boundary against the scrim and the page
flow reads better as a dedicated page.

- Card rows, top to bottom: loader glyph, title (`正在启动` / `游戏已启动`), instance name,
  a 4 px progress bar (corner radius 2, track surface, accent fill), four key/value rows —
  当前步骤 (stage), 登录方式 (method), 启动进度 (percent, `P0`), 下载速度 (visible only
  while a speed is reported) — the trivia hint box, and a full-width cancel button.
  Spacing lives on child margins, never on the card's padding: the renderer paints only the
  content rect (XSR-703 gotcha), so container padding would leave the card's border and
  background inset from its layout bounds. The vertical budget is trimmed so the card fits
  the minimum window, and the prompt/hint boxes carry explicit heights because the measure
  pass does not aggregate plain-element children.
- Cancel asks the pipeline to stop, shows `已请求取消启动`, and pops back to the launch
  page. The title-bar back action is refused while a launch pipeline is running.
- Key/value rows use a fixed-width label column (64px) so the value column aligns, matching
  the legacy two-column stage rows.

### Launch progress contract (migrated from the legacy stage model)

- Stage weights: get_java 4, login 15, complete_files 15, get_arguments 2,
  extract_natives 2, pre_launch 1, custom_command 1, start_process 2, wait_window 1,
  end 1 (total 44). `progress = completedWeight / total`, clamped to 0..1.
- While a stage runs, a heartbeat reports soft progress every 120 ms: soft fraction grows
  by 0.05 per tick, clamped at 0.92 of the stage weight.
- Reports publish into shared host state as cells: `minecraft.launch.active`,
  `minecraft.launch.stage` (stable token: `get_java`, `login`, `complete_files`,
  `get_arguments`, `extract_natives`, `pre_launch`, `start_process`, `end`),
  `minecraft.launch.progress` (double 0..1), `minecraft.launch.method`,
  `minecraft.launch.speed`, `minecraft.launch.launched` (bool). Services publish stable
  tokens; the desktop controller owns the Chinese display strings.
- XSR pipeline mapping: select/install Java → get_java; resolve account → login; resolve
  manifests → complete_files; create plan → get_arguments; native validation/extraction →
  extract_natives; working-directory preparation → pre_launch; process start →
  start_process; launched=true with progress 1 after the process is created. The legacy
  custom_command and wait_window stages have no XSR counterpart yet and are omitted until
  those features migrate (progress jumps across their weights; documented divergence).
- Cancellation: the coordinator keeps the active launch's cancellation source; a
  `minecraft.launch.cancel` command stops the pipeline between stages.
- Java acquisition approval (legacy parity): before any automatic runtime download the
  pipeline pauses and publishes `minecraft.java.acquire.{pending,component,major}`; a
  `minecraft.java.acquire.decide` command (approve/deny) resolves the wait, denial fails
  the launch with `minecraft.java_unavailable`, and cancellation aborts the wait. XSR-714
  supersedes the temporary inline prompt with the shared window-internal dialog surface.

### Progress bar presentation

- New PXML element `Progress` (role ProgressBar, leaf, no children) with a `Value` state
  binding to a double cell. The element paints as the accent fill bar; its parent supplies
  the track surface.
- The fill width follows a renderer-owned presented value (never the raw state value): the
  backend animates the presented fraction toward the state target on the shared motion
  clock so fast stage jumps catch up smoothly, and reduced motion snaps to the target.
  This mirrors the legacy catch-up (`show += (target - show) * 0.2 + 0.005` per report)
  without freezing geometry into services state.

### Acceptance

- Services: stage-weight math matches the legacy table; the coordinator reports the
  documented stage sequence with heartbeat clamping; cancel stops an active launch; cells
  carry the report facts; no credentials or payload data enter the progress cells.
- UI.Next: the Progress element lays out its fill from the presented value;
  `SetProgressPresentation` marks layout dirty; reduced motion snaps. PXML compiles the
  element and its `Value` binding.
- Desktop: dispatching the launch intent enters the launching page with reset facts
  (progress 0, `初始化`, `等待账户档案`, random trivia); stage/percent/method/speed follow
  the state cells; `游戏已启动` replaces the title when launched; dispatch failure,
  cancel, or a terminal process session pops back to the launch page. The no-profile
  preflight stays on the launch page and never enters the launching page.
- The launching display strings are desktop-owned projection state (`launch.launching.*`
  cells); services never learns about the page.
- Log messages are English-only (review rule): services log facts in English, the desktop
  controller owns the Chinese display strings.

## Review round 3 (W7 review): render-thread boundary and pipeline integrity

- P0 render-thread containment: the launching page closes through the frame boundary. Any
  thread (process-exit state publication, dispatch continuations after
  `ConfigureAwait(false)`) may only raise the pending-close flag; `OnFramePreparing` — the
  render-thread hook — drains it and performs the navigation-pop/tree/focus mutations, the
  same edge the feedback presenter uses. Tests pump frames (`Render` raises
  `FramePreparing`) to drive the drain.
- Offline UUIDs follow the RFC byte order (`Convert.ToHexString` of the version/variant
  nibbles), matching Java `nameUUIDFromBytes`; golden values Player/Steve/Alex lock it.
  Persisted offline profiles written by the buggy alpha (Guid-constructor byte swap) are
  recognized via `LegacyMismatchedUuid` and repaired durably at roster load.
- The coordinator is single-flight: a `_launchGate` lock registers the active pipeline, a
  second concurrent `minecraft.start` is rejected with `minecraft.launch_already_active`,
  and the `finally` clears only its own registration. Cancel and acquisition decisions read
  the same gate.
- Progress truth is one coherent snapshot cell (`minecraft.launch.snapshot`, carrying the
  launched `SessionId`); the stage/progress/method/speed/launched cells are derived
  projections. The `end` report carries the session id, and that session's terminal state
  resets the truth via `Stop(sessionId)` — `minecraft.launch.*` never outlives the game.
- The desktop observer closes the launching page only when THAT session id is terminal; other
  running games keep the flow alive.
- `IAccountLaunchIdentityResolver` (accounts side) owns provider specifics: offline
  derivation, Microsoft refresh (composed capability refreshes before launch and persists the
  rotated tokens; without the capability the persisted token is used and the gap logged), and
  an honest refusal for LittleSkin/third-party/NCloud (`accounts.launch_not_supported`) until
  Authlib Injector preparation migrates. The launch button is disabled for those kinds
  instead of failing after the fact.
- `DefaultMinecraftRootProvider` resolves the platform vanilla root (Windows
  `%APPDATA%\.minecraft`, Linux `~/.minecraft`, macOS `~/Library/Application Support/
  minecraft`); the composition root no longer guesses paths.
- `XsrCompositeStateObserver.Subscribe` returns an unsubscription handle so disposed
  controllers detach from the store fan-out.
- The notification stack is bounded for real: same level+message refreshes instead of
  stacking, and beyond 200 entries the oldest is evicted.
- One version truth: the composition root's `LauncherBuildInfo` (informational version,
  channel, semantic core) feeds the shell title version and the game command line's
  `${launcher_version}`.

## Review round 4: fast-exit JVMs, frame loops, and the wait-for-window stage

- A JVM that dies between process creation and the coordinator's subscription no longer
  leaves `minecraft.launch.*` stuck at launched: the terminal callback is a named handler
  subscribed BEFORE the end report, and the session's current snapshot is re-run after
  subscribing (subscribe-then-recheck). Regression: a process port returning an
  already-exited JVM (corpus with a working Java path) must reset the truth.
- `UpdateLaunchButton` is a pure per-frame projection: an unsupported account disables the
  button with the label `暂不支持启动` and never emits feedback — the previous inline toast
  re-raised the feedback Changed event every frame and spun a permanent render loop.
- The Microsoft refresh capability is actually wired: the account onboarding runtime exposes
  `LaunchIdentityResolver` (its own Microsoft auth service + the publish-time client id
  embedded as `PclMicrosoftClientId` assembly metadata), and the Minecraft runtime composes
  it. Refresh now runs BEFORE the persisted-credential check (a valid refresh token restores
  an expired or missing access token) and prefers the refreshed username/UUID.
- The runtime resolver recognizes the alpha's byte-swapped offline UUID regardless of the
  best-effort roster rewrite, so a read-only profiles.json still launches with the correct
  identifier.
- `launched` re-purposes the cancel button to `返回`: with no pipeline left, it pops the page
  without touching the game process.
- The legacy wait-for-window stage is real again: `IMinecraftWindowProbe` (Windows EnumWindows
  PID+visible-title match; other platforms report absence) is polled after process start with
  a 2-minute limit, the narration holds at `wait_window`, and a process that ends first
  short-circuits the wait. `WaitWindowWeight` (1) left the reserved pool.
- `XsrCompositeStateObserver.Add` is gone — `Subscribe` returns the only handle, and the
  composition root scopes it. Evicted notifications dispose their timers outside the gate.

## Notes

- Deferred polish (documented, not blocking): card box shadow, animated loader glyph, and
  the localized trivia box title reuse the existing widget hint store.
