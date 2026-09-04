# XSR-708 Profile-area presentation

At the user's request, remove the idle `就绪` line from the widget card and the account-card
footer entirely. Retain operational launch/error state for consumers; it must not reappear
as an idle card footer. Non-empty operational feedback is shown beside the launch action only
while there is something to report, so removing decorative text does not hide launch failures.
The launch page keeps the experimental two-column layout inspected read-only in the legacy
worktree. At the user's follow-up request, `apple-design` guides the account area's hierarchy
and interactions rather than reproducing the old controls pixel for pixel:

- Picker: an explicit title and return action, quiet inline context, then scrollable rounded
  56-pixel profile rows with thumbnail, username, secondary provider and a selected checkmark.
  Selection still uses the Foundation router with roster revision validation.
- Selected profile: a 72-pixel layered Minecraft head on an 88-pixel soft tile, centered
  22-pixel semibold name and 13-pixel account description. A compact, always-labelled switch
  capsule has a six-pixel icon gap. The identity block centers in available card space,
  without the legacy fixed 80-pixel top spacer or an idle footer.
- Use the existing renderer/backend press, hover and reduced-motion behavior. No decorative
  looping animation or nested glass surfaces; the page retains the shared 16/12/8/pill radii.
  Return and selection restore focus on the render thread, never from an auth worker.
- Two legacy default skin textures are deliberately migrated as assets, not legacy control
  code. The backend's finite `pcl/avatar/steve` and `pcl/avatar/alex` image registry draws the
  face/hat regions with nearest-neighbour sampling. It performs no networking or arbitrary
  file access. Desktop maps credential-free profile UUID parity to the default avatar and
  updates image presentation only at the render-thread frame boundary.
- PXML remains the product layout. There is no native account panel, ViewModel or service
  dependency in the renderer/backend. New login, profile editing, remote skin retrieval,
  create/import/export/delete workflows are not simulated with dead toolbar actions. The
  user subsequently requested login and import integration; that is the next independent
  acceptance unit and reuses the already-implemented authentication services.

Acceptance covers both roster states, selection and worker-published updates, default avatar
mapping, compact geometry, missing card footers, both shell palettes, keyboard activation,
long-list clipping, architecture, formatting and Desktop AOT/trim smoke.

## Evidence

- Release solution build: zero warnings/errors. Desktop 21, UI.Next 68, PXML 35,
  Foundation Services 177, and Avalonia backend 6 top-level executable tests pass.
- Architecture passes for 29 projects; source-format verification and `git diff --check` pass.
- Windows NativeAOT and trimmed Desktop publishes both run `--validate-shell` successfully
  in Experimental and LiquidGlass styles (50 nodes in the empty-profile smoke fixture).
- Real Skia headless screenshots inspect the selected profile, layered default avatars,
  three-row picker, selected checkmark, return action and both shell styles. This is not
  evidence of actual online login, user-skin retrieval or a manual Narrator speech check.

The removed idle footers supersede their earlier XSR-707 layout assertions. Authentication
protocol services already exist; product onboarding/login/import is tracked by XSR-709.
