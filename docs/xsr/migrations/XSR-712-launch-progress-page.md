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

## Notes

- Deferred polish (documented, not blocking): card box shadow, animated loader glyph, and
  the localized trivia box title reuse the existing widget hint store.
