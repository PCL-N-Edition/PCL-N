# XSR-723 Task center, bottom-right bubble, and the task page

The legacy task manager returns as three coordinated pieces on the shared host store:

- **`Nexa.Services.Tasks.TaskCenterService`** — a foundation service (one writer of its cells)
  that tracks user-visible background tasks. Entries are the legacy task-manager cards
  (title, stage, detail, progress, file counts, speed, state, cancel-ability, step plan);
  terminal entries stay visible until dismissed, abandoned handles surface as failed, and a
  retention cap (30) bounds terminal history. The summary cell aggregates active count,
  visible count, average progress, summed speed, and remaining files.
- **Stage monotonicity** is preserved from the legacy `TaskManagerStagePlanner`: progress
  never rewinds when a later phase reuses a generic stage name such as 下载文件, keyword
  resolution maps unknown stage names onto the plan, and 已完成/就绪 mark completion.
- **Routes** — `tasks.center.cancel`, `tasks.center.dismiss`, `tasks.center.clear` are typed
  XSR commands; cancel routes to the owning task's token (the service owns the CTS), dismiss
  only accepts terminal entries.

The bottom-right bubble is the legacy extra-dock task bubble: a persistent shell-level
overlay (round 44px capsule at the dock inset) whose translucent fill **rises from the
bottom edge** with aggregated progress. UI.Next adds `XsrUiProgressFillAnchor.Bottom` for the
rising fill and `XsrUiProgress.SetTarget` so runtime-built entities drive presentation-only
targets (the backend catch-up animation still applies). The bubble is hidden while the task
page is staged, filled at 100% once every task is done (completion is acknowledged, not timed
out), reconciles at `FramePreparing` from the summary cell, and workers only publish a wake
cell — no tree mutation off the render thread.

The task center page is a pushed shell subpage (the shared back affordance pops it):
a header with the aggregate and a bulk clear-finished action, one card per entry
(state icon, stage copy, progress fill, file counts, per-step rows, error line, and a
trailing action that flips between cancel and dismiss), and an explicit empty state. Card
actions resolve their entry by walking the source entity to the card root and dispatch the
typed routes; the bubble reclaims the corner the frame after the page leaves the stage.

## Behavorial parity notes

- Legacy `RefreshTaskManagerButton`: `show = hasVisibleTask && !IsTaskManagerVisible`;
  progress = active average, 1.0 when only finished tasks remain. Preserved exactly.
- Legacy stage plan names are kept (版本信息 / 游戏文件 / 加载器 / 附加组件 / 完成) so
  planner keyword resolution behaves identically.
- Speed formatting keeps the legacy unit ladder (B/s → GB/s, F1 below the first unit).

## Presentation refresh (2026-09-09)

The corner task control uses the launch page's light neutral surface, compact count/status and a
thin progress track. Its exit can reverse immediately when work becomes visible again. The task
page separates title, current stage, progress and actions; detailed steps expand on demand instead
of occupying every card. Presentation changes remain renderer-thread-only and unchanged snapshots
must leave the scene clean. Task ownership and cancel/dismiss routes remain Service contracts.

## Capability boundary corrections (2026-09-09, review round)

- **Retention cap covers every terminal kind.** All four terminalizations (complete, fail,
  cancel, abandon) commit through one `CommitTerminal` that disposes the token, publishes the
  terminal entry, and bounds terminal history to 30 — the cap is a capability contract over
  `_registrations` and the shared state collection, not a UI nicety.
- **CanCancel is enforced at the service boundary.** `RequestCancel` rejects a protected task
  outright and returns a distinguishable `NotCancelable` result; the route layer maps it to
  `tasks.task_not_cancelable` so an enforced boundary is never disguised as an unknown id.
  Hiding the button is presentation, not protection.

### Zero-progress presentation

A progress node remains in the scene while its viewport intersects the parent clip, even when
its initial fill has zero width. This lets the backend start the progress animation. Clipping
continues to use the viewport; offscreen progress nodes remain culled.

## Navigation and installation follow-up (2026-09-11)

Window content reaches the system frame. Returning from launch progress only hides its page;
cancel remains explicit. Navigation titles follow the staged page regardless of its entry point.
Leaving Java installation clears transient selections. Service eligibility hides a successfully
loaded empty catalog; pending or failed catalogs remain reachable. Catalog visibility changes
preserve the current page identity without animating through removed indices. The launch widget
persists its page through Settings. Scroll recycling must not replay entry animations.

Install commands distinguish the Minecraft version from an optional instance name. Addon
commands can carry the selected immutable download descriptors from the sealed catalog state;
the installer validates the URL and file name and uses the existing checksum transfer pipeline.
This prevents a later provider outage or a changed merge identifier from losing the selection.
The optional descriptors preserve compatibility with callers that resolve by game and version.
