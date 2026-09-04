# XSR-712 Launch progress page (1:1 with the legacy launching overlay)

## Scope locked before implementation

1:1 replication of the legacy experimental launch overlay (`PageLaunchHomeExperimental`):
a full-page scrim with one centered card (width 420, min height 360, padding 32/36/32/28,
corner radius 20) narrating the launch while it runs.

- Card rows, top to bottom: loader glyph, title (`正在启动` / `游戏已启动`), instance name,
  a 4 px progress bar (corner radius 2, track surface, accent fill), four key/value rows —
  当前步骤 (stage), 登录方式 (method), 启动进度 (percent, `P0`), 下载速度 (visible only
  while a speed is reported) — the trivia hint box, and a full-width cancel button.
- The idle launch page fades under the overlay while it is visible and regains input when
  the overlay hides. Cancel asks the pipeline to stop, shows `已请求取消启动`, and returns
  to the idle page.

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
- Desktop: dispatching the launch intent shows the overlay with reset facts (progress 0,
  `初始化`, `等待账户档案`, random trivia); stage/percent/method/speed follow the state
  cells; `游戏已启动` replaces the title when launched; dispatch failure, cancel, or a
  terminal process session hides the overlay and restores the idle page.
- The overlay presentation is desktop-owned projection state (`launch.launching.*` cells);
  services never learns about the overlay.

## Notes

- Deferred polish (documented, not blocking): card box shadow, animated loader glyph, and
  the localized trivia box title reuse the existing widget hint store.
