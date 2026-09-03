# XSR-202 deterministic layout and scene production

## Outcome

Wave 2 adds the layout engine and the render-scene boundary of `PCL.UI.Next`: a deterministic measure/arrange pass that touches only dirty subtrees, and an immutable ordered scene that is the single input to any backend commit.

## Locked contract

- Layout is two-pass (measure, arrange) over the entity tree with a closed layout model: explicit and minimum/maximum content sizes, padding, margin, stretch/start/center/end alignment, and a stack panel with fixed spacing along one direction. A positive element weight shares the stack's remaining main-axis space with weighted siblings, using deterministic star sizing and min/max redistribution. Entities without a stack component overlap children in the full content rect in attach order. Text sizing is a text-shaping concern and stays out of the kernel: text without explicit sizes fills its arranged rect.
- Explicit sizes constrain the content box and disable stretching on their axis; padding adds on top of explicit sizes, while child margins participate in the parent's desired size. Every rule is deterministic: fixed traversal order, fixed arithmetic, no platform measurement.
- Layout is incremental and bounded: desired sizes and paint rects persist across passes, fully clean subtrees reuse the previous pass's measurements, and a dirty descendant re-aggregates only its ancestor chain. `LastLayoutVisits` counts the entities measured per pass, so dirty propagation is observable. Dead entities are pruned from the caches when structure dirt is processed.
- The render scene is an immutable snapshot: nodes in depth-first pre-order (later nodes draw above earlier ones), each carrying the entity handle, paint rect, tree depth, semantic role and label, and text. Text bound to a state entry renders the applied value through the store's boxed read with invariant formatting. Invisible entities are excluded from scene and layout.
- A clean tree returns the cached scene with an unchanged version; any applied change that dirties the root subtree rebuilds the scene and advances the version. Rendering clears the dirty flags it consumed.
- `Render()` is synchronous, allocation-bounded per frame, and performs no I/O, no blocking wait, and no service lookup.

## Non-goals

This unit does not introduce text shaping, images, clipping, transforms, animation, scrolling, hit testing, or any backend contract. XSR-203 layers input, navigation, and overlay on the produced scene.

## Verification

`PCL.UI.Next.Tests` covers exact fixed rects, vertical and horizontal stack flow with spacing, weighted star distribution with min/max redistribution, padding and margin composition (including nested desired sizes), alignment and maximum-size constraints, invisible exclusion from scene and layout, clean-render scene reuse, dirty-subtree-only relayout observed through `LastLayoutVisits`, state-bound text reflecting applied values across renders, deterministic depth-first scene order with roles and labels, missing-root rejection, and viewport-change relayout. The architecture gate enforces the UI.Next dependency boundary.
