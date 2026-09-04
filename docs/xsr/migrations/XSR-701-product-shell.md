# XSR-701 Wave 7 product shell foundation

> **Superseded in part by XSR-703.** The shell geometry (58 px title bar, 236 px rail), the six
> fixed destinations, the dark Experimental palette, and the title-bar style-toggle button below
> were replaced by the legacy-experimental base plate in
> [XSR-703](XSR-703-product-base-plate.md). The renderer/backend boundary rules on this page
> remain in force.

> **Style scope updated by [XSR-712](XSR-712-defer-liquid-glass.md).** LiquidGlass is deferred;
> only the Experimental product presentation is currently available.

## Outcome

Wave 7 starts with the product chrome that every vertical slice shares: a top title bar, a
primary left navigation rail, and a content host. The chrome is authored in the checked-in
`PCL.Desktop/Ui/Shell.pxml`, compiled through the control catalog into UI.Next entities, and can be
presented in the current Experimental style.

XSR-702 tightens the initial shell into the renderer-model boundary: the backend commits the
immutable UI.Next scene and no longer recreates a parallel application visual tree.

## Locked contract

- `XsrUiShell` owns the semantic tree. The title bar, navigation rail, navigation items, and
  content host have stable entity handles and accessibility roles (`TitleBar`, `Navigation`,
  `NavigationItem`, and `Content`). Navigation IDs are `XsrSemanticId` values and never depend on
  Avalonia control names.
- The checked-in PXML product shell owns the primary destinations. ~~Six destinations
  `navigation.home` … `navigation.settings`.~~ **XSR-703 replaced these with the four
  legacy-experimental destinations `navigation.launch`, `navigation.download`,
  `navigation.community`, and `navigation.settings`.** Its Desktop composition rejects a
  replacement navigation list rather than pretending a fixed PXML template is dynamic. A future
  product navigation change is an explicit PXML and migration-contract change.
- `Experimental` is the only product presentation. It uses opaque surfaces and a compact
  high-contrast active rail item. The former LiquidGlass palette is removed by XSR-712; generic
  backend-neutral material tokens are not an available product theme.
- `SetStyle` changes the palette in place and preserves the selected destination. Pointer and
  keyboard activation select the same destination and emit the item's semantic command through the
  normal UI intent sink. There is no product style-toggle command; minimize/maximize/close remain
  native window actions.
- `PCL.UI.Next` remains backend-free. `PCL.UI.Next.Backend.Avalonia` is the only project that
  references `Avalonia.Desktop`. Its `AvaloniaUiSceneSurface` consumes the immutable scene only and
  owns final drawing, native input translation, and accessibility mapping. The small native window
  action overlay owns only minimize/maximize/close. `PCL.Desktop` creates the UI runtime context
  before Foundation, passes its state bridge as the one host-store observer, compiles the PXML shell
  into that same tree, and starts the backend host.
- ~~The title bar is 58 logical pixels and the navigation rail is 236 logical pixels in the
  canonical UI.Next layout.~~ **XSR-703: the title bar is 52 logical pixels and the navigation
  rail is a 48 px icon rail that expands to 120 px; see
  [XSR-703](XSR-703-product-base-plate.md).** The final child of the root/body stack stretches
  into remaining viewport space so the content host is never dependent on caller-provided filler
  controls.
- Desktop starts in `Experimental`; old LiquidGlass arguments no longer select another style.
  `--validate-shell` compiles the embedded
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

`PCL.UI.Next.Tests` covers the Experimental palette, canonical title/navigation/content geometry,
removed-style rejection without state mutation, pointer activation, intent emission, unknown-route
rejection, and the production-ready state-bridge context. The Avalonia backend and Desktop
composition build against `Avalonia.Desktop` 12.1.0; the architecture gate continues to allow that
package only at the backend boundary.
