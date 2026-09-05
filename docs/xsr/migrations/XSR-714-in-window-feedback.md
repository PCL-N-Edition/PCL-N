# XSR-714 In-window feedback surfaces

## Decision

Wave 7 owns one Desktop feedback service and one PXML/UI.Next presenter for transient
notifications and modal decisions. Feedback is always rendered inside the launcher scene; it
must not open a native child window, system message box, or backend-owned popup.

The feedback service belongs to `PCL.Desktop`, not `PCL.Services`. Notifications and dialogs are
product presentation state. Foundation, account, and Minecraft services continue to publish
typed results/state without acquiring a dependency on Desktop, Avalonia, or renderer types.
Desktop controllers translate those results into feedback requests at the composition edge.

## Notification contract

- Every notification is shown in one shared stack anchored 18 logical pixels to the right of the
  navigation rail's currently presented edge and 18 pixels above the window bottom. The anchor
  follows the same committed rail geometry during its spring, keeping the notice close to the
  collapsed rail without covering either rail state. The stack is a non-modal stage overlay, so
  the rest of the page remains usable.
- A notification presents at most two wrapped message lines with an end ellipsis. The immutable
  service message and accessible label remain complete. Activating the notice (but not its close
  button) opens that complete message in the shared dialog with one `OK` action.
- Notice height is intrinsic to its clamped copy: one-line messages use the compact one-line
  surface and two-line messages grow by exactly one line box. Decorative accents stretch to that
  result; no fixed two-line height leaves empty space beneath short content.
- The bounded stack stays pinned to its bottom edge as content grows and shrinks. A closing notice
  stops participating in stack flow immediately while its visual exit completes, so remaining
  notices reflow without an exit-duration gap. Overflow recovery re-clamps the continuous scroll
  offset on every arrange; an empty host and all host whitespace are pointer-through.
- The only public levels are exactly `Info`, `Warn`, and `Error`:
  - `Info`: blue, automatically dismissed after 5 seconds.
  - `Warn`: yellow, automatically dismissed after 15 seconds.
  - `Error`: red, never automatically dismissed.
- Every level has a visible level label, a distinct icon, an accessible live-region policy, and
  a manually invokable close button. Color is supplementary, never the only severity signal.
- The service owns the timers and is safe to call from worker continuations. It publishes a wake
  revision into Host State; the presenter reconciles PXML entities only from the renderer's
  `FramePreparing` boundary. No timer or worker mutates `XsrUiTree`.
- Notifications enter upward from their lower-left anchor and leave along the inverse path.
  Existing notices retain their presented position while the stack reflows. Motion starts from
  the current presented value and is replaceable; reduced motion uses the settled geometry and
  a short/static opacity equivalent.

The former launch-card `launch.status` footer and account-form one-shot `feedback` footer are
removed. Ongoing, task-local content such as account authorization progress and launch-stage
progress remains inline because it describes the current task rather than a one-shot notice.

## Dialog contract

- A dialog is a modal stage overlay above the complete scene, including title bar and primary
  navigation. A dim scrim blocks pointer input and removes the underlying scene from keyboard
  and accessibility traversal while the dialog is active.
- The dialog surface is authored with the PXML `Dialog` control. It exposes a semantic title,
  message, primary action, secondary action, initial focus, Escape cancellation, and focus
  restoration. All actions return through UI.Next intent; the Avalonia backend never invokes a
  product service directly.
- The surface materializes with a restrained, critically damped lift/scale and the scrim fades
  with it. Dismissal follows the same path in reverse. Reduced motion removes the spatial move.
- The message lives in a bounded viewport between the fixed header and actions. Long content has
  a visible vertical indicator and uses a double-precision continuous pixel offset: wheel and
  precision-touchpad deltas scroll freely rather than snapping to pages or rows. The complete
  message remains available to accessibility, and short messages leave the indicator hidden.
- Decision dialogs retain primary and secondary actions. Notification-detail dialogs intentionally
  expose one `OK` action; Escape resolves that informational surface through the same close path.

The Java runtime acquisition gate introduced by XSR-712 now uses this dialog. The launch
coordinator remains paused on its typed decision command; Desktop projects the acquisition
state into the dialog and dispatches approve/decline after the user chooses. The launching page
continues to show progress behind the modal and no longer embeds its own confirmation panel.

## Renderer boundary

`XsrUiStage` marks overlay roots explicitly. Stack measurement and normal flow ignore marked
overlay children and arrange each one against its parent's full content rectangle, preserving
the checked-in PXML shell geometry. Modal overlay accessibility suppresses earlier siblings,
while a non-modal notification host only occupies and intercepts its lower-left rectangle.

Scene nodes carry backend-neutral live-region, text-overflow, scroll-indicator, and overlay-motion
facts. Avalonia maps them to
native automation live settings and visual presentation only; layout, hit testing, modality,
focus routing, and command identity remain canonical UI.Next facts.

Modal overlay accessibility suppresses earlier siblings. Non-modal input lookup searches through
decorative or empty overlay geometry for the topmost actual input, so notification-host whitespace
does not intercept the page beneath it.

## Acceptance

- UI.Next tests lock overlay flow exclusion, modal hit/accessibility isolation, focus traversal,
  Escape routing, and reduced-motion state.
- PXML tests lock the `Notification` and `Dialog` control catalog entries and runtime semantics.
- Desktop tests lock the exact 5 s / 15 s / permanent policy, manual close for all levels, the
  navigation-safe lower-left placement, two-line summary/detail routing, bottom-pinned overflow
  recovery, immediate close reflow, scrollable one-action details, Java acquisition dialog
  routing, focus restoration, and migration of launch/account one-shot footers.
- Avalonia backend tests lock live-region mapping and interruptible notification/dialog motion.
- Managed suites, architecture/format gates, trimmed Desktop validation, and NativeAOT Desktop
  `--validate-shell` must pass before the unit closes.
