# XSR-702 PXML scene-backend closure

## Outcome

The Wave 7 shell now follows the locked renderer pipeline in production:

```text
PXML -> UI.Next IR -> UI.Next tree/runtime -> immutable render scene -> Avalonia commit
```

There is no second Grid/title/navigation/content tree in the backend. A later PXML page attached
to `XsrUiShell.Stage.ContentHost` is therefore the same page that the user sees.

## Locked contract

- `AvaloniaUiSceneSurface` consumes `XsrUiScene` nodes only. It does not inspect UI tree
  components, shell navigation collections, or PXML properties to reconstruct layout. Every
  native node is arranged from the scene rectangle and draws from its scene visual/text facts.
- Native pointer, wheel, and keyboard messages call `XsrUiRenderer`. The backend never selects a
  navigation destination directly. Renderer activation produces the correlation ID and sends the
  semantic command through `IXsrUiIntentSink`; `XsrUiShell` then owns local selection and the
  Desktop composition-edge dispatcher exposes the intent to later explicit typed bindings.
- `XsrUiRenderer.PointerMoved()` reports whether it changed presentation state, rather than whether
  the pointer currently rests on an input. This guarantees that leaving a hovered entity schedules
  a repaint immediately, including when the pointer enters empty content or exits the window.
- The scene carries focusable, clickable, hovered, and pressed facts in addition to the existing
  semantic role, label, focus, selection, text, geometry, and visual facts. Avalonia accessibility
  control type/name mappings and custom automation peers are produced from those scene facts rather
  than hand-maintained navigation metadata. Native accessibility focus and invoke actions route to
  `XsrUiRenderer.Focus()` and `XsrUiRenderer.Activate()`; a navigation selection provider exposes
  scene-selected items and routes selection through the same activation path.
- `XsrUiRuntimeContext` creates one entity tree and its `XsrUiStateBridge` before Foundation
  composition. Desktop passes that bridge to `FoundationComposer.Compose(observer: ...)`, then
  loads the PXML template into the same tree and injects the bridge into the shell renderer.
  Publisher-thread notifications request a backend frame; only `XsrUiRenderer.Render()` drains and
  marks the tree on the render thread. The backend coalesces notifications safely without dropping
  a publication that arrives while a frame is rendering.
- UI.Next provides deterministic intrinsic text metrics before any backend commits the scene.
  Bound and composed text therefore declares `Layout,Paint` dirtiness, so labels have usable
  geometry for both layout and renderer hit testing.
- Native minimize/maximize/close controls are window affordances only. They are the sole backend
  overlay and obtain their presentation tokens from the committed scene, not from a duplicate app
  title/navigation model.
- The checked-in Product Shell is intentionally fixed to its six PXML destinations. Desktop
  rejects `XsrUiShellOptions.NavigationItems`; it no longer advertises a dynamic-navigation
  capability that the template cannot render.

## Verification

`PCL.UI.Next.Tests` proves a context-created bridge observes a host store, requests a frame, and
refreshes a shell-bound scene without manual dirty marking. `PCL.UI.Next.Backend.Avalonia.Tests`
calls the real native automation peers and proves focus, invoke, and navigation selection return to
the renderer rather than a shell shortcut. A NativeAOT Desktop `--validate-shell` smoke path
composes the Foundation host, embedded PXML, UI.Next shell, state bridge, and scene renderer without
opening an Avalonia window. A separate trimmed Desktop publish remains the static composition
analysis. CoreCLR, NativeAOT, architecture, formatting, and trimmed Desktop gates remain required
before closing the unit.
