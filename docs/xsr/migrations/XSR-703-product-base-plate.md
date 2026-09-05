# XSR-703 Wave 7 product base plate

## Outcome

The product shell is rebuilt as the legacy experimental base plate: a frameless transparent
window whose rounded, shadowed surface hosts a 52 px accent title bar, a 48 px icon navigation
rail (expanding to 120 px), and the content host. Startup runs the legacy choreography — a
136 px splash window that fades out over the shell window — and every motion follows the
fluid-interface rules: instant pointer-down feedback, continuous hover fades, critically damped
settles without overshoot, and animations that always start from the currently presented value
so interruptions stay continuous.

## Shell contract (supersedes XSR-701 geometry and destinations)

- `XsrUiShell` constants are the canonical chrome geometry: `TitleBarHeight` 52,
  `CollapsedRailWidth` 48, `ExpandedRailWidth` 120, `NavigationItemHeight` 42. The first rail
  item is inset 10 px from the title bar (`RailTopInset`) and the shell-owned toggle is inset
  12 px from the bottom (`RailBottomInset`).
- The primary destinations mirror the legacy experimental navigation registry:
  `navigation.launch` 启动, `navigation.download` 安装, `navigation.community` 资源,
  `navigation.settings` 设置, with commands `ui.navigation.*` derived from the IDs. The Desktop
  composition still rejects a replacement navigation list; future changes are explicit PXML and
  contract edits.
- The shell owns a rail toggle entity (`XsrUiShell.NavigationToggle`, button role, command
  `ui.navigation.expand`) pinned to the bottom of the rail (stack `StretchLastChild` with end
  alignment) in both composition paths. `ToggleNavigationExpanded` / `SetNavigationExpanded`
  rewrite the rail width; items always span the full rail width and the toggle reveals its
  "收起" label while expanded. Item text is always the destination label; whether the label is
  drawn beside the icon is a backend presentation decision derived from the committed item
  width. Expansion is ephemeral presentation mechanics owned by the shell and never becomes
  product state.
- The title bar displays the product title "Nexa Launcher" with the legacy typography tokens:
  17 px semibold in the palette's title text color (`TitleFontSize`/`TitleFontWeight` on the
  visual style), with secondary title text at 12 px. `XsrUiVisualStyle` gained `FontSize` (0 =
  role default) and `FontWeight` (400 = normal) as backend-neutral text facts.
- The Experimental palette mirrors the legacy light theme: solid accent title bar
  `#1370f3` with white text, light window `#fbfbfb`, soft rail `#f3f7fc`, text `#343d4a`, and a
  `#d5e6fd` hover tint. The selected destination presents as a 5×20 accent selection pill plus
  the darker selected text color — not a filled background. The palette gained `TitleBarText`,
  `NavigationHover`, and `SelectedNavigationText` and lost `ActiveNavigation*`. LiquidGlass
  keeps its dark glass materials and maps onto the same token set.

## Icons

- Scene nodes carry icons as `ImageSource` names (`NavigationItem.Icon` PXML property /
  `XsrUiImage` component); the loader attaches `XsrUiImage` for command inputs, and
  `PxmlIrNode.ImageSource` is a supported target of the `CommandInput` recipe.
- `PCL.UI.Next.Backend.Avalonia` embeds the frozen path table (`AvaloniaUiIcons`): the lucide
  icons used by the base plate (`play`, `package-plus`, `blocks`, `settings`, `menu`, `minus`,
  `square`, `x`) plus the product `pcl/window-restore`, transcribed from the legacy icon packs
  (lucide-static v1.17.0, ISC). Names keep the legacy `pack/key` spelling. There is no runtime
  SVG parsing and no icon asset files; unknown names degrade to text-only drawing.
- Drawing rules from scene facts: collapsed rail items (rect width ≤ 50) center the 20 px icon
  and hide the label; expanded items lead with the icon and draw the label 8 px after it;
  icon-bearing nodes without text (the rail toggle) center the icon. Window-action buttons draw
  the same registry through `AvaloniaUiSvgIcon`.

## Window and startup behavior (legacy parity)

- Startup shows a frameless 136×136 transparent topmost splash with the product icon at
  112 px, then shows the shell window underneath. The window's circular reveal starts at the
  surface's first committed scene (never over a blank window) and, when it completes, the
  window's own icon copy — identical pixels at the identical screen position — takes over and
  the splash closes instantly, so the icon never leaves the screen. Missing brand assets skip
  the splash — decoration is never a startup dependency. The icon assets are brand migration
  from the legacy checkout, embedded as plain managed resources of `PCL.Desktop` and resolved
  by the backend by stream, not through any asset framework.
- The shell window is 850×500 (min 810×470), centered, `WindowDecorations.None`, per-pixel
  transparent. Its surface is a 14 px-outset rounded (8 px) clip hosting the scene. The outset
  is a fully transparent buffer with **no self-drawn shadow**: per-pixel transparency is not
  guaranteed on every Windows machine, and a shadow rendered over an opaque margin exposes the
  rectangular window bounds as a hard-clipped frame. A real shadow should come from the
  platform (DWM corner/shadow integration), not from painting inside the transparent buffer.
  Maximized windows drop the margin and rounding.
- Size semantics must not be conflated: the **native outer window** (850×500, 810×470 min)
  includes the 14 px transparent outset on every side, so the **visible chrome surface** is
  28/56 px smaller per axis, and the **UI.Next semantic viewport** is the chrome's content
  rect (further inset by its padding). Acceptance tests must state which of the three sizes
  they measure; "810×470 minimum" refers to the native outer window. Title-bar presses drag through the native move loop (keeping Aero Snap) and
  double-click toggles maximize; eight invisible edge/corner grips (4 px edges, 14 px corners)
  hand presses to the native resize loop while Normal.
- Window actions remain the sole backend overlay: three 28 px circular buttons (minimize,
  maximize, close) with legacy parity geometry (4 px gaps, 12 px from the right edge, centered
  in the 52 px bar). Their tint follows the scene title-bar foreground; the maximize glyph
  swaps to `pcl/window-restore` while maximized. The close button routes through the backend's
  own close request because a programmatic `Close()` bypasses the cancelable `OnClosing` path
  on some platforms; system closes (Alt+F4) cancel through `OnClosing` and join the same
  collapse sequence.
- The scene surface paints with a transparent hit-test brush: its scene-node children are
  deliberately not hit-testable, so without it every pointer interaction would fall through to
  the window.

## Motion (fluid-interface rules)

`AvaloniaMotionTokens` is the vocabulary; the backend presents every scene fact through one
shared 16 ms frame clock (`AvaloniaUiMotion`) instead of the Avalonia animation stack — the
latter's `TransformAnimator` crashed under NativeAOT on transform keyframes, and the shared
clock keeps every rule testable and allocation-bounded:

- press scales to 0.97 on pointer-down over 120 ms and settles back on release;
- hover fades are 120 ms in, 180 ms out, mirrored and interruptible;
- the selection pill grows over 300 ms (ease-out) and collapses over 120 ms (ease-in);
- the rail expansion animates the rail-subtree and content-subtree rects over 200 ms on the
  same shared clock. A navigation-width change starts the interpolation and commits that arrive
  while it plays (the press, hover, and selection of the very click) re-target it from the
  currently presented value instead of snapping; rail items keep full rail width in both
  states, so the selection pill never shifts sideways during expansion, and expanded rows
  indent icons past the pill and reveal labels plus the toggle's collapse label ("收起");
- startup: the circular reveal expands over 340 ms starting at the first committed scene, then
  the inherited icon bounces 1→1.12→0 (110 ms up, 190 ms collapse) as the product content
  takes over;
- close reverses the sequence: the mask contracts over 280 ms while the icon bounces back in
  0→1.12→1, then the icon folds away (190 ms) and the window closes for real;
- reduced motion (renderer flag) applies every fact immediately and skips the mask sequences.

## Boundary compliance

`PCL.UI.Next` stays backend-free: it declares geometry constants, the toggle entity, palette
tokens, and `ImageSource` scene facts. `PCL.UI.Next.Backend.Avalonia` remains a pure function
of committed scenes — motion animates between scene facts from presented values, never reads
tree components or shell state. The splash, window chrome, grips, and window-action overlay
are native-window concerns at the backend edge, and the window-action overlay is still the
only non-scene control.

## Deliberate scope

- Icons are the nine embedded paths this plate needs; a lucide expansion or per-theme icon
  packs can extend `AvaloniaUiIcons` without contract changes.
- The legacy `UiLauncherLogo` splash setting and first-run wizard hand-off are not migrated;
  the splash is unconditional until settings surfaces exist.
- Dark-mode palettes beyond the two shell styles, page enter/exit transitions, and the
  window-transparency setting belong to the product page slices that attach to `Content`.
- The deterministic text metric under-reserves width for proportional fonts; authored title
  texts carry an explicit width until the renderer grows real font metrics.

## Verification

`PCL.UI.Next.Tests` cover the new geometry, pill/hover presentation tokens, rail toggle
behavior and its renderer intent path (60 tests). `PCL.Pxml.Tests` compile the Icon property
and load icon-carrying command inputs into the shell template (28 tests). The backend tests
prove automation peers unchanged and that selection/hover facts present synchronously under
reduced motion (3 tests). `--validate-shell` compiles the new PXML base plate and renders 11
semantic nodes under both NativeAOT and trimmed publishes; the windowed app was launched on a
real desktop to prove the splash/chrome/entrance path end to end.
