# XSR-206 renderer kernel completion

## Outcome

Wave 2's locked renderer scope had deferred pieces the PXML wave consumes: easing and keyframes on the animation kernel, scroll, and the media slot. This unit completes them.

## Locked contract

- Easing: `XsrUiEasing` is a pure delegate with deterministic built-ins (linear, ease-in/out/in-out quad). The animator applies easing to raw progress before keyframe evaluation; the scene node carries both the raw progress and the computed value.
- Keyframes: an animation may declare a value track of `(progress, value)` pairs held in ascending order. Between keyframes the animator interpolates linearly over the eased progress; outside the track the boundary values hold. The computed value is written back to the component and carried on the scene node; animations without a track report no value.
- Scroll: `XsrUiScroll` carries renderer-local offsets on a stacking container. Arrange clamps the offsets to the measured stack content extent (children sum, independent of explicit element sizes) and shifts child slots by the offset, so hit testing follows scrolled content without a second code path. `PointerScroll` routes a wheel delta to the nearest scroll container under the point, walking up the hierarchy. Visual clipping stays a backend concern; the kernel produces correct offsets and rects.
- Media: `XsrUiImage` carries a source reference only. Decoding and drawing belong to backends; the scene node carries the source and the semantic image role.

## Non-goals

Easing splines beyond the quad family, spring physics, page transitions (composition-level: the navigator observer starts animations), pinch gestures, and text shaping remain later units.

## Verification

`PCL.UI.Next.Tests` covers deterministic easing values, eased keyframe evaluation, boundary holds, scroll offsets with clamping to content extent, scroll hit testing following offsets, wheel routing, and image sources on the scene. The full suite passes locally; the benchmark and architecture gates are unchanged.
