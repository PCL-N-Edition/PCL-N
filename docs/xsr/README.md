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
- [migrations/XSR-406-transactional-snapshot-typed-codecs.md](migrations/XSR-406-transactional-snapshot-typed-codecs.md) — transactional snapshots, typed codec registry, content-addressed host cache
- [migrations/XSR-501-settings-capability.md](migrations/XSR-501-settings-capability.md) — Wave 5 settings capability: schema, durable-first writes, stable errors, legacy file compatibility
- [migrations/XSR-502-logging-capability.md](migrations/XSR-502-logging-capability.md) — Wave 5 logging capability: bounded redacted ring as ordered state, level gate, no static sink
- [migrations/XSR-503-launcher-settings-compatibility.md](migrations/XSR-503-launcher-settings-compatibility.md) — launcher settings JSON compatibility: full legacy key universe, quarantine recovery, atomic saves
- [migrations/XSR-504-download-capability.md](migrations/XSR-504-download-capability.md) — download capability: failover with resume, per-destination coalescing, active transfers as state
- [migrations/XSR-505-segmented-download.md](migrations/XSR-505-segmented-download.md) — segmented parallel download: range planning, part-file assembly, fallback for non-segmented sources
- [migrations/XSR-506-account-capability.md](migrations/XSR-506-account-capability.md) — account capability: legacy launch profile file compatibility with credential-free state views
- [migrations/XSR-507-update-block-contracts.md](migrations/XSR-507-update-block-contracts.md) — update block data contracts: FastCDC chunking, gzip/zstd block codecs, local block index
- [migrations/XSR-508-update-eligibility.md](migrations/XSR-508-update-eligibility.md) — one-way upgrade gate: legacy 1.4.x crosses into 2.0.0, downgrades never offered
- [migrations/XSR-509-update-package-planning.md](migrations/XSR-509-update-package-planning.md) — update package planning: variant selection, cheapest patch path, patch-versus-full by size
- [migrations/XSR-510-update-discovery-transport.md](migrations/XSR-510-update-discovery-transport.md) — update discovery and transport: index fetch, multi-tag walk, HEAD probe, eligibility gate
- [migrations/XSR-511-update-signing-delta-codecs.md](migrations/XSR-511-update-signing-delta-codecs.md) — update signature and delta codecs: pinned-key GPG verification, RFC 3284 VCDIFF decoder
- [migrations/XSR-512-staged-install-core.md](migrations/XSR-512-staged-install-core.md) — staged install core: verify/flatten/plan/apply with safe paths, re-verification, and managed deletes
- [migrations/XSR-513-online-account-flows.md](migrations/XSR-513-online-account-flows.md) — online account flows: Microsoft device-code chain, Yggdrasil validate/refresh, roster bridge
- [migrations/XSR-514-littleskin-oauth-appearance.md](migrations/XSR-514-littleskin-oauth-appearance.md) — LittleSkin OAuth (device flow, closet, texture upload) and Microsoft skin/cape services
- [migrations/XSR-515-file-capability.md](migrations/XSR-515-file-capability.md) — File capability: canonical data folders and the safe atomic file port
- [migrations/XSR-516-payload-extraction-patch-orchestration.md](migrations/XSR-516-payload-extraction-patch-orchestration.md) — payload extraction (zip/tar) and HDiffPatch orchestration with binary chains and scatter ops
- [migrations/XSR-517-network-telemetry.md](migrations/XSR-517-network-telemetry.md) — Network probing with latency and opt-in Telemetry buffering/flush with a pending state cell
- [migrations/XSR-518-helper-handoff-restart.md](migrations/XSR-518-helper-handoff-restart.md) — helper hand-off and restart scheduling: artifact validation, replacement process contract, launch port
- [migrations/XSR-519-wave5-acceptance-integration.md](migrations/XSR-519-wave5-acceptance-integration.md) — Wave 5 acceptance: unified host state composition, foundation command routing, cross-capability PXML integration
- [migrations/XSR-520-foundation-correctness-closure.md](migrations/XSR-520-foundation-correctness-closure.md) — Wave 5 review closure: raw typed settings, formal Foundation runtime composition, unified download logging
- [migrations/XSR-701-product-shell.md](migrations/XSR-701-product-shell.md) — Wave 7 product shell foundation: shared UI.Next chrome with Experimental and LiquidGlass presentations
- [migrations/XSR-702-pxml-scene-backend.md](migrations/XSR-702-pxml-scene-backend.md) — Wave 7 PXML-to-scene Avalonia backend closure

## Decision process

Any change to a locked boundary requires:

1. a concrete motivating use case;
2. an update to the affected document;
3. an architecture-test or analyzer update;
4. compatibility and migration impact notes;
5. review before implementation depends on the new boundary.

Public API baselines describe the accepted surface; changing a baseline never turns a breaking change into a compatible one.
- [migrations/XSR-606-minecraft-launch-hardening.md](migrations/XSR-606-minecraft-launch-hardening.md) — Minecraft launch hardening: Java conflicts, token coverage, natives extraction, process state
- [migrations/XSR-608-minecraft-java-policy.md](migrations/XSR-608-minecraft-java-policy.md) — Minecraft Java policy closure: version schemes, manifest-first selection, and the historical compatibility matrix
- [migrations/XSR-609-minecraft-library-artifact-native-pair.md](migrations/XSR-609-minecraft-library-artifact-native-pair.md) — Minecraft library artifact/native pairing: preserve ordinary classpath JARs beside native classifiers
