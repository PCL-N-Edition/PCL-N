# XSR migration map

## Repository isolation

The migration branch is `refactor/xsr`. Its working copy must be a dedicated Git worktree outside the active `dev` checkout. `dev` remains the legacy maintenance line; XSR work must not be created as a subdirectory of that checkout.

The migration branch itself contains no legacy source, project, solution, implementation submodule, installer, native bootstrap, or release tooling. Reference work is performed against the separate `dev` checkout as described in [source-reference.md](source-reference.md).

There is no periodic `dev -> refactor/xsr` merge. A legacy fix is forward-ported by meaning:

- security fixes are re-evaluated and carried forward;
- algorithm fixes are reimplemented against XSR contracts;
- behavior fixes are represented by parity tests and then implemented;
- irrelevant legacy-only changes are not imported.

## Waves

| Wave | Outcome | Exit evidence |
|---|---|---|
| 0 Architecture lock | normative documents, version policy, clean-slate source boundary | architecture review and zero legacy source inventory |
| 1 X kernel | registry, command/query routers, state store/graph, events, scopes, scheduling, diagnostics | no Avalonia/Minecraft/plugin refs; AOT/trim pass |
| 2 Renderer kernel | stable UI.Next ECS, scene, layout, input, navigation, overlay, accessibility | deterministic contract and benchmark gates |
| 3 PXML | parser through generated UI.Next IR and runtime loader | no runtime reflection binding |
| 4 Sidecar Fabric v2 | registration, command/query, state mirror, event, UI/resources, recovery | protocol, crash, reconnect, and performance gates |
| 5 Foundation services | Settings through Account/Update | capability parity and data compatibility — complete (XSR-501…520, see below) |
| 6 Minecraft core | discovery, instances, Java, assets, libraries, launch, process, crash analysis | canonical corpus parity — complete (XSR-601…606, XSR-608…609) |
| 7 Product UI | product vertical slices rendered through PXML/UI.Next | UX and accessibility parity |
| 8 Plugin SDK 1.0 | stable API, package, manifest, UI IR, permissions, testing, analyzers | compatibility baseline and validation plugins |
| 9 Plugin ecosystem | runtime, internal plugins, IDE, market, legacy adapter | real plugin migration evidence |
| 10 Cutover | XSR becomes `dev`; legacy deletion begins | complete product and compatibility gate |

## Wave 5 status (complete)

Foundation services are composed over one shared host state store with formal command/query
routers sealed in `PCL.Services.Composition`, and the update loop runs end to end
(discovery → eligibility → plan → download → verify → stage → install → restart):

| Unit | Commit | Outcome |
|---|---|---|
| XSR-501 | `8aca8559` | settings capability (schema, durable-first writes, stable errors) |
| XSR-502 | `8b92b2f6` | logging capability (bounded redacted ring as ordered state) |
| XSR-503 | `4b6abed2` | launcher settings JSON compatibility (103 legacy keys, quarantine) |
| XSR-504/505 | `ac3e896a`/`f18e7ce2` | downloads: failover, resume, segmented parallel |
| XSR-506 | `7f7dca4b` | account roster with credential-free published views |
| XSR-507 | `2b587037` | update block data contracts (FastCDC, gzip/zstd, local index) |
| XSR-508 | `b3788b4c` | one-way update eligibility (1.4.x → 2.0.0, downgrades never) |
| XSR-509 | `e53d9e67` | update package planning (variant, patch path, patch-vs-full) |
| XSR-510 | `69ba2ef0` | update discovery/transport (index fetch, hop walk, HEAD probe) |
| XSR-511 | `d93d127d` | signature/delta codecs (pinned-key GPG, RFC 3284 VCDIFF) |
| XSR-512 | `9b5b03e7` | staged install core (verify/flatten/plan/apply) |
| XSR-513 | `6cd574ee` | online account flows (Microsoft device chain, Yggdrasil) |
| XSR-514 | `93de2c86` | LittleSkin OAuth and appearance services |
| XSR-515 | `c109a475` | file capability (canonical folders, safe atomic port) |
| XSR-516 | `a200d492`+`378f8d51` | payload extraction (zip/tar) and HDiffPatch orchestration |
| XSR-517 | `a273efb7` | network probing and opt-in telemetry |
| XSR-518 | `8a1100e5`+`e0deb40a` | helper hand-off and restart scheduling |
| XSR-519 | `98e78477` | unified host state composition, foundation handler contracts, cross-capability PXML integration test, NativeAOT CI evidence |
| XSR-520 | `a33184f1` + this commit | raw typed settings, formal Foundation Runtime composition, and unified download logging |

Exit evidence: `tests/PCL.Services.Tests` (130 executable tests) green under CoreCLR and
NativeAOT in CI; architecture gate green including the Desktop trim gate over the composed
Foundation Runtime (the trimmed binary composes five services, three command routes, and one
query route over one host state store and runs).

## Wave 6 status (complete)

Minecraft core is now represented as portable Services contracts and a composition edge; no
legacy ViewModel or UI dependency is carried into the branch:

| Unit | Commit | Outcome |
|---|---|---|
| XSR-601 | `6a61b7b4` | version classification, safe local discovery, and instance metadata persistence |
| XSR-602 | `806ecbde` | Java selection, asset/index resolution, and library/classpath contracts |
| XSR-603 | `2d6f74f8` | ModLoader detection, launch planning, process lifecycle, and crash analysis |
| XSR-604 | `5f3bed3b` | formal Minecraft command/query router composition |
| XSR-605 | `4add2898` + `fd8d6fc2` | runtime/client/asset acquisition, ARM64 parity, instance discovery, and final evidence |
| XSR-606 | `fcaf7fa6` + `10d72a09` + `c566bc3f` + `1ee58542` + `d70ddff2` | acceptance hardening: Minecraft/Java version domains and conflicts, full token coverage with strict unknown-token rejection, automatic inherited client-JAR and `jar`-alias resolution, ARM64 natives, launch-integrated extraction, shared process state and cancel route, common Mojang rules, safe download paths, and the expanded corpus |
| XSR-608 | `1dba7f82` | Java policy closure: Legacy/Calendar version schemes, manifest-first Java selection, the 1.16.5/1.17/1.18/1.20.5/26.1 matrix, corrected corpus goldens, and Java 7/8 selection regressions |
| XSR-609 | `4ce6eb69` + `220a5e74` | preserve Mojang ordinary library artifacts alongside native classifiers; keep classpath JARs and native extraction tokens independent, including system-GLFW filtering |

Exit evidence: `tests/PCL.Services.Tests` passes 173 executable tests under CoreCLR and
NativeAOT; Runtime, UI.Next, PXML, and Sidecar CoreCLR/AOT gates pass; the UI.Next benchmark
gate and 27-project architecture gate pass; Desktop trim publish succeeds; and `dotnet format`
reports no changes.

## Wave 7 status (in progress)

Wave 7 begins with the shared product shell. The semantic title bar, primary navigation rail, and
content host are composed in UI.Next and presented by an Avalonia edge in two switchable styles:
the current Experimental baseline and an Apple-inspired LiquidGlass treatment. Product pages and
PXML vertical slices remain subsequent units.

| Unit | Commit | Outcome |
|---|---|---|
| XSR-701 | `b9a5bc54` + `6992acd8` | shared shell contract, deterministic chrome layout, Experimental/LiquidGlass palettes, PXML shell template, Avalonia custom controls, and Desktop composition |

## Closed migration unit

Every task has an ID and a narrow, testable outcome:

```text
Inspect legacy behavior
  -> map dependencies and data ownership
  -> write migration note
  -> implement against locked contracts
  -> parity/contract tests
  -> AOT and trim analysis
  -> architecture gate
  -> benchmark when the path is performance-sensitive
  -> commit
```

A task does not mix architecture migration with user-data format migration unless the data change is the explicit task. It does not introduce a temporary legacy dependency without a named compatibility adapter and removal gate.

## Wave 0 work items

| ID | Work item | Status |
|---|---|---|
| XSR-000 | create dedicated `refactor/xsr` worktree | complete |
| XSR-001 | lock architecture, state, service, renderer, Sidecar, and version documents | complete |
| XSR-002 | add project/package dependency fitness tests and migration-branch CI with the first source project | complete |
| XSR-003 | add source analyzers for forbidden namespaces, sync-over-async, reflection dispatch, and unstable plugin API use | pending |
| XSR-004 | inventory legacy behaviors, data formats, and parity corpora by capability | pending |
| XSR-005 | remove all legacy source, projects, submodules, and build/release implementation from the migration branch | complete |

The first Wave 1 implementation unit must complete XSR-002. XSR-003 may grow incrementally with compilable surfaces, but bypassing the architecture gate is not allowed. Empty placeholder projects are not evidence of completion.

## Wave 1 work items

| ID | Work item | Status |
|---|---|---|
| XSR-101 | semantic/runtime identifiers and deterministic sealed registry | complete |
| XSR-102 | asynchronous command and query routing with cancellation and stable errors | complete |
| XSR-103 | revisioned state store, snapshots, deltas, and derived dependency graph | complete |
| XSR-104 | ordered events, scopes, and bounded delivery | complete |
| XSR-105 | scheduling, lifecycle, and end-to-end diagnostics | complete |
| XSR-106 | runtime lifetime scopes for plugin, window, and session cleanup | complete |

Wave 1 is complete. The X kernel provides the sealed registry, command/query routers, revisioned state with a derived dependency graph, ordered event scopes with bounded delivery, scheduling, lifecycle, and session diagnostics. Exit evidence: every kernel project is AOT-compatible, the NativeAOT runtime-test publish and trimmed Desktop publish pass, and the architecture gate enforces that no kernel project references Avalonia, Minecraft, plugin, or renderer code.

## Wave 2 work items

| ID | Work item | Status |
|---|---|---|
| XSR-201 | UI.Next entity kernel: tree, components, dirty tracking, state bridge | complete |
| XSR-202 | deterministic layout and immutable scene production | complete |
| XSR-203 | input routing, focus, navigation, overlays, accessibility semantics | complete |
| XSR-204 | deterministic benchmark gates and renderer CI | complete |
| XSR-205 | animation kernel primitive, generational handles, thread-safe state bridge, multi-property bindings | complete |
| XSR-206 | renderer kernel completion: easing, keyframes, scroll, media slot | complete |

Wave 2 is complete. The renderer kernel provides the entity tree, deterministic layout with dirty-subtree relayout, the immutable scene boundary, input and focus routing, navigation and overlays, and accessibility semantics. Exit evidence: the UI.Next test suite and benchmark gates run in CI including NativeAOT publishes, and the architecture gate enforces that UI.Next references no backend, service, or runtime assembly. XSR-205 closed the animation scope gap with the kernel primitive (easing and keyframes stay later units) and folded review hardening into the contracts: generational entity handles, separated measure/arrange caching with slot-correct siblings, a thread-safe state bridge drained at frame start, applied-value reads over derived and coalesced state, and multi-property state bindings.

## Wave 3 work items

| ID | Work item | Status |
|---|---|---|
| XSR-207 | PXML grammar and structural parser | complete |
| XSR-208 | PXML to host-internal IR compilation with the binding table | complete; catalog model superseded by XSR-212, artifact explicitly not the Plugin UI IR v1 ABI |
| XSR-209 | runtime loader with hand-built scene parity | complete |
| XSR-210 | PXML NativeAOT, architecture, and CI acceptance gates | complete |
| XSR-211 | parser security boundary and transactional loader review fixes | complete |
| XSR-212 | required external control catalog and early compile-time expansion | complete |

Wave 3 is complete. The parser enforces a deterministic XML boundary, the compiler consumes a required UI.Next-owned control directory expanded into physical generated source, the typed IR carries finite runtime recipes and binding tables, and the transactional loader produces the same scene as hand-built entities. Exit evidence: default and substitute catalogs compile incrementally, malformed and missing catalogs fail deterministically, text and visibility state bindings drive rendering, Compiler and Runtime contain no reflection or named-control dispatch, and the complete pipeline passes CoreCLR, NativeAOT, architecture, format, benchmark, and trim gates.

## Wave 4 work items

| ID | Work item | Status |
|---|---|---|
| XSR-401 | Sidecar protocol surface: frames, message numbers, payload codec with unknown-field skipping | complete |
| XSR-402 | reliable local transport with connection lifecycle | complete |
| XSR-403 | session lifecycle, registration, state mirror, and local IPC factories | complete |
| XSR-404 | data plane, crash recovery, reconnect, and command/query forwarding | complete |
| XSR-405 | execute by ID, snapshot lifecycle, capability boundary, protocol draft status | complete |
| XSR-406 | transactional snapshot, typed codec registry, UiModule/Resource content cache | complete |

Wave 4 is complete. The Sidecar Fabric provides the versioned binary protocol with unknown-field skipping, the framed transport with an explicit connection lifecycle, the host session over the locked handshake and registration flow, the per-session state mirror the renderer reads with zero IPC, the bounded data plane with correlated results and stable errors, ordered event delivery, and crash/reconnect semantics where a new session replaces the mirror only after a coherent snapshot. XSR-405 hardened the wave per review: the data plane executes by session-local contract ID (semantic strings never cross the wire hot path), READY is a real wire message emitted only after the pre-activation snapshot commits, the registration table is an enforced capability boundary with local rejection at zero wire bytes, CANCEL reaches the sidecar, DEACTIVATE returns to Ready, and the protocol is explicitly 1.0-draft pending the Plugin SDK freeze. Exit evidence: the protocol, transport, session, and data-plane suites pass under CoreCLR and NativeAOT, the architecture gate carries the Sidecar project graph with reflection tokens forbidden in Compiler and Runtime, and CI runs the full sequence on every push.

## Cutover gate

Before XSR replaces the legacy architecture, startup, downloads, instances, Minecraft launch, accounts, settings, updates, cloud, online play, plugins, OOBE, crash recovery, AOT, and trim must pass. Plugin SDK 1.0, Sidecar Protocol v1, Manifest/Package v1, and Plugin UI IR v1 must be frozen with compatibility tests.
