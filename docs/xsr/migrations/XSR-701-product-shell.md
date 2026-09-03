# XSR-701 Wave 7 product shell foundation

## Outcome

Wave 7 starts with the product chrome that every vertical slice shares: a top title bar, a
primary left navigation rail, and a content host. The chrome is authored in the checked-in
`PCL.Desktop/Ui/Shell.pxml`, compiled through the control catalog into UI.Next entities, and can be
presented in either the current Experimental style or the Apple-inspired LiquidGlass style.

XSR-702 tightens the initial shell into the renderer-model boundary: the backend commits the
immutable UI.Next scene and no longer recreates a parallel application visual tree.

## Locked contract

- `XsrUiShell` owns the semantic tree. The title bar, navigation rail, navigation items, and
  content host have stable entity handles and accessibility roles (`TitleBar`, `Navigation`,
  `NavigationItem`, and `Content`). Navigation IDs are `XsrSemanticId` values and never depend on
  Avalonia control names.
- The checked-in PXML product shell owns the six product destinations `navigation.home`,
  `navigation.downloads`, `navigation.instances`, `navigation.library`, `navigation.accounts`,
  and `navigation.settings`. Its Desktop composition rejects a replacement navigation list rather
  than pretending a fixed PXML template is dynamic. A future product navigation change is an
  explicit PXML and migration-contract change.
- `Experimental` and `LiquidGlass` share layout, input, focus, selection, commands, and content
  ownership. They differ only through `XsrUiShellPalette` and backend-neutral visual tokens.
  Experimental uses opaque surfaces and a compact high-contrast active rail item. LiquidGlass uses
  layered translucent surfaces, a restrained white border, rounded active items, and an optional
  compositor blur. If a platform cannot provide blur, the translucent fallback remains valid and
  readable; no fake screenshot or bitmap blur is required.
- `SetStyle` changes the palette in place and preserves the selected destination. Pointer and
  keyboard activation select the same destination and emit the item's semantic command through the
  normal UI intent sink. The PXML title bar owns the style-toggle command; minimize/maximize/close
  remain native window actions.
- `PCL.UI.Next` remains backend-free. `PCL.UI.Next.Backend.Avalonia` is the only project that
  references `Avalonia.Desktop`. Its `AvaloniaUiSceneSurface` consumes the immutable scene only and
  owns final drawing, native input translation, and accessibility mapping. The small native window
  action overlay owns only minimize/maximize/close. `PCL.Desktop` creates the UI runtime context
  before Foundation, passes its state bridge as the one host-store observer, compiles the PXML shell
  into that same tree, and starts the backend host.
- The title bar is 58 logical pixels and the navigation rail is 236 logical pixels in the
  canonical UI.Next layout. The final child of the root/body stack stretches into remaining
  viewport space so the content host is never dependent on caller-provided filler controls.
- Desktop starts in `Experimental` by default; `--ui-style=liquid-glass` (or
  `--liquid-glass`) selects the alternate presentation. `--validate-shell` compiles the embedded
  PXML template, renders its semantic scene, and exits without opening a native window for CI and
  smoke checks.

## Interaction and accessibility

Every navigation item is focusable and clickable, has a visible text label alongside its icon, and
maps from scene semantic role/label into native accessibility properties. Native pointer and
keyboard input call `XsrUiRenderer` first, so correlation IDs, hover, focus, pressed state,
selection, and intent emission have one owner. The title bar exposes a drag surface and labelled
native window actions. Selection is separate from focus/pressed state and is copied into the
immutable scene, so a future backend or PXML slice can bind to the same state without observing
CLR events.

## Non-goals

This unit does not migrate product pages, route navigation to Foundation/Minecraft commands, or
define the Plugin UI IR. It does not copy legacy Avalonia views. Those vertical slices will attach
to the existing `Content` entity through PXML/UI.Next in subsequent Wave 7 units.

## Verification

`PCL.UI.Next.Tests` covers both palettes, canonical title/navigation/content geometry, selected
state persistence across style changes, pointer activation, intent emission, unknown-route
rejection, and the production-ready state-bridge context. The Avalonia backend and Desktop
composition build against `Avalonia.Desktop` 12.1.0; the architecture gate continues to allow that
package only at the backend boundary.
