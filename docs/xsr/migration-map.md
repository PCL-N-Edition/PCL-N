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
| 5 Foundation services | Settings through Account/Update | capability parity and data compatibility |
| 6 Minecraft core | discovery, instances, Java, assets, libraries, launch, crash analysis | canonical corpus parity |
| 7 Product UI | product vertical slices rendered through PXML/UI.Next | UX and accessibility parity |
| 8 Plugin SDK 1.0 | stable API, package, manifest, UI IR, permissions, testing, analyzers | compatibility baseline and validation plugins |
| 9 Plugin ecosystem | runtime, internal plugins, IDE, market, legacy adapter | real plugin migration evidence |
| 10 Cutover | XSR becomes `dev`; legacy deletion begins | complete product and compatibility gate |

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

## Cutover gate

Before XSR replaces the legacy architecture, startup, downloads, instances, Minecraft launch, accounts, settings, updates, cloud, online play, plugins, OOBE, crash recovery, AOT, and trim must pass. Plugin SDK 1.0, Sidecar Protocol v1, Manifest/Package v1, and Plugin UI IR v1 must be frozen with compatibility tests.
