# XSR-205 renderer animation kernel and review fixes

## Outcome

Wave 2's locked scope included animation; this unit closes that gap with the animation kernel primitive and folds the review fixes into the renderer contracts: generational entity handles, separated measure/arrange caching, the thread-safe state bridge, applied-value reads, and multi-property state bindings.

## Locked contract

- Animation: `XsrUiAnimation` is a component carrying duration and a progress value written by the animator; `XsrUiAnimator` advances every active animation per `Tick` on the render thread, marks animating entities paint-dirty, and completes and deregisters them. Progress is carried on the scene node. Reduced motion completes animations immediately instead of advancing them. Easing, keyframes, and transitions remain later units.
- Entity handles are generational (`Index`, `Generation`): destroy advances the slot's generation, so a stale handle — including handles held by the navigator's back stack, overlay lists, focus, hover, and press state — can never silently resolve to a recycled entity. Stale handles are dead; renderer input paths clear stale state instead of throwing.
- Layout caching is split: measure results are cached per subtree and re-aggregated only through layout-relevant dirt; arrange results are cached per input slot, and any entity whose slot moved re-arranges even when it is clean — a growing sibling shifts later siblings' coordinates, and the renderer must produce them. `LastLayoutVisits` counts measurements, not arrangements.
- The state bridge crosses the thread model: state publishers run on arbitrary threads and only enqueue changed state IDs into the bridge (duplicates coalesce); the render thread drains the queue at frame start inside `Render()`, resolves every affected entry through the store's derived-dependency index (`AffectedBy`), and marks bound entities per binding record. The bridge never touches the tree from a publisher thread.
- Applied-value reads: `ReadAppliedValue` is the boxed read for consumers without a value contract. Cells flush deferred coalesced publications; derived entries recompute through their watermark logic; coalesced publications notify with a pending hint (`CoalescedPublished`) so queued bridges see them before the value is applied.
- State bindings are records of `(State, Property, DirtyKinds)` and an entity carries as many as it needs — text, visibility, and enabled state can bind to different entries on one entity. `XsrUiText.BoundState` and `XsrUiStateBinding` maintain their own records; this table is the runtime form of the PXML binding table.

## Non-goals

Easing curves, keyframes, transitions, scroll, gestures, IME, and platform accessibility bridges remain later units; the PXML compiler (Wave 3) consumes the binding table as its runtime target.

## Verification

`PCL.UI.Next.Tests` covers generational stale-handle rejection across recycling, multi-property binding coexistence and unbinding, sibling coordinates following slot changes (grow and shrink), bounded measure visits, the state bridge queue with coalescing and frame-start drain, derived state driving bound text, coalesced state becoming visible without manual flush, animator progress and completion, and reduced-motion completion. `PCL.Xsr.Runtime.Tests` covers the coalesced-pending notification. All suites pass under NativeAOT; the benchmark gates assert slot correctness in addition to allocation and visit bounds.
