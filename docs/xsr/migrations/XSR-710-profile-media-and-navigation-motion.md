# XSR-710 Profile media, navigation motion and account actions

## Scope locked before implementation

Follow the experimental launch layout with Apple-style restrained motion and capsule controls.
Resolve actual profile skin avatars, avoid clipped title descenders, animate page/account/title
navigation, and provide paired switch-profile/wardrobe capsule actions. Wardrobe gets its own
product page and title-bar back route; editing/upload UI is a separate vertical slice.

- Services resolve explicit skin URLs or UUID/session-server metadata without credentials.
  Bounded public PNG data is fetched asynchronously through injected HTTP, with safe scheme,
  response-size and pixel-dimension validation. Missing/invalid media retains embedded default
  avatars. Never perform network or filesystem I/O in render/paint, or mutate the tree from a
  completion thread. Profile changes must not display another profile's delayed response.
- Media state belongs to the shared Host State. Desktop projects opaque resource references;
  backend owns bounded bitmap decoding/cache and face/hat pixel-art drawing. No native account
  layout and no backend knowledge of account services.
- Navigation presentations are reusable scene contracts, not backend checks for product entity
  names. PXML transition keys identify content changes through state; page/title changes also
  animate when a destination reuses a page entity. Controls have independent, interruptible
  motion; page/card surfaces stay stationary, without group choreography. Reduced motion snaps cleanly.
- Text intrinsic line boxes scale with declared font size; explicit title heights reserve enough
  space for CJK and Latin descenders. Native drawing remains clipped to its intended content.
- Capsules share renderer-owned geometry and the existing motion clock. At rest their actual
  size is icon-only; hover/keyboard expansion must not reflow the row, push adjacent controls
  out of bounds, or create a separate paint-only hit region.

## Acceptance

### Motion rework after interaction review

Apple-design motion grammar, updated to the user's no-grouping preference: critical springs
retain position/velocity on interruption; capsules retain hover ownership under geometry-only
pointer events and use unrounded scene coordinates. Title text and back controls have separate
presentations. Page/account leaves enter independently; never translate an entire page or card.
Keep the original per-control stagger: visible leaves receive scene-owned sequence ordinals;
the backend starts their short rise/reveal springs 14 ms apart, capped at 224 ms. Direct title,
hover and press feedback never wait for that sequence. Retargeting a live spring adds no delay.
Individual labels can retain a small outgoing snapshot to make replacement continuous.
The outgoing layer is bounded, non-interactive and absent from accessibility; it contains
immutable scene facts, never live product controls or credentials. Renderer owns all offsets
and hit rectangles; backend only clocks and paints them. Reduced motion discards outgoing
layers and settles geometry. Preserve rail, pager, caption, startup and close controllers.

### Follow-up scope

- Remove legacy import from the add-account menu (retain the explicit migration capability).
- Replace profile-row checkmarks with named delete actions through the Foundation account
  router. Selection remains visible through row styling; deleting a row must not also select it.
- Title-bar navigation uses a horizontal, interruptible transition with scene-owned geometry,
  not a fade or paint-only offset with stale hit regions. Caption controls remain stationary.
- Rotate the trivia content every three seconds through Host State, with cancellation on
  controller disposal. Add a third pager card, Echo Cave, with the specified placeholder copy.
  Generalize pager indicators to three pages while retaining real scene hit geometry.

Test explicit and UUID-derived skin sources, invalid/failed/oversized images and stale completion;
verify face/hat crop and bounded resource lifetime. Check minimum layout and both shell styles,
title line boxes, capsule geometry, wardrobe/back route. Exercise native page/account/title
animations including rapid reversal and reduced motion. Run full CoreCLR, NativeAOT Desktop,
trim, architecture, formatting and benchmark gates; distinguish fixture evidence from manual UI.

### Recovery trail and verified result (2026-09-04)

- `065b6c6d` introduced the original 10 px rise with a 14 ms per-control delay.
  `07e15eba` removed the rise, changed entry to fade/scale and omitted the delay constant;
  `ed52b527` restored that constant, not the original rise. This iteration restores independent
  staggered spatial entry through renderer geometry, without moving page/card containers.
- Capsule regression exercises stationary-pointer layout changes and fractional native arrange;
  title regression checks independent back/text geometry, live hit testing, rapid retargeting,
  bounded non-interactive outgoing snapshots and reduced-motion cleanup. Visible entry ordinals
  and the native delayed spring are covered separately. Pager indicators now select an absolute
  page, so selecting Echo Cave from the first card cannot accidentally stop on the second card.
- Release build: zero warnings/errors. CoreCLR suites passed: Runtime 89, UI.Next 69, PXML 37,
  Services 185, Sidecar 19, Desktop 38, plus all six native-backend scenarios and their nested
  input/animation/media/lifetime checks. Timing-sensitive native/runtime cases passed when run
  without a competing AOT compiler; do not treat overloaded wall-clock tests as deterministic.
- Architecture: 29 projects. Benchmark: clean renders allocate zero bytes, paint-only changes
  visit zero layout nodes, and the 931-node scene preserves deterministic tree order.
  Source formatting (`IDE0055`, `IMPORTS`) passed.
- Windows x64 Desktop NativeAOT and trimmed/link publish both passed, followed by
  `--validate-shell` for Experimental and LiquidGlass (56 semantic nodes each). The local AOT
  compiler used workstation GC and a two-processor cap to bound resource use. An interrupted
  trim output contained invalid native files; validation used a fresh build/output directory.
- Real Skia headless snapshots checked the 850x500 launch layout, expanded capsules, title
  transitions, profile picker and both styles. This is automated scene/native-render evidence,
  not a claim of manual Narrator, live OAuth, or interactive Windows preview acceptance.
