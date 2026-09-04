# XSR-707 Launch card acceptance

## Scope and boundaries

This unit completes the launch page account roster, selected-profile presentation and switching,
isolates primary navigation from shell presentation intents, corrects the version-card geometry,
and refines the compact action capsules. Full login, profile editing and skin/cape management
pages remain separate Wave 7 slices, as agreed with the user.

- Only commands registered as shell navigation destinations replace the content page. Expanding
  or collapsing the rail must retain the page and account selection at every animation frame.
- Account facts and selection live in the shared host state. Switching uses the Foundation
  command router. Launch uses the selected account, never an implicit first-row fallback.
- Roster and selected-profile layouts are PXML. Desktop materializes credential-free roster
  views at the renderer frame boundary; workers only publish state. Repeated frames without a
  roster change must preserve entities, focus and scroll position.
- Empty, single-profile and multi-profile rosters are explicit states. Long rosters scroll
  inside the account card; external roster changes reconcile selection before launching.
- The version card is content-sized, not weighted to fill its column. Its descendants remain
  inside the card across minimum/default/wide windows, both shell styles and expanded rails.
- Capsules occupy only their current presented width, with an accessible action label. Following `apple-design`,
  they use restrained material contrast, consistent rounded geometry, immediate press feedback,
  focus-visible labels and interruptible presentation. Reduced motion retains static feedback.
  The backend only presents scene facts; it does not invent product actions or content.

## Renderer contract additions

`FramePreparing` is a render-thread-only materialization hook before the StateBridge drain and
layout, not an asynchronous event channel. Account row templates are compiled PXML loaded from
credential-free state snapshots at that boundary. The controller owns no native controls and
performs no account-service calls. `accounts.select-profile` validates the displayed roster
revision before publishing session selection; durable roster edits reconcile the selected index.

PXML buttons accept child content and a state-bound `Clickable` value. Input over non-interactive
children resolves to their button ancestor. A false enabled binding blocks pointer, keyboard and
automation activation without collapsing the layout. Scroll scenes omit fully clipped rows and
carry a viewport clip for partially visible nodes, shared by backend drawing and hit testing.
The selected card shows an embedded neutral avatar, profile name, provider and description; live
skin rendering and login/edit/appearance actions are not represented by nonfunctional buttons.

The follow-up acceptance includes a separate wrench `修改` capsule beside the expandable version
details action. Scene focus and focus-visible presentation are distinct: pointer focus must not
draw a keyboard focus ring. The Avalonia bridge must expose reachable content peers, synchronize
the renderer's focused entity into native keyboard focus, and notify accessible-name changes.
Peer metadata alone is not evidence that Narrator can discover and read the application.

At the user's request, version list/settings/modification are intentionally empty PXML subpages in
this unit. Back belongs to the original title-bar location, replacing the product title with
a back arrow and the subpage title; there is no duplicate navigation row in page content. Entry routes are functional; editing and
details content are not claimed complete. Hierarchical push/pop is separate from main-rail
destination changes, which clear the subpage history. `ui.launch.instances`, `ui.launch.settings`
and `ui.launch.modify` open distinct pages; the wrench always opens version modification,
including when entered from version settings. Back restores the entry control's focus.

Capsule progress is renderer-local geometry, like rail expansion. Its explicit PXML width is
the expanded endpoint; its height defines the collapsed circle. UI.Next measure/arrange, scene,
hit testing and accessibility bounds use the same presented width. The backend clock only
advances the renderer's progress; it must not reserve the expanded width or expand paint twice.
The version name and both actions share one horizontal row, including at minimum window size.
The settings action uses `lucide/settings` and the short caption `设置`; modification keeps
its wrench. Following `apple-design`, one radius scale is shared by both presentations:
16 for window/card surfaces, 12 for inset surfaces and navigation, 8 for compact badges,
and half the height for pill actions. Maximized native windows remain square at screen edges.
The title-bar and main-navigation surfaces have no internal corner rounding; only the native
outer window clips its corners. The bottom rail toggle uses the same icon/text layout and
hover/press animation module as destination items. A critically damped 0.34-second-response
spring drives rail width without a second easing curve.
Capsule captions end 6 logical pixels before the icon. The two-character actions expand to
72 pixels and the four-character version-list action to 98, without oversized interior gaps.

Native caption controls remain at the platform boundary. They must initialize their material
before the first scene, observe handled pointer input for press feedback, and retain operating
system minimize/maximize animation capability without replacing it with a simulated animation.
On Windows, full native decoration styles plus an empty drawn-decoration template retain DWM
animation capability. Windows 11's `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE` suppresses its outer
hairline without stripping caption/resize styles. The platform probe is guarded by OS version
and an HWND descriptor; it never runs on Headless, Linux or macOS. See the
[Microsoft DWM contract](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)
and [Avalonia 12 guidance](https://github.com/AvaloniaUI/Avalonia/issues/21212).
At close-collapse entry, disable DWM non-client rendering, the application shadow and the
whole-window acrylic backdrop. Scene/style updates must not restore them during the masked
animation. Normal minimize/maximize behavior retains native decoration capabilities.

Spring integration owns a coherent position/velocity pair instead of reading a previous,
asynchronously committed scene each tick. Retargeting inherits that pair; capsule hit geometry
and painted geometry remain identical without oscillating between old and new positions.

## Launch widget paging

The lower-right card preserves the original default content: the N Edition community notice
and the `你知道吗？` trivia card. Text comes from the legacy localization and documented built-in
hint corpus, not invented version/status widgets. The legacy shortcut dock was opt-in and
depends on the unported pinning feature; it is not represented by a dead control in this unit.
The current launch status remains a separate host-state-bound footer.

`VerticalPager` is a generated PXML recipe, loaded as a UI.Next pager component. Page index,
drag offset and settling velocity are renderer-local presentation state. Full viewport pages
are arranged vertically and clipped to the pager, so paint, hit testing and automation bounds
share the same geometry. Only the current page accepts input/accessibility traversal. Buttons,
wheel, arrow keys and captured vertical drags route into the renderer; a backend clock advances
the scene's position with an interruptible critical spring. Reduced motion snaps to the target.
Desktop supplies the two PXML pages and page-selection/refresh intents, not native widgets.
Following the user's `apple-design` revision, titles travel with their cards. There is no
visible `X/2` counter or header-arrow cluster. A vertically centered, right-side indicator
uses a 6x6 dot for inactive pages and a 6x16 pill for the current page, with an 8-pixel gap
and human-readable current-page labels. Layout and hit geometry use those presented sizes,
not equal-height capsule slots. Indicator geometry follows
the same presented page position. Dragging tracks the pointer one-to-one, with progressive
edge resistance, a short velocity history, projected landing and velocity-continuous settling.
Re-grabbing cancels the clock without a jump; wheel/button changes use a no-bounce spring.

## Acceptance

Tests exercise real renderer activation, account switching followed by the product start route,
roster publication from a worker, stable entities, scrolling, content containment, disabled input
and capsule focus/hover feedback. Run Desktop, Services, PXML, UI.Next and backend suites, the
architecture and formatting gates, and Desktop NativeAOT/trimmed `--validate-shell` smoke.

## Evidence

Validated locally on Windows with .NET 10:

- Release solution build: zero warnings/errors; formatting verification and `git diff --check` passed.
- CoreCLR suites: Services 177, UI.Next 68, PXML 35, Desktop composition 19, Avalonia backend
  6 top-level cases (including native peer/focus/invoke, captured drag, spring interruption,
  delayed-scene-read regression, caption input and close-shadow scenarios).
- Architecture: 29 projects passed; renderer benchmark gates passed, including zero allocation
  on clean frames and no layout for paint-only changes.
- Desktop NativeAOT and trimmed publishes passed. Both executables ran `--validate-shell`
  successfully for Experimental and LiquidGlass, each producing 50 scene nodes. Local Windows
  AOT disables debug-symbol generation to fit the build host's memory; CI uses its normal flags.
- Skia/headless images inspected at 850x500: both palettes, expanded settings capsule, blank
  title-bar-backed subpage, both widget pages and an intermediate slide frame. Geometry tests
  also cover 810x470 and 1280x800, both rail states and intermediate progress.

Native automation discovery/focus/invoke has executable evidence. Actual Narrator speech and
Windows DWM minimize/maximize/close appearance still require an interactive OS check; headless
testing is not presented as proof of those platform effects. No full login/edit/skin page,
optional shortcut dock or version editor content is claimed in this unit.
