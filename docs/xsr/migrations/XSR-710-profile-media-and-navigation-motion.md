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
  names. PXML transition groups identify content changes through state; page/title changes also
  animate when a destination reuses a page entity. Use coordinated, interruptible motion, no
  per-descendant stagger that separates a card from its contents. Reduced motion snaps cleanly.
- Text intrinsic line boxes scale with declared font size; explicit title heights reserve enough
  space for CJK and Latin descenders. Native drawing remains clipped to its intended content.
- Capsules share renderer-owned geometry and the existing motion clock. At rest their actual
  size is icon-only; hover/keyboard expansion must not reflow the row, push adjacent controls
  out of bounds, or create a separate paint-only hit region.

## Acceptance

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
