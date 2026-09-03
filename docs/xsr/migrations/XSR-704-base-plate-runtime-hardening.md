# XSR-704 Wave 7 base plate runtime hardening

## Outcome

Closes the P0/P1 findings from the first hands-on review of the running base plate: the process
no longer outlives the shell window, animated rail geometry lives in UI.Next so the scene, the
hit test, and the drawn frame share one truth, and reduced motion is a live presentation
policy rather than a construction-time snapshot.

## P0 — desktop lifetime

`AvaloniaUiShellLifetime.Compose` (backend) now owns the lifetime wiring and is the only place
that touches `ShutdownMode`:

- the splash window never becomes the application main window and never owns the process
  lifetime — it is shown as decoration and closes itself when the shell window's reveal hands
  the icon over (under reduced motion this happens synchronously during composition);
- the shell window is the main window and `ShutdownMode.OnMainWindowClose` is set before it
  shows, so every real close path — the close button after the collapse animation, Alt+F4,
  and taskbar close — terminates the process through the normal lifetime contract. The old
  wiring set `OnExplicitShutdown` for the splash handoff and never restored it, so closing the
  shell could leave a hidden process behind.

Regression: the backend test project runs the real classic desktop lifetime under
`Avalonia.Headless` (`lifetime: splash never owns the process and main window close
terminates`), asserting the shutdown mode, the shell window as main window, and that closing
the main window returns from the lifetime loop. `Avalonia.Headless` is the single sanctioned
Avalonia package outside the backend project; the architecture gate records that exception and
it must never leak into product projects.

## P1 — rail geometry has one owner

Rail expansion animation moved from backend-private rect interpolation into UI.Next. The shell
owns an ephemeral `RailPresentationProgress` (0 collapsed, 1 expanded) plus
`SetRailPresentationProgress` and `RailWidthFor` (critically damped ease-out mapping); every
progress step re-commits the rail width into the tree, re-renders the scene, and therefore
advances the hit test, the accessibility tree, and the drawing together. The backend keeps
only the clock: on `NavigationExpandedChanged` it runs one `AvaloniaUiMotion` track that pumps
the shell progress to the new target. All backend presented-rect bookkeeping
(`_presentedRects`, `_railAnimation`, `_railProgress`) is deleted; `ArrangeOverride` arranges
from the committed scene rects.

This removes three defect classes at once: the first expansion could never start (the
collapsed steady state had no presented rect, so `from == target` short-circuited), re-targets
jumped (stale local progress applied to a new from/to segment), and the hit test disagreed
with the visible frame during motion.

## P1 — reduced motion is a live policy

`Renderer.ReducedMotion` is read dynamically at every motion decision point: the shell snaps
its presentation progress to the target when the flag is set (so no backend track ever has
anything to animate), the backend skips starting the rail track entirely, scene node controls
and the native window actions take a `Func<bool>` policy instead of a construction-time bool,
and hover/press/fact animations apply their end state immediately under the flag.

## Verification

`PCL.UI.Next.Tests` add the motion regressions requested for this unit:
`shell rail toggle expands and collapses` (start at the collapsed rect, intermediate geometry
is the scene truth, end at the expanded rect, re-target from the presented rect with no jump),
`rail presentation matches hit test during motion`, and `reduced motion skips rail
presentation motion` (62 tests total). `PCL.UI.Next.Backend.Avalonia.Tests` run the headless
lifetime regression (4 tests). Full gates, NativeAOT, and trimmed Desktop publishes remain
required; `--validate-shell` renders 11 semantic nodes.
