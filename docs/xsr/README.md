# XSR architecture lock

This directory is the normative architecture baseline for the XSR migration. The three original migration guides remain design inputs; the documents here record the decisions that are enforced in this repository.

The user-requested constraints take precedence:

- migration work happens on `refactor/xsr` in a dedicated worktree outside the `dev` checkout;
- the XSR product line starts at `2.0.0` and uses the dotted version forms in [versioning.md](versioning.md);
- the branch contains no legacy source or project graph; the new XSR graph is built independently while `dev` is consulted read-only;
- Wave 0 locks architecture and migration policy only; it does not migrate product behavior.

## Documents

- [architecture.md](architecture.md) — system direction and project boundaries
- [dependency-rules.md](dependency-rules.md) — allowed dependency graph and CI enforcement
- [state-model.md](state-model.md) — state ownership, snapshots, deltas, and derived state
- [service-model.md](service-model.md) — service responsibilities and communication primitives
- [renderer-model.md](renderer-model.md) — UI.Next and backend boundaries
- [sidecar-protocol.md](sidecar-protocol.md) — Sidecar Fabric control/data planes
- [versioning.md](versioning.md) — XSR product-version grammar and compatibility surfaces
- [migration-map.md](migration-map.md) — waves, closed work units, and cutover gates
- [source-reference.md](source-reference.md) — clean-slate rules for consulting legacy code
- [migrations/XSR-002-project-graph.md](migrations/XSR-002-project-graph.md) — initial solution graph and architecture gate
- [migrations/XSR-101-identifiers-and-registry.md](migrations/XSR-101-identifiers-and-registry.md) — Wave 1 identity and sealed registry contract
- [migrations/XSR-102-command-query-routing.md](migrations/XSR-102-command-query-routing.md) — asynchronous command/query routing and stable errors
- [migrations/XSR-103-revisioned-state.md](migrations/XSR-103-revisioned-state.md) — revisioned cells, collections, snapshots, deltas, and derived state
- [migrations/XSR-104-ordered-events.md](migrations/XSR-104-ordered-events.md) — ordered event scopes and bounded delivery with backpressure
- [migrations/XSR-105-scheduling-lifecycle-diagnostics.md](migrations/XSR-105-scheduling-lifecycle-diagnostics.md) — scheduling, lifecycle state machines, and session traces
- [migrations/XSR-106-lifetime-scopes.md](migrations/XSR-106-lifetime-scopes.md) — runtime lifetime scopes for atomic resource cleanup
- [migrations/XSR-201-ui-entity-kernel.md](migrations/XSR-201-ui-entity-kernel.md) — renderer entity tree, components, dirty tracking, state bridge
- [migrations/XSR-202-layout-and-scene.md](migrations/XSR-202-layout-and-scene.md) — deterministic layout and the immutable render scene
- [migrations/XSR-203-input-navigation-overlay.md](migrations/XSR-203-input-navigation-overlay.md) — pointer and keyboard input, navigation, overlays, accessibility
- [migrations/XSR-204-renderer-gates.md](migrations/XSR-204-renderer-gates.md) — deterministic benchmark gates and renderer CI
- [migrations/XSR-205-animation-and-review-fixes.md](migrations/XSR-205-animation-and-review-fixes.md) — animation kernel, generational handles, thread-safe state bridge
- [migrations/XSR-206-renderer-completion.md](migrations/XSR-206-renderer-completion.md) — easing, keyframes, scroll, and the media slot
- [migrations/XSR-207-pxml-parser.md](migrations/XSR-207-pxml-parser.md) — PXML grammar and the structural parser
- [migrations/XSR-208-pxml-ir-compiler.md](migrations/XSR-208-pxml-ir-compiler.md) — PXML compilation to the typed UI.Next IR
- [migrations/XSR-209-pxml-loader.md](migrations/XSR-209-pxml-loader.md) — runtime loader with hand-built parity
- [migrations/XSR-210-pxml-gates.md](migrations/XSR-210-pxml-gates.md) — PXML NativeAOT, generated-catalog, and Wave 3 acceptance gates
- [migrations/XSR-211-pxml-review-hardening.md](migrations/XSR-211-pxml-review-hardening.md) — parser boundary and transactional loader review fixes
- [migrations/XSR-212-generated-control-catalog.md](migrations/XSR-212-generated-control-catalog.md) — required UI.Next control directory and early generated compiler catalog
- [migrations/XSR-401-sidecar-protocol.md](migrations/XSR-401-sidecar-protocol.md) — Sidecar frames, message numbers, and the TLV payload codec
- [migrations/XSR-402-sidecar-transport.md](migrations/XSR-402-sidecar-transport.md) — frame transport and the connection lifecycle
- [migrations/XSR-403-sidecar-session.md](migrations/XSR-403-sidecar-session.md) — host session lifecycle, registration, and the state mirror
- [migrations/XSR-404-data-plane-and-reconnect.md](migrations/XSR-404-data-plane-and-reconnect.md) — data plane, bounded exchanges, crash recovery, and reconnect
- [migrations/XSR-405-execute-by-id.md](migrations/XSR-405-execute-by-id.md) — session-local contract IDs, snapshot lifecycle, capability boundary, protocol draft

## Decision process

Any change to a locked boundary requires:

1. a concrete motivating use case;
2. an update to the affected document;
3. an architecture-test or analyzer update;
4. compatibility and migration impact notes;
5. review before implementation depends on the new boundary.

Public API baselines describe the accepted surface; changing a baseline never turns a breaking change into a compatible one.
