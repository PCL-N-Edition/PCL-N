# XSR architecture

## Direction

XSR is the long-term architecture of PCL N:

```text
X = execution and exchange runtime
S = business services
R = semantic renderer
```

The implementation is a modular monolith with one deliberate out-of-process boundary for plugin execution. This preserves straightforward deployment and debugging while giving business capabilities explicit ownership. A module may be extracted later only when independent deployment, scaling, or ownership justifies the operational cost.

The legacy system is a source of behavior, algorithms, data formats, protocol knowledge, and verified platform handling. It is not the foundation of the new dependency graph.

## Clean-slate repository rule

`refactor/xsr` contains no legacy source projects, solution graph, native bootstrap, installer, release pipeline, or implementation submodule. The separate `dev` worktree is the reference corpus. XSR code may reproduce verified behavior or algorithms only after ownership and contracts are documented; it must not reference or copy the legacy project graph.

The first project in each family is created only when a closed migration unit needs it. Empty placeholder projects and broad compatibility assemblies are not architecture progress.

## Runtime shape

```text
                         PCL.Desktop
                    composition root only
                               |
                               v
                        PCL.Xsr.Runtime
             +-----------------+-----------------+
             |                 |                 |
             v                 v                 v
       PCL.Services.*     PCL.Xsr.State      PCL.UI.Next
             |                 |                 |
             +-----------------+-----------------+
                               |
                               v
                    Domain / Contracts / Core

        Host process                    Plugin process
  PCL.Xsr.Runtime + UI.Next  <------>  Sidecar Fabric v2
```

X coordinates service registration, command/query routing, state, events, scopes, scheduling, capabilities, Sidecar sessions, and diagnostics. It does not own business rules or presentation truth. Two distinct scope abstractions exist by design: event ordering scopes bound and order event delivery inside the router, while runtime lifetime scopes (`IXsrScope`) form a disposal tree so a plugin, window, Sidecar session, or service group can be torn down atomically.

## Target project families

| Family | Responsibility |
|---|---|
| `PCL.Core`, `PCL.Domain`, `PCL.Contracts` | portable primitives, domain rules, and stable cross-module contracts |
| `PCL.Xsr.*` | runtime abstractions, routing, state, transport, diagnostics, and generated code |
| `PCL.Services.*` | business capabilities grouped by change ownership |
| `PCL.UI.Next` | canonical semantic renderer |
| `PCL.UI.Next.Backend.*` | platform rendering, windows, native input, IME, clipboard, and accessibility bridges |
| `PCL.Pxml.*` | authoring language, compiler, IR, generators, and runtime loading |
| `PCL.Sidecar.*`, `PCL.Plugin.Sidecar` | protocol, transport, and dynamic plugin execution |
| `PCL.Desktop` | process bootstrap and composition root |

`PCL.Sidecar.Protocol` and `PCL.Sidecar.Transport` live in this Host repository. The `PCL.Plugin.Sidecar` executable lives in the independent PCL.Plugin XSR repository and consumes released protocol surfaces; neither repository uses a workstation-relative source reference to the other.

The target list is not a requirement to create an empty project for every name. A project is introduced only when it has a clear owner and a dependency boundary worth enforcing.

PXML-visible control models are owned by UI.Next in an explicitly configured catalog directory, not by `PCL.Pxml.Compiler`. `PCL.Pxml.Generators` consumes that directory as build-time `AdditionalFiles` and expands the complete, validated catalog into compiler-generated source before semantic compilation. The compiler consumes only the generated table and generic typed-value rules; runtime never reads catalog files or performs reflection binding.

## Communication model

XSR has four distinct primitives:

- Command: request an action. It is asynchronous and may be accepted before business completion.
- Query: request a one-time result. It is asynchronous, cancellable, and absent from render paths.
- State: represent a durable current fact. It is the renderer's primary input.
- Event: represent a transient fact that already happened. It never substitutes for current state.

Development identifiers may be readable strings. Source generation resolves them to compact, stable runtime IDs for hot paths. Reflection and string dispatch are not runtime routing mechanisms.

The initial Wave 1 registry treats semantic IDs as opaque, case-sensitive values. After registration closes, it assigns contiguous nonzero runtime IDs by sorting the complete semantic-ID set with ordinal comparison. The sealed mapping is immutable; numeric lookup, rather than string lookup, is the runtime hot path. Generated code may cache these resolved IDs without owning a second registry.

Command and query routes are registered through closed generic adapters and sealed before use. Commands separate immediate route acceptance from asynchronous handler completion, and every completion is observed even when the caller does not await it. Queries are asynchronous, cancellable, correlated, and may apply an explicit timeout. Runtime failures cross the router boundary as stable XSR error codes rather than leaked handler or transport exceptions.

The sealed registry is part of the XSR abstractions kernel: routing, state, events, transport, and generated code resolve the identical deterministic ID mapping. State entries declared through the store follow the same rule — typed cells and ordered collections carry a monotonic revision and availability separate from the last value, collection deltas apply only against a matching base revision, and derived entries recompute only after an input revision changes.

## Ownership and composition

- Services own business behavior and publish state/events.
- The state store owns observable system facts and their revisions.
- Renderers project state and emit intent without becoming a second source of truth.
- Platform projects own OS-specific implementations behind portable contracts.
- Desktop owns startup order and dependency composition, not business behavior.
- Sidecar owns plugin code execution and private plugin runtime state; Host owns the local mirror, UI module registry, permissions, and presentation continuity.

## Operability

The runtime must make `SessionId`, plugin identity, correlation ID, command/query ID, duration, queue depth, state coalescing, restarts, and activation time observable. Cancellation, backpressure, shutdown, crash recovery, and reconnect are correctness requirements, not later performance work. Wave 1 exposes these through bounded `XsrSessionTrace` diagnostics: every subsystem observation lands in one trace per session, correlation IDs are preserved wherever the subsystem contract carries them, and the trace observes behavior without changing it.
