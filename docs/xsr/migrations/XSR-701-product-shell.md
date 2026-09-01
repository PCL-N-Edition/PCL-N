# XSR-701 Wave 7 product shell foundation

## Outcome

Wave 7 starts with the product chrome that every vertical slice shares: a top title bar, a
primary left navigation rail, and a content host. The chrome is authored in the checked-in
`PCL.Desktop/Ui/Shell.pxml`, compiled through the control catalog into UI.Next entities, and can be
presented in either the current Experimental style or the Apple-inspired LiquidGlass style.

## Locked contract

- `XsrUiShell` owns the semantic tree. The title bar, navigation rail, navigation items, and
  content host have stable entity handles and accessibility roles (`TitleBar`, `Navigation`,
  `NavigationItem`, and `Content`). Navigation IDs are `XsrSemanticId` values and never depend on
  Avalonia control names.
- The shell defaults to the six product destinations `navigation.home`,
  `navigation.downloads`, `navigation.instances`, `navigation.library`, `navigation.accounts`,
  and `navigation.settings`. A composition root may provide another ordered list, but duplicate
  IDs and an unknown initial destination fail deterministically.
- `Experimental` and `LiquidGlass` share layout, input, focus, selection, commands, and content
  ownership. They differ only through `XsrUiShellPalette` and backend-neutral visual tokens.
  Experimental uses opaque surfaces and a compact high-contrast active rail item. LiquidGlass uses
  layered translucent surfaces, a restrained white border, rounded active items, and an optional
  compositor blur. If a platform cannot provide blur, the translucent fallback remains valid and
  readable; no fake screenshot or bitmap blur is required.
- `SetStyle` changes the palette in place and preserves the selected destination. Pointer and
  keyboard activation select the same destination and emit the item's semantic command through the
  normal UI intent sink. The Avalonia style button is an application-level presentation toggle,
  while minimize/maximize/close remain native window actions.
- `PCL.UI.Next` remains backend-free. `PCL.UI.Next.Backend.Avalonia` is the only project that
  references `Avalonia.Desktop`; its `XsrTitleBarControl` and `XsrPrimaryNavigationControl` own
  the native window chrome, client-area drag behavior, transparency hint, text controls, and
  automation names. `PCL.Desktop` only composes Foundation/Minecraft, compiles the PXML shell over
  the existing host state store, and starts the backend host.
- The title bar is 58 logical pixels and the navigation rail is 236 logical pixels in the
  canonical UI.Next layout. The final child of the root/body stack stretches into remaining
  viewport space so the content host is never dependent on caller-provided filler controls.
- Desktop starts in `Experimental` by default; `--ui-style=liquid-glass` (or
  `--liquid-glass`) selects the alternate presentation. `--validate-shell` compiles the embedded
  PXML template, renders its semantic scene, and exits without opening a native window for CI and
  smoke checks.

## Interaction and accessibility

Every navigation item is focusable and clickable, has a visible text label alongside its icon, and
maps to a native button with an automation name. The title bar exposes a drag surface and labelled
window actions. Selection is separate from focus/pressed state and is copied into the immutable
scene, so a future backend or PXML slice can bind to the same state without observing CLR events.

## Non-goals

This unit does not migrate product pages, route navigation to Foundation/Minecraft commands, or
define the Plugin UI IR. It does not copy legacy Avalonia views. Those vertical slices will attach
to the existing `Content` entity through PXML/UI.Next in subsequent Wave 7 units.

## Verification

`PCL.UI.Next.Tests` covers both palettes, canonical title/navigation/content geometry, selected
state persistence across style changes, pointer activation, intent emission, and unknown-route
rejection. The Avalonia backend and Desktop composition build against `Avalonia.Desktop` 12.1.0;
the architecture gate continues to allow that package only at the backend boundary.
