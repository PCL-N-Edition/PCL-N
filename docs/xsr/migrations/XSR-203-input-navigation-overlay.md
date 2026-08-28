# XSR-203 input, navigation, overlay, and accessibility semantics

## Outcome

Wave 2 completes the renderer kernel's interaction layer: pointer routing with hit testing against the produced scene, keyboard focus and activation, a page navigator, overlay layers, and the accessibility semantics carried on every scene node.

## Locked contract

- Hit testing reads the last produced scene in reverse draw order, so the top-most entity wins and hit testing never triggers layout or state reads. A point over the background resolves to the background entity; misses are the caller's filter.
- Pointer routing is renderer-local ephemeral state (hover, pressed) exactly as the renderer model allows: pressed-and-released over the same clickable entity activates its command binding and emits one intent through `IXsrUiIntentSink` with a renderer-produced correlation ID. Presses on non-clickable entities are unhandled; releasing outside the pressed entity never activates.
- Focus follows scene (tab) order and wraps. Focused entities carry `IsFocused` on their input component, and the scene node exposes it so backends can draw visible focus. Enter and Space activate the focused entity. Reduced motion is a renderer-level presentation flag that backends and animation drivers must honor.
- The navigator owns one host entity and a page stack: the current page is attached under the host, back-stack pages stay alive but detached, and push/pop/replace are deterministic tree operations reported through an observer. Pushing the current page, pushing dead pages, and popping the last page are rejected.
- The stage composes one surface: a root whose children are the content host and overlay layers in attach order, so overlays always draw above the page without a second draw system. Show, dismiss, and dismiss-top are the only overlay operations.
- Accessibility semantics ride the scene: role, label, text, and focus state are on every node. Backends map them to native accessibility bridges; UI.Next references no backend.

## Non-goals

This unit does not introduce text input/IME routing, gestures, scroll, drag and drop, animation, transitions between pages, or platform accessibility bridges. IME and native bridges belong to backends; gestures and transitions are later kernel units when a concrete vertical slice needs them.

## Verification

`PCL.UI.Next.Tests` covers top-most hit testing including background resolution, pointer activation emitting a bound command intent, unhandled presses, release-outside non-activation, hover tracking with clearing, focus cycling with wrap and non-focusable rejection, keyboard activation, focus visibility in the scene, navigator push/pop/replace with events, navigator rejections, stage overlay draw order, overlay dismissal, stage navigation content swaps, and the reduced-motion flag. The NativeAOT publish of the test project runs the full suite, and the architecture gate enforces the UI.Next dependency boundary.
